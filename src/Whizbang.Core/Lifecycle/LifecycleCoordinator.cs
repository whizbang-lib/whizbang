// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <para>
/// Every entry has a defined end: the path that begins tracking ends it once the lifecycle it tracks is
/// done. <see cref="CleanupStaleTracking"/> is a safety net, not what keeps memory stable (#1797).
/// </para>
/// <para>
/// Entries are keyed by <see cref="LifecycleTrackingPath"/> and event id, so an event on several paths in one
/// process (dispatched locally, projected, published) has one entry per path, each with its own stage guard and its
/// own end. The perspective WhenAll gate belongs to the <see cref="LifecycleTrackingPath.Perspective"/> entry and ends
/// only with it (#1918).
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Lifecycle/LifecycleCoordinatorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs</tests>
public sealed partial class LifecycleCoordinator : ILifecycleCoordinator {
  private readonly ConcurrentDictionary<TrackingKey, LifecycleTrackingState> _tracked = new();
  private readonly ConcurrentDictionary<TrackingKey, WhenAllState> _whenAllStates = new();
  private readonly ConcurrentDictionary<Guid, PerspectiveWhenAllState> _perspectiveStates = new();
  private readonly ConcurrentDictionary<Task, byte> _endedDetachedTasks = new();
  private readonly LifecycleCoordinatorMetrics? _metrics;
  private readonly ILogger<LifecycleCoordinator> _logger;
  private readonly TimeProvider _timeProvider;

  /// <summary>Initializes a new instance of the <see cref="LifecycleCoordinator"/> class.</summary>
  /// <param name="logger">Logs detached-stage failures and partial entries the sweep ends.</param>
  /// <param name="metrics">Lifecycle metrics; when given, the coordinator publishes its live tracked count.</param>
  /// <param name="timeProvider">The clock the inactivity sweep reads; the system clock when omitted.</param>
  /// <docs>fundamentals/lifecycle/lifecycle-coordinator#stale-tracking-cleanup</docs>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs:Sweep_KeepsAnUnreportedEntry_UntilTheInjectedClockPassesTheBoundAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs:TrackedEntriesGauge_ReportsTheLiveTrackedCountAsync</tests>
  public LifecycleCoordinator(
      ILogger<LifecycleCoordinator> logger,
      LifecycleCoordinatorMetrics? metrics = null,
      TimeProvider? timeProvider = null) {
    _logger = logger;
    _metrics = metrics;
    _timeProvider = timeProvider ?? TimeProvider.System;
    metrics?.ObserveTrackedEntries(() => _tracked.Count);
  }

  /// <summary>The number of entries tracked right now: what the tracked-entries gauge reports.</summary>
  internal int TrackedCount => _tracked.Count;

  /// <summary>Detached stages of ended entries that are still running, held so a drain can await them.</summary>
  internal int RetainedDetachedTaskCount => _endedDetachedTasks.Count;

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingPathTests.cs:EachPathTracksTheSameEventInAnEntryOfItsOwnAsync</tests>
  public ILifecycleTracking BeginTracking(
    LifecycleTrackingPath path,
    Guid eventId,
    IMessageEnvelope envelope,
    LifecycleStage entryStage,
    MessageSource source,
    Guid? streamId = null,
    Type? perspectiveType = null) {
    return _tracked.GetOrAdd(new TrackingKey(path, eventId),
      key => {
        _metrics?.ActiveTrackedEvents.Add(1);
        return new LifecycleTrackingState(key.EventId, envelope, entryStage, source, streamId, perspectiveType, _logger) { Clock = _timeProvider };
      });
  }

  /// <inheritdoc/>
  public ILifecycleTracking? GetTracking(LifecycleTrackingPath path, Guid eventId) {
    return _tracked.TryGetValue(new TrackingKey(path, eventId), out var state) ? state : null;
  }

