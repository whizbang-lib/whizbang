// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The worker's set-based path (#2246), on both of its sources. The pairs of a batch whose perspective writes set-based
/// are taken under their affinity gates and apply locks in one order, prepared together, written with one call to their
/// context's sink, and completed with their own outcomes; each completion is then reported exactly as on the per-pair
/// path. A pair the batch hands back runs the per-pair path, and a service in the per-stream mode never prepares.
/// </summary>
/// <remarks>
/// Every stream is queued before the worker starts, so its first batch holds them all; each wait is on the completion
/// reports the assertions read.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#the-batch</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.DrainGroups.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.ClaimedGroups.cs</code-under-test>
public partial class PerspectiveWorkerDrainModeTests {
  private const string SET_BASED = "Tests.SetBased.Orders";
  private const string PER_STREAM = "Tests.SetBased.Ledger";
  private const string NO_RUNNER = "Tests.SetBased.Unresolvable";
  private const string SINK_KEY = "orders-context";

  static PerspectiveWorkerDrainModeTests() {
    // A generated runner registers its perspective's write mode when its assembly loads; these tests' runners are
    // fakes, so they register the way generated ones do.
    PerspectiveWriteModes.Register(SET_BASED, null);
    PerspectiveWriteModes.Register(PER_STREAM, PerspectiveWriteMode.PerStream);
    PerspectiveWriteModes.Register(NO_RUNNER, null);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_APerStreamPerspectiveOfTheSameStreams_RunsPerPairBesideTheBatchAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { AlsoPerStream = true };
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.DrainExpectingAsync(streams, expectedReports: 4, expectedFailures: 0, cancellationToken);

    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams)
      .Because("the set-based perspective's rows are written by the batch");
    await Assert.That(rig.Runners.RunWithEventsCalls).IsEqualTo(2)
      .Because("the perspective that declares the per-stream mode is applied per pair, in the same drain");
    await Assert.That(rig.Coordinator.Reported.Count(c => c.PerspectiveName == PER_STREAM)).IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AnInversion_IsRewoundPerPair_AndTheOtherPairsAreBatchedAsync(CancellationToken cancellationToken) {
    var inverted = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { CommitSequenceAheadOf = inverted };

    await rig.DrainAsync([inverted, healthy], cancellationToken);

    await Assert.That(rig.Runners.Rewound).IsEquivalentTo(new[] { inverted })
      .Because("an event at or below the cursor rewinds its stream, as the per-pair path does");
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { healthy });
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(new[] { inverted, healthy });
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AFinishThatThrows_FailsOnlyItsPairAsync(CancellationToken cancellationToken) {
    var broken = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { ReportThrowsFor = broken };

    await rig.DrainExpectingAsync([broken, healthy], expectedReports: 1, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo(new[] { broken });
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(new[] { healthy });
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_ABatchThatOutlivesItsLease_FailsEveryPairOfTheBatchAsync(CancellationToken cancellationToken) {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { OutliveLease = true };

    await rig.DrainExpectingAsync(streams, expectedReports: 0, expectedFailures: 2, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Coordinator.Failed.All(f => f.Error!.Contains("Lease deadline exceeded", StringComparison.Ordinal))).IsTrue()
      .Because("a batch whose lease ran out is routed to the failure path, as one pair's would be, so its rows are re-issued");
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ARewindTheCursorAsksFor_RunsPerPair_AndTheOtherPairsAreBatchedAsync(CancellationToken cancellationToken) {
    var rewound = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { RewindRequiredFor = rewound };

    await rig.ClaimAsync([rewound, healthy], cancellationToken);

    await Assert.That(rig.Runners.Rewound).IsEquivalentTo(new[] { rewound });
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { healthy });
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(new[] { rewound, healthy });
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ARunnerThatDoesNotExposeItsRead_RunsPerPairWithRunAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { ReadsNothing = true };
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.ClaimAsync(streams, cancellationToken);

    await Assert.That(rig.Runners.RunCalls).IsEqualTo(2);
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_AFailedPrepareAndAFailedFinish_FailOnlyTheirPairsAsync(CancellationToken cancellationToken) {
    var badPrepare = Guid.NewGuid();
    var badFinish = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { FailPrepareOf = badPrepare, ReportThrowsFor = badFinish };

    await rig.ClaimExpectingAsync([badPrepare, badFinish, healthy], expectedReports: 1, expectedFailures: 2, cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo(new[] { badPrepare, badFinish });
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(new[] { healthy });
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_APerStreamPairAndAnUnresolvableRunner_RunBesideTheBatchAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { AlsoPerStream = true, AlsoUnresolvable = true };
    var streams = new[] { Guid.NewGuid() };

    await rig.ClaimExpectingAsync(streams, expectedReports: 2, expectedFailures: 0, cancellationToken);

    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.RunCalls).IsEqualTo(1)
      .Because("the per-stream perspective's claimed pair runs RunAsync, as before set-based writes");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.PerspectiveName)).IsEquivalentTo(new[] { SET_BASED, PER_STREAM })
      .Because("a perspective whose runner cannot be resolved ends without a report, as on the per-pair path");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_PreparesEveryPair_WritesThemWithOneSinkCall_AndReportsEachCompletionAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased);
    var streams = new[] { Guid.Parse("00000000-0000-0000-0000-00000000000b"), Guid.Parse("00000000-0000-0000-0000-00000000000a") };

    await rig.DrainAsync(streams, cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(1).Because("the batch's rows are written with one call to their context's sink");
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.Prepared.Select(p => p.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.Completed.Values).All().Satisfy(o => o.IsEqualTo(PerspectiveWriteStatus.Written));
    await Assert.That(rig.Runners.RunWithEventsCalls).IsEqualTo(0);
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams)
      .Because("each pair's completion is reported as the per-pair path reports it");
    await Assert.That(rig.Locks.Acquired).IsEquivalentTo(streams.Order().ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the apply locks are taken in one fixed order");
    await Assert.That(rig.GatesReleased.Select(r => r.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.StateSource.Planned!.Select(p => p.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.StateSource.Planned!.All(p => p.KnownNew)).IsTrue()
      .Because("no cursor, and each batch starts its stream: the state source plans no read for either");
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_APairHandedBack_RunsThePerPairPath_AndWritesNothingAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { HandBack = true };
    var stream = Guid.NewGuid();

    await rig.DrainAsync([stream], cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(rig.Runners.Completed.ContainsKey(stream)).IsTrue()
      .Because("the runner completes a pair it handed back, which runs it per pair under the batch's lock");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(new[] { stream });
  }

  [Test]
  [Timeout(30000)]
  public async Task PerStreamMode_NeverPrepares_AndAppliesEachPairWithRunWithEventsAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.PerStream);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.DrainAsync(streams, cancellationToken);

    await Assert.That(rig.Runners.Prepared).IsEmpty();
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(rig.Runners.RunWithEventsCalls).IsEqualTo(2).Because("the per-stream mode is the path before set-based writes");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedClaimed_ReadsEachPairsEvents_AndWritesThemWithOneSinkCallAsync(CancellationToken cancellationToken) {
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    await rig.ClaimAsync(streams, cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(1);
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(streams);
    await Assert.That(rig.Runners.EventsRead).IsEquivalentTo(streams)
      .Because("a claimed pair's events are read with the runner's own read, which RunAsync makes");
    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(streams);
  }

  [Test]
  [Timeout(30000)]
  public async Task SetBasedDrain_AFailedPrepare_FailsOnlyItsPairAsync(CancellationToken cancellationToken) {
    var broken = Guid.NewGuid();
    var healthy = Guid.NewGuid();
    var rig = new SetBasedRig(PerspectiveWriteMode.SetBased) { FailPrepareOf = broken };

    await rig.DrainExpectingAsync([broken, healthy], expectedReports: 1, expectedFailures: 1, cancellationToken);

    await Assert.That(rig.Coordinator.Reported.Select(c => c.StreamId)).IsEquivalentTo(new[] { healthy });
    await Assert.That(rig.Coordinator.Failed.Select(f => f.StreamId)).IsEquivalentTo(new[] { broken })
      .Because("a pair whose apply threw is failed as the per-pair path fails it; its batch's other pairs are written");
    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { healthy });
  }

  /// <summary>A worker over fakes, with the set-based seams registered and one fake runner for <see cref="SET_BASED"/>.</summary>
  private sealed class SetBasedRig(PerspectiveWriteMode mode) {
    public bool HandBack { get; init; }
    public Guid? FailPrepareOf { get; init; }
    public Guid? ReportThrowsFor { get; init; }
    public Guid? CommitSequenceAheadOf { get; init; }
    public Guid? RewindRequiredFor { get; init; }
    public bool ReadsNothing { get; init; }
    public bool AlsoPerStream { get; init; }
    public bool AlsoUnresolvable { get; init; }
    public bool OutliveLease { get; init; }
    public RecordingSink Sink { get; } = new();
    public RecordingLocks Locks { get; } = new();
    public PlanningStateSource StateSource { get; } = new();
    public SetBasedRunners Runners { get; private set; } = null!;
    public ReportingCoordinator Coordinator { get; } = new();
    public ConcurrentQueue<PerspectiveWorker.AffinityGateRelease> GatesReleased { get; } = new();

    public Task DrainAsync(Guid[] streams, CancellationToken ct) =>
      _runAsync(streams, drain: true, streams.Length, 0, ct);

    public Task DrainExpectingAsync(Guid[] streams, int expectedReports, int expectedFailures, CancellationToken ct) =>
      _runAsync(streams, drain: true, expectedReports, expectedFailures, ct);

    public Task ClaimAsync(Guid[] streams, CancellationToken ct) => _runAsync(streams, drain: false, streams.Length, 0, ct);

    public Task ClaimExpectingAsync(Guid[] streams, int expectedReports, int expectedFailures, CancellationToken ct) =>
      _runAsync(streams, drain: false, expectedReports, expectedFailures, ct);

    private async Task _runAsync(Guid[] streams, bool drain, int expectedReports, int expectedFailures, CancellationToken ct) {
      var events = streams.ToDictionary(s => s, s => new MessageEnvelope<IEvent> {
        MessageId = new MessageId(Guid.CreateVersion7()),
        Payload = new DrainModeTestEvent($"set-based-{s}"),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        IsFirstInLocalStream = true,
      });
      var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
      Runners = new SetBasedRunners(events) {
        HandBack = HandBack,
        FailPrepareOf = FailPrepareOf,
        ReadsNothing = ReadsNothing,
        AlsoPerStream = AlsoPerStream,
        OnPrepare = OutliveLease ? () => time.Advance(TimeSpan.FromHours(1)) : null,
      };
      Coordinator.Expect(expectedReports, expectedFailures);
      Coordinator.ReportThrowsFor = ReportThrowsFor;
      if (CommitSequenceAheadOf is { } ahead) {
        Coordinator.Cursors[ahead] = new PerspectiveCursorInfo { StreamId = ahead, PerspectiveName = SET_BASED, LastEventId = Guid.CreateVersion7(), LastCommitSequence = 100 };
      }
      if (RewindRequiredFor is { } rewind) {
        Coordinator.Cursors[rewind] = new PerspectiveCursorInfo {
          StreamId = rewind,
          PerspectiveName = SET_BASED,
          LastEventId = Guid.CreateVersion7(),
          Status = PerspectiveProcessingStatus.RewindRequired,
          RewindTriggerEventId = events[rewind].MessageId.Value,
        };
      }
      string[] perspectives = AlsoPerStream ? [SET_BASED, PER_STREAM] : [SET_BASED];
      Coordinator.StreamEventsToReturn = [.. streams.SelectMany(s => perspectives.Select(p => new StreamEventData {
        StreamId = s,
        EventId = events[s].MessageId.Value,
        EventType = TypeNameFormatter.Format(typeof(DrainModeTestEvent)),
        EventData = JsonSerializer.Serialize(new DrainModeTestEvent("set-based")),
        Metadata = null,
        Scope = null,
        EventWorkId = Guid.NewGuid(),
        PerspectiveName = p,
        CommitSequence = s == CommitSequenceAheadOf ? 50 : null,
      }))];
      var eventStore = new DrainModeEventStore { DeserializedEventsToReturn = [.. events.Values] };
      var eventTypeProvider = new FakeEventTypeProvider([typeof(DrainModeTestEvent)]);
      var instanceProvider = new FakeServiceInstanceProvider();
      var harness = new PerspectiveWorkerTestHarness();

      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<IWorkCoordinator>(Coordinator);
      services.AddSingleton<IPerspectiveRunnerRegistry>(Runners);
      services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
      services.AddSingleton<IEventStore>(eventStore);
      services.AddSingleton<IEventTypeProvider>(eventTypeProvider);
      services.AddSingleton<IPerspectiveApplyCoordinator>(Locks);
      services.AddSingleton<IPerspectiveStateSource>(StateSource);
      services.AddKeyedSingleton<IPerspectiveWriteSink>(SINK_KEY, Sink);
      services.AddLogging();
      var provider = services.BuildServiceProvider();

      var options = new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxConcurrentDrainConsumers = 1,
        WriteMode = mode,
        // Every stream is queued before the worker starts, so the drain's coalescing window has nothing to wait for;
        // closed at once, it never waits on the test's fake clock.
        DrainBatcher = new SlidingWindowBatcherOptions { SlidingWindow = TimeSpan.Zero, MaxWait = TimeSpan.Zero, MaxSize = 1000 },
      };
      var worker = new PerspectiveWorker(
        instanceProvider: instanceProvider,
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(options),
        tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
        completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
        eventTypeProvider: eventTypeProvider,
        syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
        syncEventTracker: new SyncEventTracker(),
        logger: NullLogger<PerspectiveWorker>.Instance,
        snapshotStore: NullPerspectiveSnapshotStore.Instance,
        streamLocker: NullPerspectiveStreamLocker.Instance,
        streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
        streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
        processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
        workChannelWriter: new WorkChannelWriter(),
        rewindOptions: Options.Create(new PerspectiveRewindOptions()),
        perspectiveChannelWriter: harness.ChannelWriter,
        perspectiveCompletionChannel: harness.CompletionCapture,
        failureChannel: harness.FailureCapture,
        leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
        perspectiveDrainChannel: harness.DrainChannel,
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider(),
        perspectiveNotificationListener: new NoOpWorkNotificationListener(),
        governor: PerspectiveWorker.CreateDefaultGovernor(options),
        timeProvider: time);
      worker.OnStreamAffinityGateReleased += GatesReleased.Enqueue;

      // Queued before the worker starts, so its first batch holds every stream.
      foreach (var stream in streams) {
        if (drain) {
          await harness.EnqueueDrainStreamAsync(stream, ct);
        } else {
          foreach (var perspective in perspectives.Concat(AlsoUnresolvable ? new[] { NO_RUNNER } : Array.Empty<string>())) {
            await harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.NewGuid(), StreamId = stream, PerspectiveName = perspective }, ct);
          }
        }
      }
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      await worker.StartAsync(cts.Token);
      await Coordinator.AllReported.WaitAsync(ct);
      await cts.CancelAsync();
      await worker.ExecuteTask!.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      await provider.DisposeAsync();
    }
  }

