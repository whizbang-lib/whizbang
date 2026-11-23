// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
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
/// The worker's write-behind mode (#2247), on the drain: a held batch is neither written nor reported until its model is
/// flushed, a hot stream's batches are written once, and the model is flushed by its interval, by a sync wait, by
/// anything else that needs the row, and at stop; a model that outlived its lease is dropped, never written.
/// </summary>
/// <remarks>
/// Every wait is on a signal: the hold signal (<see cref="PerspectiveWorker.OnWriteBehindHeld"/>), the coordinator's
/// reports, the sink's writes. Time moves only on the fake clock, and only to make a model due.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.WriteBehind.cs</code-under-test>
public partial class PerspectiveWorkerDrainModeTests {
  private const string BEHIND = "Tests.WriteBehind.Counter";
  private const string BEHIND_TABLE = "wh_per_counter";
  private const int INTERVAL_MS = 1000;

  private static void _registerWriteBehindModes() => PerspectiveWriteModes.Register(BEHIND, PerspectiveWriteMode.WriteBehind);

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_StartWithoutBounds_FailsNamingThemAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig { Bounded = false };

    var start = await Assert.That(async () => await rig.StartAsync(cancellationToken)).Throws<InvalidOperationException>();

    await Assert.That(start!.Message).Contains("MaxModels is not set");
    await Assert.That(start.Message).Contains("MaxBytes is not set");
    await Assert.That(start.Message).Contains(BEHIND);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AHotStreamsBatches_AreHeld_ThenWrittenOnceWhenTheIntervalElapsesAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);

    await rig.DrainAsync(stream, 1, cancellationToken);
    await rig.DrainAsync(stream, 2, cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(0).Because("a held batch is not written");
    await Assert.That(rig.Coordinator.Reported).IsEmpty().Because("a held batch's checkpoint does not move until its flush");
    await Assert.That(rig.Runners.PreparedOnHeld).IsEqualTo(1).Because("the second batch folds onto the held model");
    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(1);

    rig.Coordinator.Expect(2, 0);
    rig.Time.Advance(TimeSpan.FromMilliseconds(INTERVAL_MS));
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Sink.Written).Count().IsEqualTo(1).Because("both batches are written with one merged write");
    await Assert.That(rig.Sink.Written[0].Row!.Data).IsEqualTo("{\"Count\":2}");
    await Assert.That(rig.Sink.Written[0].Operation).IsEqualTo(PerspectiveWriteOperation.Insert);
    await Assert.That(rig.Runners.Completed.Select(c => c.Status)).IsEquivalentTo(
      [(PerspectiveWriteStatus?)PerspectiveWriteStatus.Written, PerspectiveWriteStatus.Written]);
    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(0);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.lookups", "hit")).IsEqualTo(1);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.lookups", "miss")).IsEqualTo(1);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes")).IsEqualTo(0)
      .Because("a flush at its interval is not forced");
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ASyncWaitOnAHeldEvent_FlushesItsModelAtOnceAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Coordinator.Expect(1, 0);

    _ = rig.Sync.WaitForPerspectiveEventsAsync([rig.EventsOf(stream)[0]], BEHIND, TimeSpan.Zero, cancellationToken: cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(1);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "sync")).IsEqualTo(1);
    var flushes = rig.Sink.Calls;
    _ = rig.Sync.WaitForPerspectiveEventsAsync([Guid.NewGuid()], BEHIND, TimeSpan.Zero, cancellationToken: cancellationToken);
    await Assert.That(rig.Sink.Calls).IsEqualTo(flushes).Because("a wait on nothing held flushes nothing");
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AModelThatOutlivesItsLease_IsDropped_NeverWrittenAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);

    // The interval's flush waits on a gate the test holds; by the time it takes the gate, the lease has run out.
    using (await rig.Worker.AcquireAffinityGatesInOrderAsync([(stream, BEHIND)], "test", cancellationToken)) {
      rig.Time.Advance(TimeSpan.FromMilliseconds(INTERVAL_MS));
      rig.Time.Advance(TimeSpan.FromSeconds(270));
    }
    await rig.Worker.WriteBehindTickInFlight.WaitAsync(cancellationToken);
    await Assert.That(rig.Sink.Calls).IsEqualTo(0).Because("a model whose rows may be another instance's is never written");
    // A model past its lease is also dropped when it is found due.
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Time.Advance(TimeSpan.FromSeconds(270));
    await rig.Worker.WriteBehindTickInFlight.WaitAsync(cancellationToken);
    await rig.Worker.FlushWriteBehindDueAsync(cancellationToken);

    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(0);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.dropped", null)).IsEqualTo(2);
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
    await Assert.That(rig.Coordinator.Reported).IsEmpty().Because("its rows replay from the checkpoint wherever they are claimed next");
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ANewModelThatDoesNotFit_IsWrittenAtOnce_AndAnOverBoundCacheFlushesItsOldestAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig { MaxModels = 1 };
    var held = Guid.NewGuid();
    var written = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(held, 1, cancellationToken);
    rig.Coordinator.Expect(1, 0);

    await rig.DrainAsync(written, 0, cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Sink.Written.Select(w => w.StreamId)).IsEquivalentTo([written])
      .Because("a second model does not fit a cache of one, so its batch is written as set-based writes it");
    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AModelAtItsBatchCap_IsFlushedAfterTheBatchThatReachesItAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig { MaxBatches = 2 };
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Coordinator.Expect(2, 0);

    await rig.DrainAsync(stream, 2, cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Sink.Written).Count().IsEqualTo(1);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "batches")).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AMergedWriteTheDatabaseRejects_HandsEveryHeldBatchToThePerPairPathAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    await rig.DrainAsync(stream, 2, cancellationToken);
    rig.Sink.Outcome = PerspectiveWriteOutcome.Failed(new InvalidOperationException("rejected"));
    rig.Coordinator.Expect(2, 0);

    await rig.Worker.FlushWriteBehindAsync(cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Runners.Completed.Select(c => c.Status)).IsEquivalentTo(
      [(PerspectiveWriteStatus?)PerspectiveWriteStatus.Failed, PerspectiveWriteStatus.Failed]);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "requested")).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AMergedWriteTheGuardRefuses_IsReappliedAsAConflictAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Sink.Outcome = PerspectiveWriteOutcome.Refused;
    rig.Coordinator.Expect(1, 0);

    await rig.Worker.FlushWriteBehindAsync(cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Runners.Completed.Single().Status).IsEqualTo(PerspectiveWriteStatus.Conflict)
      .Because("each held batch is re-applied per pair, where the guard rewinds the stream on the row it finds");
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ACompletionThatThrows_FailsOnlyItsBatchAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Runners.CompleteThrows = true;
    rig.Coordinator.Expect(0, 1);

    await rig.Worker.FlushWriteBehindAsync(cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Coordinator.Failed.Single().StreamId).IsEqualTo(stream);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AnUncheckedWrite_IsNeverHeldAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    rig.Runners.Unchecked = true;
    await rig.StartAsync(cancellationToken);
    rig.Coordinator.Expect(1, 0);

    await rig.DrainAsync(stream, 0, cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Sink.Calls).IsEqualTo(1)
      .Because("a write that is not checked against the row's version could not meet a writer it missed");
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ClaimedWorkForAHeldStream_FlushesTheModelFirstAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Coordinator.Expect(2, 0);

    await rig.ClaimAsync(stream, cancellationToken);
    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);

    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "bypass")).IsEqualTo(1)
      .Because("the claimed pair meets its row current");
    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ALaterDrainOfOnlyHeldEvents_AppliesNothingAgainAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);

    // The held batch's rows are still pending, so the claim emits the stream again and the drain fetches them again.
    await rig.RedrainHeldAsync(stream, cancellationToken);

    await Assert.That(rig.Runners.PrepareCalls).IsEqualTo(1).Because("the held events are left out of the drain");
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_TheWorkerStopping_FlushesEveryHeldModelAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Coordinator.Expect(1, 0);

    await rig.Worker.StopAsync(cancellationToken);

    await rig.Coordinator.AllReported.WaitAsync(cancellationToken);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "stop")).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_AWorkerWithNothingWritingBehind_StartsAndStopsWithoutACacheAsync(CancellationToken cancellationToken) {
    var worker = SetBasedRig.BareWorker();

    await worker.StartAsync(cancellationToken);
    await worker.StopAsync(cancellationToken);
    await worker.FlushWriteBehindAsync(cancellationToken);
    await worker.FlushWriteBehindDueAsync(cancellationToken);

    await Assert.That(worker.WriteBehindModels).IsEqualTo(0);
  }

  /// <summary>A model held across drains: its count, serialized.</summary>
  public sealed class CounterModel {
    public int Count { get; set; }
  }

  /// <summary>A drain rig for one write-behind perspective, driven drain by drain.</summary>
  private sealed class WriteBehindRig : IAsyncDisposable {
    private readonly ServiceProvider _provider;
    private readonly PerspectiveWorkerTestHarness _harness = new();
    private readonly ConcurrentDictionary<Guid, List<Guid>> _events = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _held = new();
    private readonly MeterListener _listener = new();
    private readonly DrainModeEventStore _store = new();
    private readonly ConcurrentDictionary<(string Name, string? Tag), long> _measured = new();
    private int _holds;
    private CancellationTokenSource? _running;
    private PerspectiveWorker? _worker;

    public bool Bounded { get; init; } = true;
    public int MaxModels { get; init; } = 100;
    public int MaxBatches { get; init; } = 32;
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    public WriteBehindRunners Runners { get; }
    public ReportingCoordinator Coordinator { get; } = new();
    public OutcomeSink Sink { get; } = new();
    public SyncEventTracker Sync { get; } = new();
    public PerspectiveWorker Worker => _worker!;

    public WriteBehindRig() {
      _registerWriteBehindModes();
      Runners = new WriteBehindRunners();
      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddSingleton<IWorkCoordinator>(Coordinator);
      services.AddSingleton<IPerspectiveRunnerRegistry>(Runners);
      services.AddSingleton<IEventTypeProvider>(new FakeEventTypeProvider([typeof(DrainModeTestEvent)]));
      services.AddSingleton<IEventStore>(_store);
      services.AddSingleton<IPerspectiveApplyCoordinator>(new RecordingLocks());
      services.AddKeyedSingleton<IPerspectiveWriteSink>(SINK_KEY, Sink);
      services.AddLogging();
      services.AddMetrics();
      services.AddSingleton<WhizbangMetrics>();
      services.AddSingleton<PerspectiveMetrics>();
      _provider = services.BuildServiceProvider();
      // Only this rig's meter: every test's worker publishes instruments of the same names.
      var meter = _provider.GetRequiredService<PerspectiveMetrics>().WriteBehindDropped.Meter;
      _listener.InstrumentPublished = (instrument, listener) => {
        if (ReferenceEquals(instrument.Meter, meter) && instrument.Name.Contains("write_behind", StringComparison.Ordinal)) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
        string? tag = null;
        foreach (var t in tags) {
          if (t.Key is "reason" or "result") {
            tag = t.Value as string;
          }
        }
        // A passive counter is observed: each observation is its cumulative value for the tag.
        _measured[(instrument.Name, tag)] = value;
      });
      _listener.Start();
    }

    private const string ALL = "*";

    /// <summary>A counter's total, for the given reason or result tag, or across all of them.</summary>
    public long Measured(string name, string? tag = ALL) {
      _listener.RecordObservableInstruments();
      return tag == ALL
        ? _measured.Where(m => m.Key.Name == name).Sum(m => m.Value)
        : _measured.GetValueOrDefault((name, tag));
    }

    public List<Guid> EventsOf(Guid stream) => _events[stream];

    public async Task StartAsync(CancellationToken ct) {
      var options = new PerspectiveWorkerOptions {
        PollingIntervalMilliseconds = 50,
        MaxConcurrentDrainConsumers = 1,
        WriteMode = PerspectiveWriteMode.SetBased,
        DrainBatcher = new SlidingWindowBatcherOptions { SlidingWindow = TimeSpan.Zero, MaxWait = TimeSpan.Zero, MaxSize = 1000 },
        WriteBehind = Bounded
          ? new PerspectiveWriteBehindOptions { MaxModels = MaxModels, MaxBytes = 1_000_000, MaxBatchesPerModel = MaxBatches, MaxFlushIntervalMilliseconds = INTERVAL_MS }
          : new PerspectiveWriteBehindOptions(),
      };
      _worker = new PerspectiveWorker(
        instanceProvider: new FakeServiceInstanceProvider(),
        scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>(),
        options: Options.Create(options),
        tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
        completionStrategy: new InstantCompletionStrategy(logger: NullLogger<InstantCompletionStrategy>.Instance),
        eventTypeProvider: _provider.GetRequiredService<IEventTypeProvider>(),
        syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
        syncEventTracker: Sync,
        logger: NullLogger<PerspectiveWorker>.Instance,
        snapshotStore: NullPerspectiveSnapshotStore.Instance,
        streamLocker: NullPerspectiveStreamLocker.Instance,
        streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
        streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
        processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
        workChannelWriter: new WorkChannelWriter(),
        rewindOptions: Options.Create(new PerspectiveRewindOptions()),
        perspectiveChannelWriter: _harness.ChannelWriter,
        perspectiveCompletionChannel: _harness.CompletionCapture,
        failureChannel: _harness.FailureCapture,
        leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
        perspectiveDrainChannel: _harness.DrainChannel,
        leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
        leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
        deadLetterStore: NullDeadLetterStore.Instance,
        generationProvider: new DefaultGenerationProvider(),
        perspectiveNotificationListener: new NoOpWorkNotificationListener(),
        governor: PerspectiveWorker.CreateDefaultGovernor(options),
        metrics: _provider.GetRequiredService<PerspectiveMetrics>(),
        timeProvider: Time);
      _worker.OnWriteBehindHeld += key => {
        if (key.PerspectiveName == BEHIND) {
          _signal(Interlocked.Increment(ref _holds)).TrySetResult();
        }
      };
      _running = CancellationTokenSource.CreateLinkedTokenSource(ct);
      await _worker.StartAsync(_running.Token);
    }

    /// <summary>
    /// Commits one more event on the stream and drains it, then waits until <paramref name="heldCount"/> batches have
    /// been held in all (or, for zero, until the drain's batch has ended).
    /// </summary>
    public async Task DrainAsync(Guid stream, int heldCount, CancellationToken ct) {
      var eventId = Guid.CreateVersion7(Time.GetUtcNow().AddSeconds(_events.GetValueOrDefault(stream)?.Count ?? 0));
      _events.AddOrUpdate(stream, _ => [eventId], (_, list) => [.. list, eventId]);
      _serve(stream, [eventId]);
      await _drainAndWaitAsync(stream, heldCount, ct);
    }

    /// <summary>Drains the stream again with only its held events pending, as the claim emits it while they are.</summary>
    public async Task RedrainHeldAsync(Guid stream, CancellationToken ct) {
      _serve(stream, _events[stream]);
      await _drainAndWaitAsync(stream, 0, ct);
    }

    /// <summary>Queues the rows the drain's fetch returns once, and the events the store deserializes them to.</summary>
    private void _serve(Guid stream, List<Guid> eventIds) {
      Coordinator.StreamEventsToReturn = [.. Coordinator.StreamEventsToReturn, .. eventIds.Select(e => _row(stream, e))];
      _store.DeserializedEventsToReturn = [.. eventIds.Select(WriteBehindRunners.Envelope)];
    }

    /// <summary>Hands the stream's pair to the claimed path.</summary>
    public async Task ClaimAsync(Guid stream, CancellationToken ct) {
      Runners.ClaimedEvents[stream] = _events[stream];
      await _harness.EnqueueWorkAsync(new PerspectiveWork { WorkId = Guid.NewGuid(), StreamId = stream, PerspectiveName = BEHIND }, ct);
    }

    private async Task _drainAndWaitAsync(Guid stream, int heldCount, CancellationToken ct) {
      var cycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      void onCycle() => cycle.TrySetResult();
      _worker!.OnBatchCycleComplete += onCycle;
      try {
        await _harness.EnqueueDrainStreamAsync(stream, ct);
        await (heldCount > 0 ? _signal(heldCount).Task : cycle.Task).WaitAsync(ct);
        await cycle.Task.WaitAsync(ct);
      } finally {
        _worker.OnBatchCycleComplete -= onCycle;
      }
    }

    private static StreamEventData _row(Guid stream, Guid eventId) => new() {
      StreamId = stream,
      EventId = eventId,
      EventType = TypeNameFormatter.Format(typeof(DrainModeTestEvent)),
      EventData = JsonSerializer.Serialize(new DrainModeTestEvent("write-behind")),
      EventWorkId = Guid.NewGuid(),
      PerspectiveName = BEHIND,
    };

    private TaskCompletionSource _signal(int count) =>
      _held.GetOrAdd(count, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public async ValueTask DisposeAsync() {
      if (_running is not null) {
        await _running.CancelAsync();
        if (_worker?.ExecuteTask is { } execution) {
          await execution.WaitAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        _running.Dispose();
      }
      _listener.Dispose();
      await _provider.DisposeAsync();
    }
  }

  /// <summary>A sink whose every write ends with <see cref="Outcome"/>.</summary>
  private sealed class OutcomeSink : IPerspectiveWriteSink {
    private readonly ConcurrentQueue<PendingPerspectiveWrite> _written = new();
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public IReadOnlyList<PendingPerspectiveWrite> Written => [.. _written];
    public PerspectiveWriteOutcome Outcome { get; set; } = PerspectiveWriteOutcome.Written;

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      foreach (var write in writes) {
        _written.Enqueue(write);
      }
      return Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([.. writes.Select(_ => Outcome)]);
    }
  }

  /// <summary>The fake runner of <see cref="BEHIND"/>: it counts its events onto the held model, or a new one.</summary>
  private sealed class WriteBehindRunners : IPerspectiveRunnerRegistry {
    private int _preparedOnHeld;
    private int _prepareCalls;
    public int PreparedOnHeld => Volatile.Read(ref _preparedOnHeld);
    public int PrepareCalls => Volatile.Read(ref _prepareCalls);
    public bool Unchecked { get; set; }
    public bool CompleteThrows { get; set; }
    public ConcurrentQueue<(Guid StreamId, PerspectiveWriteStatus? Status)> Completed { get; } = new();
    public ConcurrentDictionary<Guid, List<Guid>> ClaimedEvents { get; } = new();

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) =>
      perspectiveName == BEHIND ? new Runner(this) : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new(BEHIND, "global::" + BEHIND, "global::Tests.WriteBehind.CounterModel", [TypeNameFormatter.Format(typeof(DrainModeTestEvent))])];

    public IReadOnlyList<Type> GetEventTypes() => [typeof(DrainModeTestEvent)];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();

    private sealed class HeldModel(PerspectiveApplyRead read, Guid last, CounterModel model)
      : PerspectiveWriteBehindRow(read, last, JsonSerializer.SerializeToUtf8Bytes(model.Count).Length) {
      public override PerspectiveStoredRow<TModel>? ReadAs<TModel>() =>
        new PerspectiveStoredRow<CounterModel>(ApplyRead, new CounterModel { Count = model.Count }) as PerspectiveStoredRow<TModel>;
    }

    private sealed class Prepared(Guid streamId, IReadOnlyList<MessageEnvelope<IEvent>> events, PendingPerspectiveWrite write, HeldModel? held)
      : PreparedPerspectiveApply(streamId, BEHIND, null, events, write, PerspectivePerPairReason.None) {
      public override PerspectiveWriteBehindRow? WriteBehindRow => held;
    }

    private sealed class Runner(WriteBehindRunners owner) : IPerspectiveRunner {
      public Type PerspectiveType => typeof(Runner);

      public Task<IReadOnlyList<MessageEnvelope<IEvent>>?> ReadEventsAsync(Guid streamId, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>?>([.. owner.ClaimedEvents.GetValueOrDefault(streamId, []).Select(_envelope)]);

      public async Task<PreparedPerspectiveApply> PrepareAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
          IReadOnlyList<MessageEnvelope<IEvent>> events, IPerspectiveBatchState? batchState, CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref owner._prepareCalls);
        var row = batchState is null ? null : await batchState.ReadRowAsync<CounterModel>(null!, streamId, perspectiveName, cancellationToken);
        if (row?.Model is not null) {
          Interlocked.Increment(ref owner._preparedOnHeld);
        }
        var version = owner.Unchecked ? PerspectiveRowVersion.Unchecked : row?.ApplyRead.Version ?? PerspectiveRowVersion.Absent;
        var model = new CounterModel { Count = (row?.Model?.Count ?? 0) + events.Count };
        var values = new PerspectiveRowValues(
          $"{{\"Count\":{model.Count}}}", "{}", "{}", false, false, DateTime.UnixEpoch, DateTime.UnixEpoch, false, true, null, []);
        var write = PendingPerspectiveWrite.Write(BEHIND_TABLE, streamId, version, values).ForSink(SINK_KEY);
        var keep = batchState?.KeepsModels(streamId, perspectiveName) == true;
        var read = new PerspectiveApplyRead(version, null);
        return new Prepared(streamId, events, write, keep ? new HeldModel(read, events[^1].MessageId.Value, model) : null);
      }

      public Task<PerspectiveCursorCompletion> CompleteAsync(PreparedPerspectiveApply prepared, PerspectiveWriteOutcome? outcome, CancellationToken cancellationToken = default) {
        owner.Completed.Enqueue((prepared.StreamId, outcome?.Status));
        if (owner.CompleteThrows) {
          throw new InvalidOperationException("the completion threw");
        }
        return Task.FromResult(_completion(prepared.StreamId, prepared.Events[^1].MessageId.Value));
      }

      public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_completion(streamId, owner.ClaimedEvents.GetValueOrDefault(streamId, [Guid.Empty])[^1]));

      public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
          IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) =>
        Task.FromResult(_completion(streamId, events[^1].MessageId.Value));

      public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_completion(streamId, triggeringEventId));

      public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

      private static PerspectiveCursorCompletion _completion(Guid streamId, Guid lastEventId) => new() {
        StreamId = streamId,
        PerspectiveName = BEHIND,
        LastEventId = lastEventId,
        Status = PerspectiveProcessingStatus.Completed,
        RowChanged = true,
        ProcessedEventIds = [lastEventId],
      };

      private static MessageEnvelope<IEvent> _envelope(Guid eventId) => Envelope(eventId);
    }

    /// <summary>The event an id stands for, as the drain and the claimed read hand it to the runner.</summary>
    public static MessageEnvelope<IEvent> Envelope(Guid eventId) => new() {
      MessageId = new MessageId(eventId),
      Payload = new DrainModeTestEvent("write-behind"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
  }
}
