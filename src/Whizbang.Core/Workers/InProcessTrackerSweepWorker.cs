// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Workers;

/// <summary>
/// What one pass of the <see cref="InProcessTrackerSweepWorker"/> ended.
/// </summary>
/// <param name="LifecycleEntriesEnded">Lifecycle tracking entries the pass ended.</param>
/// <param name="SyncEntriesRemoved">Perspective sync entries the pass removed.</param>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#stale-tracking-cleanup</docs>
public readonly record struct InProcessTrackerSweepResult(int LifecycleEntriesEnded, int SyncEntriesRemoved);

/// <summary>
/// Sweeps the in-process trackers that hold an entry per message and remove it on one path only: lifecycle
/// tracking in <see cref="ILifecycleCoordinator"/> and the perspective sync entries in
/// <see cref="ISyncEventTracker"/> (#2228).
/// </summary>
/// <remarks>
/// <para>
/// Both trackers end their entries where the work they track ends, so this sweep is a safety net, never what keeps
/// memory stable. It exists for the entries whose one removal path is not taken: a sync entry for an event whose
/// perspective runs on another instance, or that fails or is dead-lettered; a lifecycle entry whose path stopped
/// without ending it. Such an entry used to stay for the life of the process: the lifecycle sweep ran only from the
/// perspective worker's batch loop, so a host whose perspective worker processes no batches never swept it, and the
/// sync tracker's sweep had no caller.
/// </para>
/// <para>
/// Every host runs it: <c>AddWhizbangWorkers</c> registers it, and it does no database work, so it starts with the
/// host, which registers both trackers' defaults with <c>TryAdd</c>. It reads the injected <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#stale-tracking-cleanup</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/InProcessTrackerSweepWorkerTests.cs</tests>
/// <param name="logger">Logs a sweep that ended entries and one that failed.</param>
/// <param name="lifecycleCoordinator">The lifecycle coordinator to sweep.</param>
/// <param name="syncEventTracker">The perspective sync tracker to sweep.</param>
/// <param name="timeProvider">The clock the sweep ticks on; the system clock when omitted.</param>
public sealed partial class InProcessTrackerSweepWorker(
    ILogger<InProcessTrackerSweepWorker> logger,
    ILifecycleCoordinator lifecycleCoordinator,
    ISyncEventTracker syncEventTracker,
    TimeProvider? timeProvider = null) : BackgroundService {
  /// <summary>How often the sweep runs.</summary>
  public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

  /// <summary>A lifecycle entry nothing has touched for this long is ended (the bound the perspective worker uses).</summary>
  public static readonly TimeSpan LifecycleInactivityThreshold = TimeSpan.FromMinutes(5);

  /// <summary>
  /// A sync entry older than this is removed and its waiters released. Far above any sync wait's timeout
  /// (seconds by default), so no live wait is released early.
  /// </summary>
  public static readonly TimeSpan SyncEntryMaxAge = TimeSpan.FromMinutes(5);

  private readonly ILifecycleCoordinator _lifecycleCoordinator = ArgumentGuard.NotNull(lifecycleCoordinator);
  private readonly ISyncEventTracker _syncEventTracker = ArgumentGuard.NotNull(syncEventTracker);
  private readonly ILogger<InProcessTrackerSweepWorker> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <summary>
  /// Raised after every sweep pass with what it ended. A signal for monitoring, and for a test to wait on the pass
  /// rather than on time.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/InProcessTrackerSweepWorkerTests.cs:Tick_EndsStaleSyncAndLifecycleEntries_OnTheInjectedClockAsync</tests>
  public event Action<InProcessTrackerSweepResult> Swept = static _ => { };

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    using var timer = new PeriodicTimer(SweepInterval, _timeProvider);
    try {
      // Ends only when the host stops: the timer is never disposed while the loop runs, so a tick is never false.
      while (true) {
        _ = await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        Swept(SweepOnce());
      }
    } catch (OperationCanceledException) {
      // Host shutdown ends the loop; it is not a sweep failure.
    }
  }

  /// <summary>
  /// Runs one pass over both trackers. A tracker that throws is logged by name and does not stop the other's pass.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/InProcessTrackerSweepWorkerTests.cs:SweepOnce_WhenATrackerThrows_LogsWhatFailed_AndStillSweepsTheOtherAsync</tests>
  internal InProcessTrackerSweepResult SweepOnce() {
    var lifecycleEnded = 0;
    var syncRemoved = 0;
    try {
      lifecycleEnded = _lifecycleCoordinator.CleanupStaleTracking(LifecycleInactivityThreshold);
    } catch (Exception ex) {
      LogSweepFailed(_logger, ex, "lifecycle tracking");
    }
    try {
      syncRemoved = _syncEventTracker.CleanupStaleEntries(SyncEntryMaxAge);
    } catch (Exception ex) {
      LogSweepFailed(_logger, ex, "perspective sync tracking");
    }
    if (lifecycleEnded + syncRemoved > 0) {
      LogSwept(_logger, lifecycleEnded, syncRemoved);
    }
    return new InProcessTrackerSweepResult(lifecycleEnded, syncRemoved);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "In-process tracker sweep ended {LifecycleEntries} lifecycle tracking entries and {SyncEntries} perspective sync entries that no path removed")]
  static partial void LogSwept(ILogger logger, int lifecycleEntries, int syncEntries);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "In-process tracker sweep of {Tracker} failed; the other tracker's sweep still ran")]
  static partial void LogSweepFailed(ILogger logger, Exception ex, string tracker);
}