  /// <summary>The fake runner of <see cref="SET_BASED"/>: it prepares a write for its context's sink, or hands the pair back.</summary>
  private sealed class SetBasedRunners(Dictionary<Guid, MessageEnvelope<IEvent>> events) : IPerspectiveRunnerRegistry {
    public bool HandBack { get; init; }
    public Guid? FailPrepareOf { get; init; }
    public bool ReadsNothing { get; init; }
    public bool AlsoPerStream { get; init; }
    public Action? OnPrepare { get; init; }
    public ConcurrentQueue<Guid> Rewound { get; } = new();
    private int _runCalls;
    public int RunCalls => Volatile.Read(ref _runCalls);
    public ConcurrentQueue<PreparedPerspectiveApply> Prepared { get; } = new();
    public ConcurrentDictionary<Guid, PerspectiveWriteStatus?> Completed { get; } = new();
    public ConcurrentQueue<Guid> EventsRead { get; } = new();
    private int _runWithEvents;
    public int RunWithEventsCalls => Volatile.Read(ref _runWithEvents);

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectiveName is SET_BASED or PER_STREAM ? new Runner(this, perspectiveName) : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => AlsoPerStream
      ? [_info(SET_BASED), _info(PER_STREAM)]
      : [_info(SET_BASED)];

