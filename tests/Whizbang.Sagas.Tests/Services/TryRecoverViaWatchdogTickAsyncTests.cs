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
  public async Task NotYetComplete_ReArmsAtScheduleIndexZeroAsync() {
    // Saga still in progress: aggregate says only 1/3 done, reconciliation says
    // genuinely-in-progress so TryRecoverViaWatchdogAsync returns false. The
    // tick receptor must then publish the next tick with RescheduleCount=1 and
    // scheduledFor at T+schedule[0].
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 1, Completed: 1, Failed: 0, InProgress: 0),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var firstTick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 0,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(firstTick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.ReArmed);
    var ticks = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().ToList();
    await Assert.That(ticks.Count).IsEqualTo(1);
    await Assert.That(ticks[0].RescheduleCount).IsEqualTo(1);
    await Assert.That(ticks[0].StreamId).IsEqualTo(_sagaId);
    await Assert.That(ticks[0].SagaName).IsEqualTo(SAGA_NAME);
    // Scheduled at default schedule[0] = 30s.
    await Assert.That(emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow)
      .IsLessThan(TimeSpan.FromSeconds(31));
    await Assert.That(emitter.LastScheduledFor!.Value - DateTimeOffset.UtcNow)
      .IsGreaterThan(TimeSpan.FromSeconds(28));
  }

  [Test]
  public async Task NotYetComplete_ScheduleExhausted_AbandonsAsync() {
    // RescheduleCount=4 means we've already burned through the 4-entry default
    // schedule (0..3). The next call must abandon — emit
    // SagaCompletionAbandonedEvent, NOT another tick.
    var (svc, emitter) = _buildService(
      itemRepository: new FakeItemRepository(
        agg: new SagaItemAggregate(Total: 1, Completed: 1, Failed: 0, InProgress: 0),
        items: []),
      terminalReader: new FakeTerminalReader(),
      projection: new BaseSagaModel { Id = _sagaId, SagaName = SAGA_NAME, EntityId = _entityId, TotalItems = 3 });

    var lastTick = new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      RescheduleCount = 4,
    };

    var outcome = await svc.TryRecoverViaWatchdogTickAsync(lastTick, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(WatchdogTickOutcome.Abandoned);
    await Assert.That(emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Any()).IsFalse()
      .Because("no further tick should be re-armed past schedule exhaustion");
    var abandoned = emitter.Published.OfType<SagaCompletionAbandonedEvent>().Single();
    await Assert.That(abandoned.StreamId).IsEqualTo(_sagaId);
    await Assert.That(abandoned.SagaName).IsEqualTo(SAGA_NAME);
    await Assert.That(abandoned.EntityId).IsEqualTo(_entityId);
    await Assert.That(abandoned.RescheduleCount).IsEqualTo(4);
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
