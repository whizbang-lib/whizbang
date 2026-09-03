using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
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
      IWorkCoordinator coordinator,
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
      new Whizbang.Core.Observability.ServiceInstanceProvider(),
      coalesceResolver: resolver,
      logger: null,
      timeProvider: time);
  }

  private static CoalesceGroupStats _stats(string group, long count, int oldestAge, int newestAge) => new() {
    Group = group,
    PendingCount = count,
    OldestCreatedAt = _testNow.AddSeconds(-oldestAge),
    NewestCreatedAt = _testNow.AddSeconds(-newestAge)
  };

  // === Scope carry ===
  //
  // Same defect class as the re-delivery pump: the composite's wire hop was built without a scope,
  // so a folded bundle arrived unscoped and the consumer's fan-out gave every child a null scope.
  // The children are then PERSISTED that way, which no later read can repair.

  [Test]
  public async Task RunOnce_CarriesTheSinglesScopeOntoTheCompositeHopAsync() {
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    var singles = new List<OutboxMessage> { _scopedSingle("test-topic", "tenant-a"), _scopedSingle("test-topic", "tenant-a") };
    coordinator.Stats = [_stats("record-digest", count: 2, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = [.. singles];

    await worker.RunOnceAsync(CancellationToken.None);

    var composite = coordinator.CompletedFolds[0].Composites[0];
    var hop = composite.Metadata.Hops[0];
    await Assert.That(hop.Scope).IsNotNull()
      .Because("the folded singles carried a scope; dropping it here persists every fanned-out "
             + "child unscoped, and a perspective requiring a security context parks them all");
    await Assert.That(hop.Scope!.ApplyTo(null).Scope.TenantId).IsEqualTo("tenant-a");
  }

  [Test]
  public async Task RunOnce_NeverFoldsDifferentScopesIntoOneCompositeAsync() {
    // One composite carries ONE hop scope. Folding two tenants together could only stamp one of
    // them, shipping one tenant's event under the other's authority.
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    var singles = new List<OutboxMessage> { _scopedSingle("test-topic", "tenant-a"), _scopedSingle("test-topic", "tenant-b") };
    coordinator.Stats = [_stats("record-digest", count: 2, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = [.. singles];

    await worker.RunOnceAsync(CancellationToken.None);

    var composites = coordinator.CompletedFolds.SelectMany(f => f.Composites).ToList();
    await Assert.That(composites.Count).IsEqualTo(2)
      .Because("two scopes cannot share one bundle without mis-attributing one of them");
    var tenants = composites
      .Select(c => c.Metadata.Hops[0].Scope?.ApplyTo(null).Scope.TenantId ?? "<unscoped>")
      .OrderBy(t => t, StringComparer.Ordinal).ToList();
    await Assert.That(tenants).IsEquivalentTo(["tenant-a", "tenant-b"]);
  }

  [Test]
  public async Task RunOnce_LeavesTheCompositeUnscopedWhenSinglesHadNoScopeAsync() {
    var (worker, coordinator, _) = _build(configureBinding: c => c.SlideSeconds = 15);
    coordinator.Stats = [_stats("record-digest", count: 2, oldestAge: 40, newestAge: 20)];
    coordinator.PendingSingles["record-digest"] = [.. _singles(2)];

    await worker.RunOnceAsync(CancellationToken.None);

    var composite = coordinator.CompletedFolds[0].Composites[0];
    await Assert.That(composite.Metadata.Hops[0].Scope).IsNull()
      .Because("unscoped singles must fold into an unscoped composite — inventing an authority "
             + "here would be worse than the failure it hides");
  }

  private static OutboxMessage _scopedSingle(string destination, string tenantId) {
    var single = _single(destination);
    var hop = new MessageHop {
      Type = HopType.Current,
      Timestamp = _testNow,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Scope = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = tenantId, UserId = "user-1" }),
    };
    return single with {
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = single.Envelope.MessageId,
        Payload = single.Envelope.Payload,
        Hops = [hop],
        DispatchContext = single.Envelope.DispatchContext,
      },
      Metadata = new EnvelopeMetadata { MessageId = single.Envelope.MessageId, Hops = [hop] },
    };
  }

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

  #region Loop resilience

  // The shipper is the only thing that folds coalesce rows and the only thing that releases them
  // when folding is not possible. If a transient coordinator failure ends the loop nothing
  // notices — the worker is still "running", the backlog just stops draining, and the rows sit
  // there until someone restarts the process. So each step has to survive its own failure and
  // come back on the next tick.

  /// <summary>A coordinator whose coalesce calls fail a fixed number of times, then succeed.</summary>
  private sealed class FlakyCoalesceCoordinator(int statsFailures, int releaseFailures) : IWorkCoordinator {
    private int _statsLeft = statsFailures;
    private int _releaseLeft = releaseFailures;

    public int StatsAttempts { get; private set; }
    public int ReleaseAttempts { get; private set; }
    public TaskCompletionSource StatsSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(
        CancellationToken cancellationToken = default) {
      StatsAttempts++;
      if (_statsLeft-- > 0) {
        return Task.FromException<IReadOnlyList<CoalesceGroupStats>>(
          new InvalidOperationException("transient coordinator outage"));
      }
      StatsSucceeded.TrySetResult();
      return Task.FromResult<IReadOnlyList<CoalesceGroupStats>>([]);
    }

    public Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
      ReleaseAttempts++;
      if (_releaseLeft-- > 0) {
        return Task.FromException<int>(new InvalidOperationException("release failed"));
      }
      ReleaseSucceeded.TrySetResult();
      return Task.FromResult(0);
    }

    // The rest of IWorkCoordinator is default-implemented; only the abstract members need bodies.
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private static CoalesceGroupResolver _oneGroupResolver(FakeTimeProvider time, string group = "record-digest") {
    var tagOptions = new TagOptions();
    tagOptions.Coalesce(group, c => c.SlideSeconds = 15);
    return new CoalesceGroupResolver(tagOptions, time, () => []);
  }

  /// <summary>
  /// A failing startup recovery is logged and the loop still starts ticking.
  /// </summary>
  /// <remarks>
  /// Recovery runs against a coordinator that has just come up alongside this process, so it is
  /// the single most likely step to hit a cold connection. Letting that end ExecuteAsync would
  /// mean a database blip during rollout silently disables coalesce shipping for the life of
  /// the pod.
  /// </remarks>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_StartupRecoveryFails_TheLoopStillRunsAsync(CancellationToken testToken) {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new FlakyCoalesceCoordinator(statsFailures: 0, releaseFailures: 1);
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);
    await coordinator.StatsSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.ReleaseAttempts).IsGreaterThanOrEqualTo(1)
      .Because("recovery ran and failed");
    await Assert.That(coordinator.StatsAttempts).IsGreaterThanOrEqualTo(1)
      .Because("a failed recovery must not stop the first tick — otherwise a cold-start blip "
             + "disables shipping until the process restarts");
  }

  /// <summary>
  /// A failing tick is logged and the loop keeps ticking.
  /// </summary>
  /// <remarks>
  /// There is no other path that drains these rows, so an ended loop is an unbounded backlog
  /// reported as a healthy worker.
  /// </remarks>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_ATickFails_TheLoopKeepsTickingAsync(CancellationToken testToken) {
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new FlakyCoalesceCoordinator(statsFailures: 2, releaseFailures: 0);
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);
    // Ticks pace on a timer the FakeTimeProvider owns; advance until a stats call succeeds.
    while (!coordinator.StatsSucceeded.Task.IsCompleted && !cts.IsCancellationRequested) {
      time.Advance(TimeSpan.FromSeconds(30));
      await Task.Delay(20, testToken);
    }
    await coordinator.StatsSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.StatsAttempts).IsGreaterThanOrEqualTo(3)
      .Because("two ticks failed and a third ran — one failure must not end the loop");
  }

  /// <summary>
  /// Cancellation during the loop ends it cleanly rather than faulting.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledMidLoop_StopsWithoutFaultingAsync(CancellationToken testToken) {
    // Shutdown cancels the stopping token while the worker may be inside a coordinator call.
    // A fault here surfaces as a failed host shutdown, which reads as a crash.
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new FlakyCoalesceCoordinator(statsFailures: 0, releaseFailures: 0);
    var worker = _buildWorker(coordinator, _oneGroupResolver(time), time);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);
    await coordinator.StatsSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    var executeTask = worker.ExecuteTask;

    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a faulted ExecuteAsync turns an ordinary shutdown into a reported crash");
  }

  /// <summary>
  /// Cancellation while waiting on the schema gate returns without touching the coordinator.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledBeforeSchemaReady_NeverStartsAsync(CancellationToken testToken) {
    // A host that fails during migration stops everything it built. The shipper must not run
    // recovery against a schema that is not there.
    var time = new FakeTimeProvider(_testNow);
    var coordinator = new FlakyCoalesceCoordinator(statsFailures: 0, releaseFailures: 0);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton(new WorkCoordinatorOptions());
    var sp = services.BuildServiceProvider();

    // Gate never marked ready.
    var worker = new CoalesceShipWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      new Whizbang.Core.Observability.ServiceInstanceProvider(),
      coalesceResolver: _oneGroupResolver(time),
      logger: null,
      timeProvider: time);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse();
    await Assert.That(coordinator.StatsAttempts).IsEqualTo(0)
      .Because("nothing may run before the schema the coalesce tables live in exists");
  }

  #endregion
}