    private static PerspectiveRegistrationInfo _info(string name) =>
      new(name, "global::" + name, "global::Tests.SetBased.Order", [TypeNameFormatter.Format(typeof(DrainModeTestEvent))]);

    public IReadOnlyList<Type> GetEventTypes() => [typeof(DrainModeTestEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();

    private PerspectiveCursorCompletion _completion(Guid streamId, string perspectiveName = SET_BASED) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = events[streamId].MessageId.Value,
      Status = PerspectiveProcessingStatus.Completed,
      RowChanged = true,
      ProcessedEventIds = [events[streamId].MessageId.Value],
    };

    private sealed class PreparedPair(Guid streamId, IReadOnlyList<MessageEnvelope<IEvent>> events, PendingPerspectiveWrite? write, PerspectivePerPairReason reason)
      : PreparedPerspectiveApply(streamId, SET_BASED, null, events, write, reason);

    private sealed class Runner(SetBasedRunners owner, string name) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(Runner);

      public Task<IReadOnlyList<MessageEnvelope<IEvent>>?> ReadEventsAsync(Guid streamId, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
        owner.EventsRead.Enqueue(streamId);
        return Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>?>(owner.ReadsNothing ? null : [owner._eventsOf(streamId)]);
      }

      public Task<PreparedPerspectiveApply> PrepareAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
          IReadOnlyList<MessageEnvelope<IEvent>> events, IPerspectiveBatchState? batchState, CancellationToken cancellationToken = default) {
        owner.OnPrepare?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (streamId == owner.FailPrepareOf) {
          throw new InvalidOperationException("the fold threw");
        }
        PreparedPerspectiveApply prepared = owner.HandBack
          ? new PreparedPair(streamId, events, null, PerspectivePerPairReason.RewindBelowPosition)
          : new PreparedPair(streamId, events, PendingPerspectiveWrite.Purge("wh_per_orders", streamId).ForSink(SINK_KEY), PerspectivePerPairReason.None);
        owner.Prepared.Enqueue(prepared);
        return Task.FromResult(prepared);
      }

