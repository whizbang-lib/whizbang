using System;
using System.Collections.Concurrent;
using System.Linq;
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
  private readonly LifecycleCoordinatorMetrics? _metrics;

  /// <summary>
  /// Creates a new lifecycle coordinator with optional metrics.
  /// </summary>
  public LifecycleCoordinator(LifecycleCoordinatorMetrics? metrics = null) {
    _metrics = metrics;
  }

  /// <inheritdoc/>
  public ILifecycleTracking BeginTracking(
    Guid eventId,
    IMessageEnvelope envelope,
    LifecycleStage entryStage,
    MessageSource source,
    Guid? streamId = null,
    Type? perspectiveType = null) {
    var tracking = _tracked.GetOrAdd(eventId,
      id => {
        _metrics?.ActiveTrackedEvents.Add(1);
        return new LifecycleTrackingState(id, envelope, entryStage, source, streamId, perspectiveType);
      });
    return tracking;
  }

  /// <inheritdoc/>
  public ILifecycleTracking? GetTracking(Guid eventId) {
    return _tracked.TryGetValue(eventId, out var state) ? state : null;
  }

  /// <inheritdoc/>
  public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) {
    var whenAll = new WhenAllState(sources);
    if (_whenAllStates.TryAdd(eventId, whenAll)) {
      _metrics?.PendingWhenAllStates.Add(1);
    }
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
      _metrics?.PendingWhenAllStates.Add(-1);
    }

    // Fire PostLifecycle stages
    if (_tracked.TryGetValue(eventId, out var tracking)) {
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleAsync, scopedProvider, ct).ConfigureAwait(false);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, ct).ConfigureAwait(false);
      _metrics?.PostLifecycleFired.Add(1);
    }
  }

  /// <inheritdoc/>
  public void AbandonTracking(Guid eventId) {
    if (_tracked.TryRemove(eventId, out _)) {
      _metrics?.ActiveTrackedEvents.Add(-1);
    }
    if (_whenAllStates.TryRemove(eventId, out _)) {
      _metrics?.PendingWhenAllStates.Add(-1);
    }
    if (_perspectiveStates.TryRemove(eventId, out _)) {
      _metrics?.PendingPerspectiveStates.Add(-1);
    }
  }

  /// <inheritdoc/>
  public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) {
    if (_perspectiveStates.TryAdd(eventId, new PerspectiveWhenAllState(perspectiveNames))) {
      _metrics?.PendingPerspectiveStates.Add(1);
    }
  }

  /// <inheritdoc/>
  public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) {
    _metrics?.PerspectiveCompletionsSignaled.Add(1);
    // Reset inactivity timer — perspectives are still arriving, keep tracking alive
    if (_tracked.TryGetValue(eventId, out var tracking)) {
      tracking.TouchActivity();
    }
    if (!_perspectiveStates.TryGetValue(eventId, out var state)) {
      return false;
    }
    var allComplete = state.TrySignalAndCheck(perspectiveName);
    if (allComplete) {
      _metrics?.AllPerspectivesCompleted.Add(1);
      _metrics?.PendingPerspectiveStates.Add(-1);
    }
    return allComplete;
  }

  /// <inheritdoc/>
  public bool AreAllPerspectivesComplete(Guid eventId) {
    if (!_perspectiveStates.TryGetValue(eventId, out var state)) {
      _metrics?.ExpectationsNotRegistered.Add(1);
      return true; // No expectations registered = no WhenAll gate needed.
      // PostAllPerspectives/PostLifecycle are terminal stages that must always fire.
      // The WhenAll gate controls timing (wait for all to complete), not whether stages fire.
    }
    return state.IsComplete;
  }

  /// <inheritdoc/>
  public int CleanupStaleTracking(TimeSpan inactivityThreshold) {
    var cutoff = DateTimeOffset.UtcNow - inactivityThreshold;
    var cleaned = 0;

    foreach (var kvp in _tracked) {
      var state = kvp.Value;
      if (state.IsComplete || state.LastActivityUtc >= cutoff) {
        continue;
      }

      // Stale and incomplete — remove all associated state
      if (_tracked.TryRemove(kvp.Key, out _)) {
        _perspectiveStates.TryRemove(kvp.Key, out _);
        _whenAllStates.TryRemove(kvp.Key, out _);
        _metrics?.ActiveTrackedEvents.Add(-1);
        _metrics?.StaleTrackingCleaned.Add(1);
        cleaned++;
      }
    }

    return cleaned;
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
        if (!_expected.IsSubsetOf(_completed)) {
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

        if (_expected.Any(expected => !_completed.ContainsKey(expected))) {
          return false;
        }

        _fired = true;
        return true;
      }
    }
  }
}
