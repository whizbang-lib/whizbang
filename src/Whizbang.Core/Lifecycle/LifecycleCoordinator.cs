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

  /// <inheritdoc/>
  public ILifecycleTracking BeginTracking(
    Guid eventId,
    IMessageEnvelope envelope,
    LifecycleStage entryStage,
    MessageSource source,
    Guid? streamId = null,
    Type? perspectiveType = null) {
    var state = new LifecycleTrackingState(eventId, envelope, entryStage, source, streamId, perspectiveType);
    _tracked.TryAdd(eventId, state);
    return state;
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
  }

  /// <summary>
  /// Tracks expected completions for the WhenAll pattern.
  /// Thread-safe — uses interlocked operations for completion tracking.
  /// </summary>
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