  /// <inheritdoc/>
  public void ExpectCompletionsFrom(LifecycleTrackingPath path, Guid eventId, params PostLifecycleCompletionSource[] sources) {
    var whenAll = new WhenAllState(sources);
    if (_whenAllStates.TryAdd(new TrackingKey(path, eventId), whenAll)) {
      _metrics?.PendingWhenAllStates.Add(1);
    }
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingPathTests.cs:SegmentLatch_FiresPostLifecycleOnItsOwnPathsTracking_AndEndsWithItAsync</tests>
  public async ValueTask SignalSegmentCompleteAsync(
    LifecycleTrackingPath path,
    Guid eventId,
    PostLifecycleCompletionSource source,
    IServiceProvider scopedProvider,
    CancellationToken ct) {
    var key = new TrackingKey(path, eventId);
    if (_whenAllStates.TryGetValue(key, out var whenAll)) {
      // Atomically signal and check completion — returns true exactly once
      if (!whenAll.TrySignalAndComplete(source)) {
        return; // Not all paths complete yet, or already fired
      }

      // All paths complete — remove WhenAll state and fire PostLifecycle
      _whenAllStates.TryRemove(key, out _);
      _metrics?.PendingWhenAllStates.Add(-1);
    }

    // Fire PostLifecycle stages
    if (_tracked.TryGetValue(key, out var tracking)) {
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scopedProvider, ct).ConfigureAwait(false);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scopedProvider, ct).ConfigureAwait(false);
      _metrics?.PostLifecycleFired.Add(1);
    }
  }

  /// <summary>
  /// Waits for all in-flight detached tasks across all tracked events to complete.
  /// Used for graceful shutdown and testing.
  /// </summary>
  internal async ValueTask DrainAllDetachedAsync() {
    var activeTasks = _tracked.Values.SelectMany(t => t.GetDetachedTasks());
    var allTasks = activeTasks.Concat(_endedDetachedTasks.Keys).ToArray();
    await Task.WhenAll(allTasks).ConfigureAwait(false);
    foreach (var task in allTasks) {
      _endedDetachedTasks.TryRemove(task, out _);
    }
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingPathTests.cs:EndingOnePathsTracking_NeverEndsAnotherPathsTrackingOfTheSameEventAsync</tests>
  public void AbandonTracking(LifecycleTrackingPath path, Guid eventId) {
    var key = new TrackingKey(path, eventId);
    if (_tracked.TryGetValue(key, out var state)) {
      _end(key, state);
    }
    _releaseGates(key);
  }

  /// <summary>
  /// Drops the WhenAll states one entry owns: its segment latch, and, for the perspective path's entry, the event's
  /// perspective gate. Another path's end never drops the perspective gate, which would open it with perspectives
  /// still to report (#1918). A perspective state that already completed was taken off the pending gauge when it
  /// completed, so only an incomplete one is taken off here; the gauge never goes below the states actually waiting.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingPathTests.cs:EndingAnotherPathsTracking_LeavesThePerspectiveGateClosedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs:EndingACompletedEvent_TakesItsPerspectiveStateOffThePendingGaugeOnlyOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs:Sweep_EndingAnEntry_ReleasesItsSegmentAndPerspectiveGatesAsync</tests>
  private void _releaseGates(TrackingKey key) {
    if (_whenAllStates.TryRemove(key, out _)) {
      _metrics?.PendingWhenAllStates.Add(-1);
    }
    if (key.Path == LifecycleTrackingPath.Perspective
        && _perspectiveStates.TryRemove(key.EventId, out var perspectiveState) && !perspectiveState.IsComplete) {
      _metrics?.PendingPerspectiveStates.Add(-1);
    }
  }

  /// <summary>
  /// Ends one tracked entry: removes it only when it is still the entry given (a new entry for the same event
  /// is never taken by an old end) and keeps its still-running detached stages until each finishes, so a drain
  /// can await them without the coordinator holding finished ones (#1797).
  /// </summary>
  /// <returns><see langword="true"/> when this call removed the entry.</returns>
  /// <tests>tests/Whizbang.Core.Component.Tests/Lifecycle/LifecycleCoordinatorDetachedRetentionTests.cs:EndedTracking_ReleasesItsDetachedStage_OnceTheStageFinishesAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Lifecycle/LifecycleCoordinatorDetachedRetentionTests.cs:EndedTracking_NeverRetainsADetachedStageThatAlreadyFinishedAsync</tests>
  private bool _end(TrackingKey key, LifecycleTrackingState state) {
    if (!_tracked.TryRemove(new KeyValuePair<TrackingKey, LifecycleTrackingState>(key, state))) {
      return false;
    }
    foreach (var task in state.GetDetachedTasks().Where(t => !t.IsCompleted)) {
      _endedDetachedTasks.TryAdd(task, 0);
      _ = task.ContinueWith(
        static (finished, retained) => ((ConcurrentDictionary<Task, byte>)retained!).TryRemove(finished, out _),
        _endedDetachedTasks, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    _metrics?.ActiveTrackedEvents.Add(-1);
    return true;
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
    // Reset inactivity timer: perspectives are still arriving, keep the perspective path's tracking alive
    if (_tracked.TryGetValue(new TrackingKey(LifecycleTrackingPath.Perspective, eventId), out var tracking)) {
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
  /// <remarks>
  /// A safety net on the injected clock, never what keeps memory stable: a completed entry is removed whatever its
  /// age (counted, since it means a path skipped its exit); an entry nothing has touched within
  /// <paramref name="inactivityThreshold"/> is ended, and when some of its expected perspectives reported and
  /// others never did, the end is counted and logged with the perspectives that never reported (#1797).
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs:Sweep_EndsAPartialEntry_OnceNoPerspectiveHasReportedForTheBound_NamingTheMissingPerspectivesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs:Sweep_KeepsAPartialEntry_WhilePerspectivesKeepReportingWithinTheBoundAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingEndsTests.cs:Sweep_NeverRetainsACompletedEntry_HoweverRecentAsync</tests>
  public int CleanupStaleTracking(TimeSpan inactivityThreshold) {
    var cutoff = _timeProvider.GetUtcNow() - inactivityThreshold;
    var cleaned = 0;

    // A snapshot: an entry another path ends while the sweep runs is skipped below, never counted twice.
    foreach (var (key, state) in _tracked.ToArray()) {
      var complete = state.IsComplete;
      if (!complete && state.LastActivityUtc >= cutoff) {
        continue;
      }
      var perspectiveState = _perspectiveGateOf(key);
      if (!_end(key, state)) {
        continue;
      }
      _releaseGates(key);
      cleaned++;
      if (complete) {
        _metrics?.CompletedTrackingSwept.Add(1);
        continue;
      }
      _metrics?.StaleTrackingCleaned.Add(1);
      if (perspectiveState is { HasPartialCompletions: true }) {
        _metrics?.PartialTrackingEnded.Add(1);
        if (_logger.IsEnabled(LogLevel.Debug)) {
          LogPartialTrackingEnded(_logger, key.EventId, inactivityThreshold, perspectiveState.MissingPerspectives);
        }
      }
    }

    return cleaned;
  }

  /// <summary>The perspective gate an entry owns: the event's, for the perspective path's entry; none for another path's.</summary>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingPathTests.cs:Sweep_EndingAnotherPathsStaleEntry_LeavesThePerspectiveGateClosedAsync</tests>
  private PerspectiveWhenAllState? _perspectiveGateOf(TrackingKey key) =>
    key.Path == LifecycleTrackingPath.Perspective && _perspectiveStates.TryGetValue(key.EventId, out var state) ? state : null;

  /// <summary>One path's entry for one event: the key every tracked entry and segment latch is held under (#1918).</summary>
  private readonly record struct TrackingKey(LifecycleTrackingPath Path, Guid EventId);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Lifecycle tracking for event {EventId} ended after {Inactivity} with no report; PostAllPerspectives will not fire in this process. Perspectives that never reported: {MissingPerspectives}"
  )]
  private static partial void LogPartialTrackingEnded(ILogger logger, Guid eventId, TimeSpan inactivity, string[] missingPerspectives);

  /// <summary>
  /// Tracks expected perspective completions for per-event WhenAll.
  /// PostLifecycle fires only after all expected perspectives signal complete.
  /// Thread-safe with full state exposure for debugging/observers.
  /// </summary>
  private sealed class PerspectiveWhenAllState(IReadOnlyList<string> perspectiveNames) {
    private readonly HashSet<string> _expected = [.. perspectiveNames];
    private readonly HashSet<string> _completed = [];
    private readonly Lock _lock = new();
    private bool _allComplete;

    /// <summary>Fast-path check for completion.</summary>
    public bool IsComplete {
      get { lock (_lock) { return _allComplete; } }
    }

    /// <summary>
    /// Returns true if at least one (but not all) expected perspectives have completed.
    /// Used by cleanup to avoid destroying in-progress cross-batch tracking.
    /// </summary>
    public bool HasPartialCompletions {
      get { lock (_lock) { return !_allComplete && _completed.Count > 0; } }
    }

    /// <summary>The expected perspectives that have not reported, in name order.</summary>
    public string[] MissingPerspectives {
      get { lock (_lock) { return [.. _expected.Except(_completed).Order(StringComparer.Ordinal)]; } }
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

  /// <summary>
  /// The fan-in latch for one event's post-lifecycle segments: fires exactly once, when every
  /// expected source has signaled.
  /// </summary>
  /// <remarks>
  /// Internal rather than private so the already-fired answer can be asserted directly.
  /// <see cref="SignalSegmentCompleteAsync"/> removes the state from the dictionary the moment it
  /// fires, so a later sequential signal never finds this object again — the only way a second
  /// call reaches a fired latch is two segments completing concurrently, which is exactly what the
  /// flag is for and exactly what a test cannot schedule. Getting it wrong fires PostLifecycle
  /// twice for one event.
  /// </remarks>
  internal sealed class WhenAllState(PostLifecycleCompletionSource[] sources) {
    private readonly HashSet<PostLifecycleCompletionSource> _expected = [.. sources];
    private readonly ConcurrentDictionary<PostLifecycleCompletionSource, bool> _completed = new();
    private readonly Lock _lock = new();
    private bool _fired;

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
