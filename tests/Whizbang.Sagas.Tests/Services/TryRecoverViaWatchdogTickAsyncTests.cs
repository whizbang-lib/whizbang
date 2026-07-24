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

  // ── Builder + test doubles ─────────────────────────────────────────────

  private static (TestSagaService, RecordingEmitter) _buildService(
      FakeItemRepository itemRepository,
      FakeTerminalReader terminalReader,
      BaseSagaModel projection) {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter, itemRepository, terminalReader, projection);
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

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public List<IEvent> Published { get; } = [];
    public DateTimeOffset? LastScheduledFor { get; private set; }
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Published.Add(eventData);
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
      BaseSagaModel projection)
    : BaseSagaService<TestInitiatedEvent, TestItemsDispatchedEvent, TestItemStartedEvent, TestItemCompletedEvent,
                      TestItemFailedEvent, TestCompletedEvent, TestResetEvent, TestHookStartedEvent, TestHookCompletedEvent>(
        SAGA_NAME, emitter, itemRepository, terminalReader, NullLogger<TestSagaService>.Instance) {

    private readonly BaseSagaModel _projection = projection;

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
