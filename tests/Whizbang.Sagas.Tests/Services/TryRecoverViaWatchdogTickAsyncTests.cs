using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Repositories;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Services;

/// <summary>
/// Locks the watchdog re-arm and abandon semantics of
/// <see cref="BaseSagaService{T1,T2,T3,T4,T5,T6,T7,T8,T9}.TryRecoverViaWatchdogTickAsync"/>.
/// Per the framework docstring on
/// <see cref="ISagaCompletionWatchdogTickEvent.RescheduleCount"/>, the receptor
/// "drives the exponential backoff schedule (30s → 2m → 8m → 30m → abandon)" —
/// this test fixture is the executable specification of that promise.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class TryRecoverViaWatchdogTickAsyncTests {

  private const string SAGA_NAME = "TestSaga";
  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _entityId = Guid.Parse("22222222-2222-2222-2222-222222222222");

  [Test]
  public async Task FirstReArm_NoSnapshot_UsesInitialBudgetAsync() {
    // The post-Initiate watchdog tick has no LastObservedAt — this is the FIRST re-arm,
    // so the framework has no rate measurement to base ETA on yet. It must fall back to
    // ComputeInitialWatchdogBudget (30s + items*100ms = 30s + 0.3s ≈ 30s clamped to floor).
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 3, Completed: 1, Failed: 0, InProgress: 2),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var firstTick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 0,
      LastObservedAt = null,  // first re-arm — no prior measurement
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(firstTick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var ticks = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().ToList();
    await Assert.That(ticks.Count).IsEqualTo(1);
    await Assert.That(ticks[0].RescheduleCount).IsEqualTo(1);
    await Assert.That(ticks[0].ConsecutiveStallCount).IsEqualTo(0)
      .Because("no snapshot existed, so no delta could be computed — stall counter stays at 0.");
    await Assert.That(ticks[0].LastObservedAt).IsNotNull()
      .Because("the re-armed tick MUST carry the snapshot forward so the NEXT re-arm can compute delta.");
    await Assert.That(ticks[0].LastObservedCompleted).IsEqualTo(1);

    var elapsed = emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow;
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(28));
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(35))
      .Because("first re-arm falls back to ComputeInitialWatchdogBudget = 30s + items*100ms = 30.3s, clamped to MinWatchdogDelay floor of 30s.");
  }

  [Test]
  public async Task FirstReArm_InitialBudgetBelowTheFloor_ReArmsAtTheFloorAsync() {
    // A host that raises MinWatchdogDelay above the initial budget (30s + items*100ms) must still get
    // the floor it asked for: the clamp lifts a too-short delay up to the minimum, it does not only
    // cap a too-long one at the maximum.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 3, Completed: 1, Failed: 0, InProgress: 2),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 },
      options: new SagaOptions { MinWatchdogDelay = TimeSpan.FromMinutes(2) });

    var firstTick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 0,
      LastObservedAt = null,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(firstTick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var elapsed = emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow;
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(115))
      .Because("the initial budget of about 30s is below the 2-minute floor, so the floor wins");
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromMinutes(2));
  }

  [Test]
  public async Task ProgressBetweenTicks_NextDelayIsEtaBasedAsync() {
    // Previous tick saw 100 done; current sees 200 done over 10s elapsed → rate = 10/s.
    // 50 items remain → ETA = 5s, plus 30s safety margin = 35s. The floor (30s) is below
    // this so the result must be the ETA + margin.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 250, Completed: 200, Failed: 0, InProgress: 50),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 250 });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 2,
      LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10),
      LastObservedCompleted = 100,
      LastObservedFailed = 0,
      ConsecutiveStallCount = 0,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var nextTick = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(nextTick.ConsecutiveStallCount).IsEqualTo(0)
      .Because("progress was observed — stall counter must reset to 0.");
    await Assert.That(nextTick.LastObservedCompleted).IsEqualTo(200)
      .Because("the snapshot moves forward — next tick measures rate against current values.");

    var elapsed = emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow;
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(30))
      .Because("ETA (5s) + safety margin (30s) = 35s; clamped to MinWatchdogDelay (30s) floor minimum.");
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(40))
      .Because("ETA-based delay must NOT use the 8m / 30m exponential tiers — that defeats the point of the adaptive scheduler.");
  }

  [Test]
  public async Task NoProgressBetweenTicks_StallCounterIncrementsAndBacksOffAsync() {
    // No items completed between ticks (delta = 0). The framework must:
    // (1) Increment ConsecutiveStallCount.
    // (2) Widen the next interval exponentially: MinDelay * Multiplier^stallCount.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 10, Completed: 7, Failed: 0, InProgress: 3),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 10 });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 1,
      LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(45),
      LastObservedCompleted = 7,
      LastObservedFailed = 0,
      ConsecutiveStallCount = 0,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var nextTick = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(nextTick.ConsecutiveStallCount).IsEqualTo(1)
      .Because("zero delta between ticks — stall counter MUST increment so eventual abandon is reachable.");

    var elapsed = emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow;
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(58))
      .Because("stall 1 backoff = MinDelay (30s) * 2^1 = 60s — only stuck sagas back off exponentially.");
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(62));
  }

  [Test]
  public async Task MaxConsecutiveStalls_AbandonsAsync() {
    // ConsecutiveStallCount has reached MaxConsecutiveStalls-1 (3 with default 4). A FOURTH
    // stall on this tick must abandon — the saga is stuck and operators need to triage.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 10, Completed: 7, Failed: 0, InProgress: 3),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 10 });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 4,
      LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
      LastObservedCompleted = 7,
      LastObservedFailed = 0,
      ConsecutiveStallCount = 3,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.Abandoned);
    await Assert.That(emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Any()).IsFalse()
      .Because("no further re-arm past MaxConsecutiveStalls — operator triage required.");
    var abandoned = emitter.Published.OfType<SagaCompletionAbandonedEvent>().Single();
    await Assert.That(abandoned.StreamId).IsEqualTo(_sagaId);
    await Assert.That(abandoned.RescheduleCount).IsEqualTo(4)
      .Because("abandon carries the rescheduleCount of the LAST tick that observed the stall for operator forensics.");
  }

  [Test]
  public async Task ProgressAfterStalls_ResetsStallCounterAsync() {
    // ConsecutiveStallCount = 2 from prior ticks, BUT progress was just made.
    // The framework MUST reset stallCount to 0 — slow sagas that eventually move
    // forward shouldn't trigger abandon based on past inactivity.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 10, Completed: 8, Failed: 0, InProgress: 2),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 10 });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 3,
      LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10),
      LastObservedCompleted = 7,
      LastObservedFailed = 0,
      ConsecutiveStallCount = 2,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var nextTick = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(nextTick.ConsecutiveStallCount).IsEqualTo(0)
      .Because("progress observed — past stalls don't count anymore. Resetting prevents healthy-but-slow sagas from being abandoned.");
  }

  [Test]
  public async Task NextDelay_ClampedAtMaxAsync() {
    // Computed delay (long ETA from slow rate) must NOT exceed MaxWatchdogDelay.
    // 1 item done in 60s → rate = 0.0167/s. 999 remaining → ETA ≈ 16.6 hours.
    // Clamp ceiling is 30 min.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 1000, Completed: 1, Failed: 0, InProgress: 999),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 1000 });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 1,
      LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60),
      LastObservedCompleted = 0,
      LastObservedFailed = 0,
      ConsecutiveStallCount = 0,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var elapsed = emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow;
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(2))
      .Because("MaxWatchdogDelay = 30min ceiling. A long ETA from slow observed rate must be clamped or the saga goes hours without being re-checked.");
  }

  [Test]
  public async Task EveryItemTerminalButCompletionAlreadyDispatched_ReArmsAtTheFloorAsync() {
    // Recovery declines because the projection already carries CompletionEventDispatched (a
    // duplicate or late tick for a saga another pod already completed), yet the per-item
    // aggregate shows every item terminal. Progress WAS observed (150 items since the last
    // snapshot) so this is not a stall, but there is nothing outstanding to project an ETA
    // over. The scheduler has to fall through to "everything's done" semantics: re-arm at the
    // floor so the next tick re-checks completion promptly, and leave the stall counter alone.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 250, Completed: 250, Failed: 0, InProgress: 0),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel {
        Id = _sagaId,
        SagaName = SAGA_NAME,
        EntityId = _entityId,
        TotalItems = 250,
        CompletionEventDispatched = true,
      });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 2,
      LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10),
      LastObservedCompleted = 100,
      LastObservedFailed = 0,
      ConsecutiveStallCount = 0,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var nextTick = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(nextTick.ConsecutiveStallCount).IsEqualTo(0)
      .Because("150 items reached terminal since the previous snapshot — counting that as a stall would march a saga that is visibly finishing toward a spurious abandon.");

    var elapsed = emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow;
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(28));
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(35))
      .Because("with zero items outstanding there is no ETA to project, so the delay must be the MinWatchdogDelay floor (30s) — the stall tier (60s at stall 1) would leave an already-finished saga unchecked for twice as long.");
  }

  [Test]
  public async Task SnapshotTimestampAheadOfLocalClock_ReArmsAtTheFloorAsync() {
    // Multi-pod clock skew: the previous tick was stamped by a pod whose clock runs ahead, so
    // "elapsed since last observation" comes out negative on this pod. Progress is real
    // (100 -> 200) and 50 items are still outstanding, so the ETA branch would otherwise
    // divide by that negative interval. The guard has to reject the degenerate interval and
    // re-arm at the floor; a delay derived from a negative rate would schedule the next tick
    // in the past, which the outbox picks up immediately and spins on.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 250, Completed: 200, Failed: 0, InProgress: 50),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel {
        Id = _sagaId,
        SagaName = SAGA_NAME,
        EntityId = _entityId,
        TotalItems = 250,
        CompletionEventDispatched = true,
      });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 2,
      LastObservedAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5),  // skewed ahead
      LastObservedCompleted = 100,
      LastObservedFailed = 0,
      ConsecutiveStallCount = 0,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    await Assert.That(emitter.LastScheduledFor!.Value).IsGreaterThan(DateTimeOffset.UtcNow)
      .Because("a next-tick instant in the past is due the moment it is written, so the watchdog would re-fire in a tight loop.");

    var nextTick = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(nextTick.ConsecutiveStallCount).IsEqualTo(0)
      .Because("100 items reached terminal since the previous snapshot — a skewed timestamp must not be read as a stall.");

    var elapsed = emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow;
    await Assert.That(elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(28));
    await Assert.That(elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(35))
      .Because("the unusable interval falls back to the MinWatchdogDelay floor (30s), not to the stall tier (60s at stall 1) and not to an ETA computed from a negative rate.");
  }

  [Test]
  public async Task Complete_RecoversWithoutReArmAsync() {
    // Aggregate already shows complete; TryRecoverViaWatchdogAsync drives the
    // emission and the tick receptor MUST NOT re-arm.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 3, Completed: 3, Failed: 0, InProgress: 0),
        items: [
          new SagaItemModel { Id = Guid.NewGuid(), SagaId = _sagaId, SagaName = SAGA_NAME, ItemIdentifier = "a", State = SagaItemState.Completed },
          new SagaItemModel { Id = Guid.NewGuid(), SagaId = _sagaId, SagaName = SAGA_NAME, ItemIdentifier = "b", State = SagaItemState.Completed },
          new SagaItemModel { Id = Guid.NewGuid(), SagaId = _sagaId, SagaName = SAGA_NAME, ItemIdentifier = "c", State = SagaItemState.Completed },
        ]),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 0,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(tick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.Recovered);
    await Assert.That(emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Any()).IsFalse();
    await Assert.That(emitter.Published.OfType<SagaCompletionAbandonedEvent>().Any()).IsFalse();
    await Assert.That(emitter.Published.OfType<TestCompletedEvent>().Count()).IsEqualTo(1);
  }

  /// <summary>
  /// A tick delivered to the framework's router reaches a hand-written saga service and drives it
  /// through the same recovery lifecycle a generated receiver would.
  /// </summary>
  /// <remarks>
  /// This is the path that was missing. A hand-written saga armed its watchdog, the tick was
  /// delivered on time, and with no receiver it was discarded — so the recovery below never ran and a
  /// stranded saga was never completed, re-armed or abandoned.
  /// </remarks>
  [Test]
  public async Task RoutedTick_ReachesAHandWrittenSagaService_AndReArmsAsync() {
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 3, Completed: 1, Failed: 0, InProgress: 2),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });
    var services = new ServiceCollection();
    services.AddScoped<ISagaWatchdogParticipant>(_ => svc);
    await using var sp = services.BuildServiceProvider();
    var router = new SagaWatchdogTickRouter(sp.GetRequiredService<IServiceScopeFactory>());

    await Assert.That(((ISagaWatchdogParticipant)svc).SagaName).IsEqualTo(SAGA_NAME)
      .Because("the router addresses ticks by the name the service armed them with");

    await router.HandleAsync(new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 0,
    }, CancellationToken.None);

    var reArmed = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(reArmed.RescheduleCount).IsEqualTo(1)
      .Because("a saga still in progress re-arms rather than being left without a next wake-up");
    await Assert.That(emitter.LastScheduledFor).IsNotNull()
      .Because("the re-arm is scheduled for a future time, not fired at once");
  }

  // ── Stranded items: a lost worker is resolved, not the whole saga abandoned ──

  private static SagaCompletionWatchdogTickEvent _tickAtTheStallLimit(int completed, int failed) => new() {
    StreamId = _sagaId,
    SagaName = SAGA_NAME,
    EntityId = _entityId,
    RescheduleCount = 4,
    LastObservedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10),
    LastObservedCompleted = completed,
    LastObservedFailed = failed,
    ConsecutiveStallCount = 3,
  };

  private static SagaItemModel _item(string id, SagaItemState state) => new() {
    SagaId = _sagaId,
    SagaName = SAGA_NAME,
    ItemIdentifier = id,
    DisplayName = "Item " + id,
    State = state,
    StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(12),
    AttemptCount = 1,
  };

  /// <summary>
  /// An item whose worker was lost is failed with a reason, so the saga can finish; the saga itself
  /// is not abandoned for the sake of one item.
  /// </summary>
  /// <remarks>
  /// Reaching the stall limit means no item has moved across every watchdog check. An item still
  /// non-terminal then, with no terminal event in the store either, was being processed by a worker
  /// that is gone: nothing will ever finish it. Abandoning the saga discarded every item that did
  /// finish along with it.
  /// </remarks>
  [Test]
  public async Task MaxConsecutiveStalls_WithAStrandedItem_FailsItAndReArmsInsteadOfAbandoningAsync() {
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 3, Completed: 2, Failed: 0, InProgress: 1),
        items: [_item("a", SagaItemState.Completed), _item("b", SagaItemState.Completed), _item("c", SagaItemState.Running)]),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(2, 0), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    await Assert.That(emitter.Published.OfType<SagaCompletionAbandonedEvent>()).IsEmpty()
      .Because("one lost item must not throw away the items that finished");
    var failed = emitter.Published.OfType<TestItemFailedEvent>().Single();
    await Assert.That(failed.ItemIdentifier).IsEqualTo("c");
    await Assert.That(failed.ErrorMessage).Contains("lost")
      .Because("the failure has to say why it happened, or it reads as a domain error in the item itself");
    var next = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(next.ConsecutiveStallCount).IsEqualTo(0)
      .Because("the failure needs time to land before the saga is judged stuck again");
  }

  /// <summary>
  /// Of two items left running, only the one with no terminal event anywhere is failed; the one the
  /// store already records as finished is left for the reconciler.
  /// </summary>
  /// <remarks>
  /// Both look identical in the item projection. The store tells them apart: one finished and only
  /// its projection row is behind, the other lost its worker. Failing the first would record a
  /// failure for work that succeeded.
  /// </remarks>
  [Test]
  public async Task MaxConsecutiveStalls_ItemAlreadyTerminalInTheStore_IsNotFailedAgainAsync() {
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 3, Completed: 1, Failed: 0, InProgress: 2),
        items: [_item("a", SagaItemState.Completed), _item("finished", SagaItemState.Running), _item("lost", SagaItemState.Running)]),
      terminalReader: new KeyedTerminalReader(new Dictionary<Guid, SagaItemTerminalOutcome> {
        [SagaItemStreams.Of(_sagaId, "finished")] = SagaItemTerminalOutcome.Completed,
      }),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(1, 0), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed)
      .Because("the precondition: the saga is not yet reconcilable, so resolution actually runs");
    await Assert.That(emitter.Published.OfType<TestItemFailedEvent>().Select(e => e.ItemIdentifier)).IsEquivalentTo(["lost"])
      .Because("the store already records 'finished' as done; failing it would report a failure for work that succeeded");
  }

  [Test]
  public async Task MaxConsecutiveStalls_ConsumerRedrivesTheItem_ItIsNotFailedAsync() {
    var emitter = new RecordingEmitter();
    var redriven = new List<string>();
    var svc = new TestSagaService(
      emitter,
      new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 2, Completed: 1, Failed: 0, InProgress: 1),
        items: [_item("a", SagaItemState.Completed), _item("b", SagaItemState.Running)]),
      new FakeTerminalReader(),
      new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 2 }) {
      Redrive = item => { redriven.Add(item.ItemIdentifier); return true; },
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(1, 0), CancellationToken.None);

    await Assert.That(redriven).IsEquivalentTo(["b"]);
    await Assert.That(emitter.Published.OfType<TestItemFailedEvent>()).IsEmpty()
      .Because("a service that can re-dispatch the work decides; the item stays in progress");
    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
  }

  /// <summary>
  /// The whole path for a worker lost mid-item: the saga ends completed with one failure, never
  /// abandoned and never left running.
  /// </summary>
  /// <remarks>
  /// Before, a saga in this state sat at "in progress" indefinitely: the item was marked started,
  /// the message that would finish it was gone with its worker, nothing retried it and nothing
  /// dead-lettered it. Now the stall limit resolves the item and the next check completes the saga.
  /// </remarks>
  [Test]
  public async Task StrandedByALostWorker_EndsCompletedWithOneFailure_NotAbandonedAsync() {
    var repository = new MutableItemRepository(
      new SagaItemAggregate(Total: 3, Completed: 2, Failed: 0, InProgress: 1),
      [_item("a", SagaItemState.Completed), _item("b", SagaItemState.Completed), _item("c", SagaItemState.Running)]);
    var (svc, emitter) = _buildService(repository, new FakeTerminalReader(),
      new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var first = await svc.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(2, 0), CancellationToken.None);
    var next = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();

    // The failed-item event lands, and the item projection catches up with it.
    repository.Agg = new SagaItemAggregate(Total: 3, Completed: 2, Failed: 1, InProgress: 0);
    repository.Items = [_item("a", SagaItemState.Completed), _item("b", SagaItemState.Completed), _item("c", SagaItemState.Failed)];
    var second = await svc.TryRecoverViaWatchdogTickAsync(next, CancellationToken.None);

    await Assert.That(first).IsEqualTo(WatchdogTickOutcome.ReArmed);
    await Assert.That(second).IsEqualTo(WatchdogTickOutcome.Recovered);
    await Assert.That(emitter.Published.OfType<SagaCompletionAbandonedEvent>()).IsEmpty();
    var completed = emitter.Published.OfType<TestCompletedEvent>().Single();
    await Assert.That(completed.CompletedItems).IsEqualTo(2);
    await Assert.That(completed.FailedItems).IsEqualTo(1)
      .Because("the saga reports the lost item as a failure rather than hiding it or hanging on it");
  }

  /// <summary>
  /// When failing the stranded item is what finishes the saga, it completes on the same tick rather
  /// than waiting for another.
  /// </summary>
  [Test]
  public async Task MaxConsecutiveStalls_FailingTheStrandedItemFinishesTheSaga_CompletesAtOnceAsync() {
    var repository = new MutableItemRepository(
      new SagaItemAggregate(Total: 2, Completed: 1, Failed: 0, InProgress: 1),
      [_item("a", SagaItemState.Completed), _item("b", SagaItemState.Running)]);
    // The item projection applies the failure as soon as it is published, as an inline projection would.
    var emitter = new RecordingEmitter {
      OnPublished = evt => {
        if (evt is TestItemFailedEvent) {
          repository.Agg = new SagaItemAggregate(Total: 2, Completed: 1, Failed: 1, InProgress: 0);
          repository.Items = [_item("a", SagaItemState.Completed), _item("b", SagaItemState.Failed)];
        }
      },
    };
    var svc = new TestSagaService(emitter, repository, new FakeTerminalReader(),
      new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 2 });

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(1, 0), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.Recovered);
    await Assert.That(emitter.Published.OfType<SagaCompletionWatchdogTickEvent>()).IsEmpty()
      .Because("a finished saga needs no further wake-up");
    await Assert.That(emitter.Published.OfType<TestCompletedEvent>().Single().FailedItems).IsEqualTo(1);
  }

  /// <summary>
  /// Without an item repository nothing can be enumerated, so the stall limit abandons exactly as it
  /// always did.
  /// </summary>
  [Test]
  public async Task MaxConsecutiveStalls_WithNoItemRepository_AbandonsAsBeforeAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter, itemRepository: null!, new FakeTerminalReader(),
      new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(0, 0), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.Abandoned);
    await Assert.That(emitter.Published.OfType<TestItemFailedEvent>()).IsEmpty();
  }

  /// <summary>
  /// Without a terminal reader there is no store to consult, and a stranded item is still resolved.
  /// </summary>
  [Test]
  public async Task MaxConsecutiveStalls_WithNoTerminalReader_StillFailsTheStrandedItemAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter,
      new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 2, Completed: 1, Failed: 0, InProgress: 1),
        items: [_item("a", SagaItemState.Completed), _item("b", SagaItemState.Pending)]),
      terminalReader: null!,
      new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 2 });

    await svc.TryRecoverViaWatchdogTickAsync(_tickAtTheStallLimit(1, 0), CancellationToken.None);

    await Assert.That(emitter.Published.OfType<TestItemFailedEvent>().Single().ItemIdentifier).IsEqualTo("b")
      .Because("an item never dispatched is as stranded as one whose worker died");
  }

  // ── Builder + test doubles ─────────────────────────────────────────────

  private static (TestSagaService, RecordingEmitter) _buildService(
      ISagaItemRepository itemRepository,
      ISagaItemTerminalReader terminalReader,
      BaseSagaModel projection,
      SagaOptions? options = null) {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter, itemRepository, terminalReader, projection, options);
    return (svc, emitter);
  }

  private sealed class FakeItemRepository(SagaItemAggregate agg, IReadOnlyList<SagaItemModel> items) : ISagaItemRepository {
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(agg);
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(items);
  }

  private sealed class FakeTerminalReader : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken)
      => Task.FromResult(SagaItemTerminalOutcome.NotTerminal);
  }

  private sealed class KeyedTerminalReader(IReadOnlyDictionary<Guid, SagaItemTerminalOutcome> outcomes) : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken)
      => Task.FromResult(outcomes.TryGetValue(perItemStreamId, out var outcome) ? outcome : SagaItemTerminalOutcome.NotTerminal);
  }

  private sealed class MutableItemRepository(SagaItemAggregate agg, IReadOnlyList<SagaItemModel> items) : ISagaItemRepository {
    public SagaItemAggregate Agg { get; set; } = agg;
    public IReadOnlyList<SagaItemModel> Items { get; set; } = items;
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(Agg);
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(Items);
  }

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public List<IEvent> Published { get; } = [];
    public DateTimeOffset? LastScheduledFor { get; private set; }
    public Action<IEvent>? OnPublished { get; init; }
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Published.Add(eventData);
      OnPublished?.Invoke(eventData);
      return Task.CompletedTask;
    }
    public Task PublishAsync<TEvent>(TEvent eventData, DateTimeOffset? scheduledFor) where TEvent : IEvent {
      Published.Add(eventData);
      LastScheduledFor = scheduledFor;
      return Task.CompletedTask;
    }
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.FromResult(true);
    }
  }

  // Test event types match the TryRecoverViaWatchdogAsyncTests fixture; this is
  // intentionally duplicated to keep each test fixture self-contained.

  private sealed class TestInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public IReadOnlyList<string>? HookNames { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class TestItemsDispatchedEvent : ISagaItemsDispatchedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public int TotalItems { get; set; }
    public int SuccessfullyDispatched { get; set; }
    public int FailedToDispatch { get; set; }
  }
  private sealed class TestItemStartedEvent : ISagaItemStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class TestItemCompletedEvent : ISagaItemCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class TestItemFailedEvent : ISagaItemFailedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetails { get; set; }
  }
  private sealed class TestCompletedEvent : ISagaCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public SagaStatus FinalStatus { get; set; }
    public string? CompletedByItemIdentifier { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class TestResetEvent : ISagaResetEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public SagaItemState PreviousStatus { get; set; }
  }
  private sealed class TestHookStartedEvent : ISagaHookStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class TestHookCompletedEvent : ISagaHookCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
    public SagaItemState Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
  }

  private sealed class TestSagaService(
      ISagaEventEmitter emitter,
      ISagaItemRepository itemRepository,
      ISagaItemTerminalReader terminalReader,
      BaseSagaModel projection,
      SagaOptions? options = null)
    : BaseSagaService<TestInitiatedEvent, TestItemsDispatchedEvent, TestItemStartedEvent, TestItemCompletedEvent,
                      TestItemFailedEvent, TestCompletedEvent, TestResetEvent, TestHookStartedEvent, TestHookCompletedEvent>(
        SAGA_NAME, emitter, itemRepository, terminalReader, options, NullLogger<TestSagaService>.Instance) {

    private readonly BaseSagaModel _projection = projection;

    /// <summary>When set, stands in for a service that can re-dispatch a stranded item's work.</summary>
    public Func<SagaItemModel, bool>? Redrive { get; init; }

    protected override Task<bool> TryRedriveStrandedItemAsync(SagaContext ctx, SagaItemModel item, CancellationToken cancellationToken)
      => Redrive is null ? base.TryRedriveStrandedItemAsync(ctx, item, cancellationToken) : Task.FromResult(Redrive(item));

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult<BaseSagaModel?>(_projection);

    protected override TestInitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
    protected override TestItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
    protected override TestItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override TestItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override TestItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
    protected override TestCompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
    protected override TestResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
    protected override TestHookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };
    protected override TestHookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }
}
