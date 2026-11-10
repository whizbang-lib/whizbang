// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>The in-process tracker sweep runs in every host (#2228). The perspective sync tracker and the lifecycle
/// coordinator each hold an entry per message that leaves only on one path; when that path is not taken (the
/// perspective runs on another instance, the event is dead-lettered, a stage throws) the stale sweep is the entry's
/// only end. The lifecycle sweep used to run only from the perspective worker's batch loop, so a host whose perspective
/// worker processes no batches never swept, and the sync tracker's sweep had no caller at all.</para>
/// <para>Every wait is on a signal: the test advances the clock only once the worker's timer is armed, and waits for
/// the worker's own <see cref="InProcessTrackerSweepWorker.Swept"/> event.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/InProcessTrackerSweepWorker.cs</code-under-test>
public sealed class InProcessTrackerSweepWorkerTests {
  private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

  private sealed record SweptEvent(Guid Id) : IEvent;

  private static MessageEnvelope<SweptEvent> _envelope(Guid id) => new() {
    MessageId = MessageId.From(id),
    Payload = new SweptEvent(id),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task Tick_EndsStaleSyncAndLifecycleEntries_OnTheInjectedClockAsync() {
    using var timeout = new CancellationTokenSource(_testTimeout);
    var clock = new ArmingClock();
    var lifecycle = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance, timeProvider: clock);
    var sync = new SyncEventTracker(clock);
    Guid eventId = TrackedGuid.New();
    lifecycle.BeginTracking(LifecycleTrackingPath.Inbox, eventId, _envelope(eventId),
      LifecycleStage.PostLifecycleDetached, MessageSource.Inbox);
    sync.TrackEvent(typeof(SweptEvent), eventId, Guid.NewGuid(), "Orders.Summary");

    var worker = new InProcessTrackerSweepWorker(NullLogger<InProcessTrackerSweepWorker>.Instance, lifecycle, sync, clock);
    var swept = new TaskCompletionSource<InProcessTrackerSweepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.Swept += result => {
      if (result.LifecycleEntriesEnded + result.SyncEntriesRemoved > 0) {
        swept.TrySetResult(result);
      }
    };

    await worker.StartAsync(CancellationToken.None);
    try {
      // One advance past both bounds, made only once the worker's timer is armed. The periodic timer coalesces the
      // ticks it missed into one, and that sweep reads the clock at its final time.
      await clock.WaitForArmedAsync(InProcessTrackerSweepWorker.SweepInterval, timeout.Token);
      clock.Advance(InProcessTrackerSweepWorker.LifecycleInactivityThreshold
        + InProcessTrackerSweepWorker.SyncEntryMaxAge + InProcessTrackerSweepWorker.SweepInterval);
      var result = await swept.Task.WaitAsync(timeout.Token);

      await Assert.That(result.LifecycleEntriesEnded).IsEqualTo(1)
        .Because("a lifecycle entry nothing touched within the bound is ended by the host-wide sweep");
      await Assert.That(result.SyncEntriesRemoved).IsEqualTo(1)
        .Because("a sync entry no instance here applied within the bound is ended by the host-wide sweep");
      await Assert.That(lifecycle.TrackedCount).IsEqualTo(0);
      await Assert.That(sync.TrackedEntryCount).IsEqualTo(0);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task SweepOnce_OnTheSystemClock_WithNothingStale_EndsNothingAsync() {
    var worker = new InProcessTrackerSweepWorker(NullLogger<InProcessTrackerSweepWorker>.Instance,
      new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance), new SyncEventTracker());

    var result = worker.SweepOnce();

    await Assert.That(result).IsEqualTo(new InProcessTrackerSweepResult(0, 0));
  }

  [Test]
  public async Task SweepOnce_WhenATrackerThrows_LogsWhatFailed_AndStillSweepsTheOtherAsync() {
    var clock = new ArmingClock();
    var sync = new SyncEventTracker(clock);
    sync.TrackEvent(typeof(SweptEvent), Guid.NewGuid(), Guid.NewGuid(), "Orders.Summary");
    clock.Advance(InProcessTrackerSweepWorker.SyncEntryMaxAge);
    var logger = new CapturingLogger();
    var worker = new InProcessTrackerSweepWorker(logger, new ThrowingLifecycleCoordinator(), sync, clock);

    var result = worker.SweepOnce();

    await Assert.That(result.SyncEntriesRemoved).IsEqualTo(1)
      .Because("one tracker's failure must not stop the other's sweep");
    await Assert.That(logger.Warnings).Count().IsEqualTo(1);
    await Assert.That(logger.Warnings[0]).Contains("lifecycle");
  }

  [Test]
  public async Task Registration_HostsTheSweepInEveryWhizbangHost_WithBothTrackersSuppliedAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();

    await Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService)
                                          && d.ImplementationType == typeof(InProcessTrackerSweepWorker))).IsEqualTo(1)
      .Because("the sweep is the safety net for entries whose one removal path was not taken, so every host runs it");
    await Assert.That(services.Any(d => d.ServiceType == typeof(ILifecycleCoordinator))).IsTrue()
      .Because("the sweep requires the lifecycle coordinator, so the registration supplies its default");
    await Assert.That(services.Any(d => d.ServiceType == typeof(ISyncEventTracker))).IsTrue()
      .Because("the sweep requires the sync tracker, so the registration supplies its default");
  }

  [Test]
  public async Task SweepOnce_WhenTheSyncTrackerThrows_LogsWhatFailed_AndStillSweepsLifecycleAsync() {
    var clock = new ArmingClock();
    var lifecycle = new LifecycleCoordinator(NullLogger<LifecycleCoordinator>.Instance, timeProvider: clock);
    Guid eventId = TrackedGuid.New();
    lifecycle.BeginTracking(LifecycleTrackingPath.Inbox, eventId, _envelope(eventId),
      LifecycleStage.PostLifecycleDetached, MessageSource.Inbox);
    clock.Advance(InProcessTrackerSweepWorker.LifecycleInactivityThreshold + TimeSpan.FromSeconds(1));
    var logger = new CapturingLogger();
    var worker = new InProcessTrackerSweepWorker(logger, lifecycle, new ThrowingSyncEventTracker(), clock);

    var result = worker.SweepOnce();

    await Assert.That(result.LifecycleEntriesEnded).IsEqualTo(1);
    await Assert.That(logger.Warnings).Count().IsEqualTo(1);
    await Assert.That(logger.Warnings[0]).Contains("perspective sync");
  }

  private sealed class ThrowingSyncEventTracker : ISyncEventTracker {
    public void TrackEvent(Type eventType, Guid eventId, Guid streamId, string perspectiveName) { }
    public IReadOnlyList<TrackedSyncEvent> GetPendingEvents(Guid streamId, string perspectiveName, Type[]? eventTypes = null) => [];
    public void MarkProcessed(IEnumerable<Guid> eventIds) { }
    public IReadOnlyList<Guid> GetAllTrackedEventIds() => [];
    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null,
      CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void MarkProcessedByPerspective(IEnumerable<Guid> eventIds, string perspectiveName) { }
    public Task<bool> WaitForPerspectiveEventsAsync(IReadOnlyList<Guid> eventIds, string perspectiveName, TimeSpan timeout,
      Guid? awaiterId = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<bool> WaitForAllPerspectivesAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, Guid? awaiterId = null,
      CancellationToken cancellationToken = default) => Task.FromResult(true);
    public void UnregisterAwaiter(Guid awaiterId) { }
    public int CleanupStaleEntries(TimeSpan maxAge) => throw new InvalidOperationException("sweep failed");
    public void MarkPerspectiveStreamProcessed(string perspectiveName, Guid streamId) { }
  }

  private sealed class CapturingLogger : ILogger<InProcessTrackerSweepWorker> {
    public List<string> Warnings { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        Warnings.Add(formatter(state, exception));
      }
    }
  }

  private sealed class ThrowingLifecycleCoordinator : ILifecycleCoordinator {
    public ILifecycleTracking BeginTracking(LifecycleTrackingPath path, Guid eventId, IMessageEnvelope envelope,
      LifecycleStage entryStage, MessageSource source, Guid? streamId = null, Type? perspectiveType = null) =>
      throw new NotSupportedException();
    public ILifecycleTracking? GetTracking(LifecycleTrackingPath path, Guid eventId) => null;
    public void ExpectCompletionsFrom(LifecycleTrackingPath path, Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(LifecycleTrackingPath path, Guid eventId, PostLifecycleCompletionSource source,
      IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;
    public void AbandonTracking(LifecycleTrackingPath path, Guid eventId) { }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => false;
    public bool AreAllPerspectivesComplete(Guid eventId) => true;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) =>
      throw new InvalidOperationException("sweep failed");
  }
}
