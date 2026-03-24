using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// Singleton coordinator that manages event lifecycle stage transitions.
/// Tracks live events, fires hooks once per stage, and manages the WhenAll pattern.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for lifecycle stage execution.
/// Workers call <see cref="BeginTracking"/> at entry points and
/// <see cref="AbandonTracking"/> at exit points. Between those,
/// <see cref="ILifecycleTracking.AdvanceToAsync"/> drives each stage transition.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs</tests>
public sealed partial class LifecycleCoordinator : ILifecycleCoordinator {
  private readonly ConcurrentDictionary<Guid, LifecycleTrackingState> _tracked = new();
  private readonly ConcurrentDictionary<Guid, WhenAllState> _whenAllStates = new();
  private readonly ConcurrentDictionary<Guid, PerspectiveWhenAllState> _perspectiveStates = new();

  /// <inheritdoc/>
  public ILifecycleTracking BeginTracking(
    Guid eventId,
    IMessageEnvelope envelope,
    LifecycleStage entryStage,
    MessageSource source,
    Guid? streamId = null,
    Type? perspectiveType = null) {
    return _tracked.GetOrAdd(eventId,
      _ => new LifecycleTrackingState(eventId, envelope, entryStage, source, streamId, perspectiveType));
  }

  /// <inheritdoc/>
  public ILifecycleTracking? GetTracking(Guid eventId) {
    return _tracked.TryGetValue(eventId, out var state) ? state : null;
  }

  /// <inheritdoc/>
  public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) {
    var whenAll = new WhenAllState(sources);
    _whenAllStates.TryAdd(eventId, whenAll);
  }

  /// <inheritdoc/>
  public async ValueTask SignalSegmentCompleteAsync(
    Guid eventId,
    PostLifecycleCompletionSource source,
    IServiceProvider scopedProvider,
    CancellationToken ct) {
    if (_whenAllStates.TryGetValue(eventId, out var whenAll)) {
      // Atomically signal and check completion — returns true exactly once
      if (!whenAll.TrySignalAndComplete(source)) {
        return; // Not all paths complete yet, or already fired
      }

      // All paths complete — remove WhenAll state and fire PostLifecycle
      _whenAllStates.TryRemove(eventId, out _);
    }

    // Fire PostLifecycle stages
    if (_tracked.TryGetValue(eventId, out var tracking)) {
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scopedProvider, ct).ConfigureAwait(false);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, ct).ConfigureAwait(false);
    }
  }

  /// <inheritdoc/>
  public void AbandonTracking(Guid eventId) {
    _tracked.TryRemove(eventId, out _);
    _whenAllStates.TryRemove(eventId, out _);
    _perspectiveStates.TryRemove(eventId, out _);
  }

  /// <inheritdoc/>
  public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) {
    _perspectiveStates.TryAdd(eventId, new PerspectiveWhenAllState(perspectiveNames));
  }

  /// <inheritdoc/>
  public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) {
    if (!_perspectiveStates.TryGetValue(eventId, out var state)) {
      return false;
    }
    return state.TrySignalAndCheck(perspectiveName);
  }

  /// <inheritdoc/>
  public bool AreAllPerspectivesComplete(Guid eventId) {
    if (!_perspectiveStates.TryGetValue(eventId, out var state)) {
      return false; // No expectations registered = fail-safe (don't fire PostLifecycle)
    }
    return state.IsComplete;
  }

  /// <summary>
  /// Tracks expected completions for the WhenAll pattern.
  /// Thread-safe — uses interlocked operations for completion tracking.
  /// </summary>
  /// <summary>
  /// Tracks expected perspective completions for per-event WhenAll.
  /// PostLifecycle fires only after all expected perspectives signal complete.
  /// Thread-safe with full state exposure for debugging/observers.
  /// </summary>
  private sealed class PerspectiveWhenAllState {
    private readonly HashSet<string> _expected;
    private readonly HashSet<string> _completed = [];
    private readonly Lock _lock = new();
    private bool _allComplete;

    public PerspectiveWhenAllState(IReadOnlyList<string> perspectiveNames) {
      _expected = [.. perspectiveNames];
    }

    /// <summary>Fast-path check for completion.</summary>
    public bool IsComplete {
      get { lock (_lock) { return _allComplete; } }
    }

    /// <summary>Perspectives expected to complete (inspectable for debugging).</summary>
    public IReadOnlySet<string> Expected {
      get { lock (_lock) { return new HashSet<string>(_expected); } }
    }

    /// <summary>Perspectives that have signaled complete (inspectable for debugging).</summary>
    public IReadOnlySet<string> Completed {
      get { lock (_lock) { return new HashSet<string>(_completed); } }
    }

    /// <summary>Perspectives still pending (inspectable for debugging).</summary>
    public IReadOnlyCollection<string> Pending {
      get { lock (_lock) { return _expected.Except(_completed).ToList(); } }
    }

    /// <summary>
    /// Signals a perspective as complete. Returns true exactly once — when all expected
    /// perspectives are complete. Subsequent calls always return false.
    /// </summary>
    public bool TrySignalAndCheck(string perspectiveName) {
      lock (_lock) {
        if (_allComplete) {
          return false;
        }
        _completed.Add(perspectiveName);
        if (!_expected.SetEquals(_completed)) {
          return false;
        }
        _allComplete = true;
        return true;
      }
    }
  }

  private sealed class WhenAllState {
    private readonly HashSet<PostLifecycleCompletionSource> _expected;
    private readonly ConcurrentDictionary<PostLifecycleCompletionSource, bool> _completed = new();
    private readonly Lock _lock = new();
    private bool _fired;

    public WhenAllState(PostLifecycleCompletionSource[] sources) {
      _expected = [.. sources];
    }

    /// <summary>
    /// Atomically signals a source as complete and returns <c>true</c> exactly once —
    /// when all expected sources are complete. Subsequent calls always return <c>false</c>.
    /// </summary>
    public bool TrySignalAndComplete(PostLifecycleCompletionSource source) {
      _completed.TryAdd(source, true);

      lock (_lock) {
        if (_fired) {
          return false;
        }

        foreach (var expected in _expected) {
          if (!_completed.ContainsKey(expected)) {
            return false;
          }
        }

        _fired = true;
        return true;
      }
    }
  }
}