      public Task<PerspectiveCursorCompletion> CompleteAsync(PreparedPerspectiveApply prepared, PerspectiveWriteOutcome? outcome, CancellationToken cancellationToken = default) {
        owner.Completed[prepared.StreamId] = outcome?.Status;
        return Task.FromResult(owner._completion(prepared.StreamId));
      }

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref owner._runCalls);
        return Task.FromResult(owner._completion(streamId, name));
      }

      public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
          IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref owner._runWithEvents);
        return Task.FromResult(owner._completion(streamId, name));
      }

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
        owner.Rewound.Enqueue(streamId);
        return Task.FromResult(owner._completion(streamId, name));
      }

      public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    }

    private MessageEnvelope<IEvent> _eventsOf(Guid streamId) => events[streamId];
  }

  /// <summary>A coordinator that serves the drain fetch and signals once the expected reports have all arrived.</summary>
  private sealed class ReportingCoordinator : IWorkCoordinator {
    private TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _expectedReports;
    private int _expectedFailures;
    public List<StreamEventData> StreamEventsToReturn { get; set; } = [];
    public Guid? ReportThrowsFor { get; set; }
    public ConcurrentDictionary<Guid, PerspectiveCursorInfo> Cursors { get; } = new();
    public ConcurrentQueue<PerspectiveCursorCompletion> Reported { get; } = new();
    public ConcurrentQueue<PerspectiveCursorFailure> Failed { get; } = new();
    public Task AllReported => _all.Task;

    public void Expect(int reports, int failures) {
      _expectedReports = reports;
      _expectedFailures = failures;
      _all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void _check() {
      if (Reported.Count >= _expectedReports && Failed.Count >= _expectedFailures) {
        _all.TrySetResult();
      }
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [], PerspectiveStreamIds = [] });

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      var rows = StreamEventsToReturn.Where(r => streamIds.Contains(r.StreamId)).ToList();
      // Each row is served once: the drain's refetch finds nothing more and ends.
      StreamEventsToReturn = [.. StreamEventsToReturn.Except(rows)];
      return Task.FromResult(rows);
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) {
      if (completion.StreamId == ReportThrowsFor) {
        throw new InvalidOperationException("the report failed");
      }
      Reported.Enqueue(completion);
      _check();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) {
      Failed.Enqueue(failure);
      _check();
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult(perspectiveName == SET_BASED && Cursors.TryGetValue(streamId, out var cursor) ? cursor : null);
  }

  private sealed class RecordingSink : IPerspectiveWriteSink {
    private readonly ConcurrentQueue<PendingPerspectiveWrite> _written = new();
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public IReadOnlyList<PendingPerspectiveWrite> Written => [.. _written];

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      foreach (var write in writes) {
        _written.Enqueue(write);
      }
      return Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([.. writes.Select(_ => PerspectiveWriteOutcome.Written)]);
    }
  }

  private sealed class RecordingLocks : IPerspectiveApplyCoordinator {
    private readonly ConcurrentQueue<Guid> _acquired = new();
    public IReadOnlyList<Guid> Acquired => [.. _acquired];

    public Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      _acquired.Enqueue(streamId);
      return Task.FromResult<IAsyncDisposable>(new Nothing());
    }

    private sealed class Nothing : IAsyncDisposable {
      public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
  }

  private sealed class PlanningStateSource : IPerspectiveStateSource {
    public IReadOnlyList<PerspectiveStateRequest>? Planned { get; private set; }

    public IPerspectiveBatchState BeginBatch(IReadOnlyList<PerspectiveStateRequest> pairs) {
      Planned = pairs;
      return new BatchedPerspectiveStateSource().BeginBatch(pairs);
    }
  }
}
