using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Tags;
using Whizbang.Core.Tests.Tags;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="CoalesceShipWorker"/> — the generic sliding-window coalesce shipper.
/// Per group and per tick it reads pending stats and folds when the group has gone QUIET
/// (newest single older than SlideSeconds) or gone OVERDUE (oldest older than
/// MaxDelaySeconds), fetching in MaxBatchCount chunks, building the binding's composite, and
/// completing fold + insert atomically through the coordinator seam. Matured strays are
/// RELEASED (group + floor cleared) on startup recovery and as a per-tick backstop.
/// No wall-clock waits: decisions run through <see cref="CoalesceShipWorker.RunOnceAsync"/>
/// with a FakeTimeProvider, mirroring the MaintenanceWorker seam idiom.
/// </summary>
[Category("Workers")]
public class CoalesceShipWorkerTests {
  private static readonly DateTimeOffset _testNow = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

  #region Slide / max-delay firing decisions

  [Test]
  public async Task RunOnce_GroupStillArriving_DoesNotFoldAsync() {
    // Newest single is 5s old with a 15s slide — the window is still open; a fold now would
    // split a burst instead of shipping its entire tail at burst-end.
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    coordinator.Stats = [_stats("record-digest", count: 10, oldestAge: 30, newestAge: 5)];

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.FetchedGroups).IsEmpty();
  }

  [Test]
  public async Task RunOnce_GroupQuiet_FoldsAsync() {
    // Newest single is 20s old with a 15s slide — quiet: the burst ended, fold the tail.
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    coordinator.Stats = [_stats("record-digest", count: 3, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = _singles(3);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.FetchedGroups).Contains("record-digest");
    await Assert.That(coordinator.CompletedFolds.Count).IsEqualTo(1);
    await Assert.That(coordinator.CompletedFolds[0].FoldedIds.Count).IsEqualTo(3);
    await Assert.That(coordinator.CompletedFolds[0].Composites.Length).IsEqualTo(1);
  }

  [Test]
  public async Task RunOnce_ContinuousArrivalsPastMaxDelay_FoldsAnywayAsync() {
    // The slide never goes quiet under continuous arrivals (newest 2s old), but the oldest
    // pending single has blown MaxDelaySeconds — the freshness cap forces an oldest-first ship.
    var (worker, coordinator, _) = _build(configureBinding: c => {
      c.SlideSeconds = 15;
      c.MaxDelaySeconds = 120;
    });
    coordinator.Stats = [_stats("record-digest", count: 5, oldestAge: 130, newestAge: 2)];
    coordinator.PendingSingles["record-digest"] = _singles(5);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.CompletedFolds.Count).IsEqualTo(1);
  }

  [Test]
  public async Task RunOnce_UnboundGroupInStats_IsNotFoldedAsync() {
    // A group present in the table but no longer bound (binding removed / disabled) must not
    // fold — its rows surface via the release backstop when their floors mature.
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    coordinator.Stats = [_stats("unbound-group", count: 4, oldestAge: 500, newestAge: 400)];

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.FetchedGroups).IsEmpty();
    await Assert.That(coordinator.ReleasedGroups).Contains("unbound-group")
      .Because("unbound strays must still degrade to individual shipping, never sit forever");
  }

  #endregion

  #region Chunking

  [Test]
  public async Task RunOnce_PendingLargerThanMaxBatch_FoldsInChunksAsync() {
    // 7 pending with MaxBatchCount 3: the fold loop drains in 3+3+1 — three composites,
    // every single folded exactly once.
    var (worker, coordinator, _) = _build(configureBinding: c => {
      c.SlideSeconds = 15;
      c.MaxBatchCount = 3;
    });
    coordinator.Stats = [_stats("record-digest", count: 7, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = _singles(7);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.CompletedFolds.Count).IsEqualTo(3);
    await Assert.That(coordinator.CompletedFolds[0].FoldedIds.Count).IsEqualTo(3);
    await Assert.That(coordinator.CompletedFolds[1].FoldedIds.Count).IsEqualTo(3);
    await Assert.That(coordinator.CompletedFolds[2].FoldedIds.Count).IsEqualTo(1);
    var allFolded = coordinator.CompletedFolds.SelectMany(f => f.FoldedIds).ToList();
    await Assert.That(allFolded.Count).IsEqualTo(allFolded.Distinct().Count());
  }

  #endregion

  #region Composite building

  [Test]
  public async Task RunOnce_DefaultFactory_BuildsRawCarryCompositeFromSinglesAsync() {
    // The default factory raw-carries each single's stored payload, wire type name, and
    // ORIGINAL message id (identity preservation dedups a race between fold and an
    // individually-shipped floor row at the consumer's inbox).
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    var singles = _singles(2);
    var expectedIds = singles.Select(m => m.MessageId).ToList();
    var expectedType = singles[0].MessageType;
    var expectedDestination = singles[0].Destination;
    coordinator.Stats = [_stats("record-digest", count: 2, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = [.. singles];

    await worker.RunOnceAsync(CancellationToken.None);

    var composite = coordinator.CompletedFolds[0].Composites[0];
    await Assert.That(composite.MessageType).Contains("CoalescedEventsComposite");
    await Assert.That(composite.CoalesceGroup).IsNull()
      .Because("the composite itself ships immediately — it must never re-enter the coalesce pool");
    await Assert.That(composite.ScheduledFor).IsNull();
    await Assert.That(composite.IsEvent).IsFalse()
      .Because("event singles were already event-stored at their own mint — the composite is transport-only");
    await Assert.That(composite.Destination).IsEqualTo(expectedDestination);
    var payload = composite.Envelope.Payload;
    var innerIds = payload.GetProperty("InnerEventIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
    await Assert.That(innerIds).IsEquivalentTo(expectedIds);
    var innerTypes = payload.GetProperty("InnerTypeNames").EnumerateArray().Select(e => e.GetString()).ToList();
    await Assert.That(innerTypes[0]).IsEqualTo(expectedType);
  }

  [Test]
  public async Task RunOnce_BindingFactory_BuildsTheBindingsCompositeAsync() {
    // The binding carries the composite factory (code, not reflection): the built-in audit
    // binding will supply AuditEventsComposite through exactly this seam.
    var (worker, coordinator, _) = _build(configureBinding: c => {
      c.SlideSeconds = 15;
      c.CompositeFactory = batch => new Whizbang.Core.Minting.AuditEventsComposite {
        StreamId = TrackedGuid.NewMedo(),
        Atomicity = batch.Atomicity,
        InnerPayloads = [.. batch.Singles.Select(s => s.Envelope.Payload)],
        InnerTypeNames = [.. batch.Singles.Select(s => s.MessageType)],
        InnerEventIds = [.. batch.Singles.Select(s => s.MessageId)]
      };
    });
    coordinator.Stats = [_stats("record-digest", count: 2, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = _singles(2);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.CompletedFolds[0].Composites[0].MessageType).Contains("AuditEventsComposite");
  }

  [Test]
  public async Task RunOnce_MixedDestinations_OneCompositePerDestinationAsync() {
    // A composite has ONE destination; a fetch spanning destinations must split.
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    var singles = new List<OutboxMessage> {
      _single("topic-a"), _single("topic-a"), _single("topic-b")
    };
    coordinator.Stats = [_stats("record-digest", count: 3, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = singles;

    await worker.RunOnceAsync(CancellationToken.None);

    var composites = coordinator.CompletedFolds.SelectMany(f => f.Composites).ToList();
    await Assert.That(composites.Count).IsEqualTo(2);
    await Assert.That(composites.Select(c => c.Destination).Order().ToList())
      .IsEquivalentTo(new List<string?> { "topic-a", "topic-b" });
  }

  #endregion

  #region Release: startup recovery + per-tick backstop

  [Test]
  public async Task StartupRecovery_ReleasesMaturedForEveryEnabledGroupOnceAsync() {
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);

    await worker.RunStartupRecoveryAsync(CancellationToken.None);

    await Assert.That(coordinator.ReleasedGroups).Contains("record-digest")
      .Because("rows that matured while no shipper ran must degrade to individual shipping immediately on recovery");
  }

  [Test]
  public async Task RunOnce_ReleaseBackstopRunsAfterTheFoldPassAsync() {
    // Backstop ordering is load-bearing: fold first (matured rows prefer folding — that is
    // the whole feature), release after (whatever the fold could not claim ships individually).
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    coordinator.Stats = [_stats("record-digest", count: 2, oldestAge: 200, newestAge: 130)];
    coordinator.PendingSingles["record-digest"] = _singles(2);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.CallOrder.IndexOf("fetch:record-digest"))
      .IsLessThan(coordinator.CallOrder.IndexOf("release:record-digest"));
  }

  #endregion

  #region No-binding gating

  [Test]
  public async Task ExecuteAsync_NoEnabledBindings_ParksWithoutTouchingTheCoordinatorAsync() {
    // Bindings finalize after AddWhizbang (EnableAudit may bind later in composition), so the
    // worker is registered unconditionally and gates at ExecuteAsync — the MaintenanceWorker
    // killswitch idiom. With nothing bound it parks; the coordinator is never resolved.
    var coordinator = new FakeCoalesceCoordinator();
    var tagOptions = new TagOptions();  // no bindings at all
    var resolver = new CoalesceGroupResolver(tagOptions, null, () => []);
    var worker = _buildWorker(coordinator, resolver, new FakeTimeProvider(_testNow));
    using var cts = new CancellationTokenSource();
    var statsSignal = coordinator.NextStatsCall();

    await worker.StartAsync(cts.Token);
    // Negative proof follows the MaintenanceWorker precedent: give an (incorrectly) unparked
    // worker a bounded chance to touch the coordinator, then assert it never did.
    await Task.WhenAny(statsSignal, Task.Delay(500, CancellationToken.None));

    await Assert.That(coordinator.StatsCalls).IsEqualTo(0);
    await Assert.That(coordinator.ReleasedGroups).IsEmpty();

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_NoResolverAtAll_ParksAsync() {
    var coordinator = new FakeCoalesceCoordinator();
    var worker = _buildWorker(coordinator, resolver: null, new FakeTimeProvider(_testNow));
    using var cts = new CancellationTokenSource();
    var statsSignal = coordinator.NextStatsCall();

    await worker.StartAsync(cts.Token);
    await Task.WhenAny(statsSignal, Task.Delay(500, CancellationToken.None));

    await Assert.That(coordinator.StatsCalls).IsEqualTo(0);

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);
  }

  #endregion

  #region Tick loop (FakeTimeProvider-driven)

  [Test]
  public async Task ExecuteAsync_RunsAFirstTickImmediatelyAfterRecoveryAsync() {
    // The loop runs its first tick right after startup recovery, before any delay — a restart
    // with an already-quiet backlog folds NOW instead of a tick later. The completion signal
    // is the fake coordinator's first stats call; no wall-clock advance is needed, which also
    // keeps this free of the FakeTimeProvider register-after-advance race.
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new FakeCoalesceCoordinator();
    coordinator.Stats = [];
    var tagOptions = new TagOptions();
    tagOptions.Coalesce("record-digest", c => c.SlideSeconds = 15);
    var resolver = new CoalesceGroupResolver(tagOptions, time, () => []);
    var worker = _buildWorker(coordinator, resolver, time);
    using var cts = new CancellationTokenSource();

    var firstStats = coordinator.NextStatsCall();
    await worker.StartAsync(cts.Token);
    await firstStats.WaitAsync(TimeSpan.FromSeconds(5));

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.StatsCalls).IsGreaterThanOrEqualTo(1);
    await Assert.That(coordinator.ReleasedGroups).Contains("record-digest")
      .Because("startup recovery released before the first tick ran");
  }

  #endregion

  #region Helpers

  private (CoalesceShipWorker Worker, FakeCoalesceCoordinator Coordinator, FakeTimeProvider Time) _build(
      Action<CoalescePolicyOptions> configureBinding) {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new FakeCoalesceCoordinator();
    var tagOptions = new TagOptions();
    tagOptions.Coalesce("record-digest", configureBinding);
    var resolver = new CoalesceGroupResolver(tagOptions, time,
      () => [CoalesceGroupResolverTests.TagRegistration(typeof(TestFoldedEvent), "record-digest")]);
    var worker = _buildWorker(coordinator, resolver, time);
    return (worker, coordinator, time);
  }

  private static CoalesceShipWorker _buildWorker(
      FakeCoalesceCoordinator coordinator,
      CoalesceGroupResolver? resolver,
      FakeTimeProvider time) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton(new WorkCoordinatorOptions());
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new CoalesceShipWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      resolver,
      logger: null,
      timeProvider: time);
  }

  private static CoalesceGroupStats _stats(string group, long count, int oldestAge, int newestAge) => new() {
    Group = group,
    PendingCount = count,
    OldestCreatedAt = _testNow.AddSeconds(-oldestAge),
    NewestCreatedAt = _testNow.AddSeconds(-newestAge)
  };

  private static List<OutboxMessage> _singles(int count) =>
    [.. Enumerable.Range(0, count).Select(_ => _single("test-topic"))];

  private static OutboxMessage _single(string destination) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { record = "data" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = destination,
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.NewGuid(),
      IsEvent = false,
      MessageType = "TestNamespace.TestFoldedEvent, TestAssembly",
      CoalesceGroup = "record-digest",
      ScheduledFor = _testNow.AddSeconds(60)
    };
  }

  internal sealed record TestFoldedEvent : IEvent;

  internal sealed class FakeCoalesceCoordinator : IWorkCoordinator {
    public IReadOnlyList<CoalesceGroupStats> Stats { get; set; } = [];
    public Dictionary<string, List<OutboxMessage>> PendingSingles { get; } = [];
    public List<string> FetchedGroups { get; } = [];
    public List<string> ReleasedGroups { get; } = [];
    public List<(IReadOnlyList<Guid> FoldedIds, OutboxMessage[] Composites)> CompletedFolds { get; } = [];
    public List<string> CallOrder { get; } = [];
    public int StatsCalls { get; private set; }

    private TaskCompletionSource _statsSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task NextStatsCall() {
      _statsSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
      return _statsSignal.Task;
    }

    public Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(CancellationToken cancellationToken = default) {
      StatsCalls++;
      CallOrder.Add("stats");
      _statsSignal.TrySetResult();
      return Task.FromResult(Stats);
    }

    public Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(string group, int limit, CancellationToken cancellationToken = default) {
      FetchedGroups.Add(group);
      CallOrder.Add($"fetch:{group}");
      if (!PendingSingles.TryGetValue(group, out var pending) || pending.Count == 0) {
        return Task.FromResult<IReadOnlyList<OutboxMessage>>([]);
      }
      var take = pending.Take(limit).ToList();
      pending.RemoveRange(0, take.Count);
      return Task.FromResult<IReadOnlyList<OutboxMessage>>(take);
    }

    public Task CompleteCoalesceFoldAsync(IReadOnlyList<Guid> foldedIds, OutboxMessage[] compositeMessages, int partitionCount, CancellationToken cancellationToken = default) {
      CompletedFolds.Add((foldedIds, compositeMessages));
      CallOrder.Add("complete");
      return Task.CompletedTask;
    }

    public Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
      ReleasedGroups.Add(group);
      CallOrder.Add($"release:{group}");
      return Task.FromResult(0);
    }

    // Required abstract members of IWorkCoordinator
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  #endregion
}
