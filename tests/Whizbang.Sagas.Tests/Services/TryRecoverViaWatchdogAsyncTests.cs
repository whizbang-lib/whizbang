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
/// Locks <see cref="BaseSagaService{T1,T2,T3,T4,T5,T6,T7,T8,T9}.TryRecoverViaWatchdogAsync"/>'s
/// slow-path completion decision under cross-pod fan-out — the framework MUST
/// drive completion from its own per-item aggregate (via
/// <see cref="SagaItemCompletionReconciler"/> over a consumer-supplied
/// <see cref="ISagaItemRepository"/> + <see cref="ISagaItemTerminalReader"/>),
/// not the consumer's saga projection's <c>CompletedItems</c> field.
///
/// <para>Background: per-item terminal events ride per-item streams
/// (<see cref="SagaItemStreams.ResolveStreamId"/>), so a consumer saga
/// projection's <c>Apply</c> chain — keyed on the saga's stream — never sees
/// them. Its <c>CompletedItems</c> field stays at 0 even when every per-item
/// row is terminal in the durable event store. If the framework reads only
/// the consumer's <see cref="BaseSagaService{T1,T2,T3,T4,T5,T6,T7,T8,T9}.LoadProjectionAsync"/>
/// it sees a stale 0/N and incorrectly decides the saga is still in progress
/// under cross-pod fan-out (where the in-memory tracker is per-pod sharded
/// and so can never reach Total either).</para>
///
/// <para>The fix wires <see cref="ISagaItemRepository"/> + <see cref="ISagaItemTerminalReader"/>
/// via the constructor; <c>TryRecoverViaWatchdogAsync</c>'s slow path then
/// calls <see cref="SagaItemCompletionReconciler.ResolveCompletionCountsAsync"/>
/// to compute authoritative counts and emits <c>SagaCompletedEvent</c> via
/// <c>PublishOnceAsync</c>.</para>
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class TryRecoverViaWatchdogAsyncTests {

  private const string SAGA_NAME = "TestSaga";
  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _entityId = Guid.Parse("22222222-2222-2222-2222-222222222222");

  [Test]
  public async Task SlowPath_WiredItemAggregate_BypassesStaleProjectionCountsAsync() {
    // The consumer projection lies: CompletedItems=0 even though every per-item row
    // is terminal. Without this fix, slow-path TryRecover sees 0/3 and returns false.
    var staleProjection = new BaseSagaModel {
      Id = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      TotalItems = 3,
      CompletedItems = 0,            // stale — the bug
      FailedItems = 0,
      CompletionEventDispatched = false,
    };

    var itemRepo = new FakeItemRepository(
      // Per-item aggregate: 3 rows, all terminal (Completed).
      aggregate: new SagaItemAggregate(Total: 3, Completed: 3, Failed: 0, InProgress: 0),
      items: [
        new SagaItemModel { Id = Guid.NewGuid(), SagaId = _sagaId, SagaName = SAGA_NAME, ItemIdentifier = "a", State = SagaItemState.Completed },
        new SagaItemModel { Id = Guid.NewGuid(), SagaId = _sagaId, SagaName = SAGA_NAME, ItemIdentifier = "b", State = SagaItemState.Completed },
        new SagaItemModel { Id = Guid.NewGuid(), SagaId = _sagaId, SagaName = SAGA_NAME, ItemIdentifier = "c", State = SagaItemState.Completed },
      ]);
    // Terminal reader is only invoked on the slow-path reconciliation tier; all rows
    // are already terminal in the projection, so the fast aggregate path returns
    // (3, 0) without reading the event store at all.
    var terminalReader = new FakeTerminalReader();
    var emitter = new RecordingEmitter();

    var svc = new TestSagaService(emitter, itemRepo, terminalReader, projectionOverride: staleProjection);

    var recovered = await svc.TryRecoverViaWatchdogAsync(
      new SagaContext(_sagaId, _entityId), CancellationToken.None);

    await Assert.That(recovered).IsTrue()
      .Because("authoritative item-aggregate counts say 3/3 done; framework must drive completion");
    await Assert.That(emitter.Published.OfType<TestCompletedEvent>().Count())
      .IsEqualTo(1)
      .Because("the slow path must publish exactly one SagaCompletedEvent via the framework's aggregate");
    var completed = emitter.Published.OfType<TestCompletedEvent>().Single();
    await Assert.That(completed.CompletedItems).IsEqualTo(3);
    await Assert.That(completed.FailedItems).IsEqualTo(0);
    await Assert.That(completed.TotalItems).IsEqualTo(3);
    await Assert.That(completed.FinalStatus).IsEqualTo(SagaStatus.Completed);
  }

  [Test]
  public async Task SlowPath_NoItemAggregateWired_FallsBackToLoadProjectionAsync() {
    // Backwards-compat: when ISagaItemRepository / ISagaItemTerminalReader are not
    // supplied, the framework keeps today's behavior — read the consumer projection
    // directly.
    var trustworthyProjection = new BaseSagaModel {
      Id = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      TotalItems = 2,
      CompletedItems = 2,
      FailedItems = 0,
      CompletionEventDispatched = false,
    };
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter, itemRepository: null, terminalReader: null, projectionOverride: trustworthyProjection);

    var recovered = await svc.TryRecoverViaWatchdogAsync(
      new SagaContext(_sagaId, _entityId), CancellationToken.None);

    await Assert.That(recovered).IsTrue();
    await Assert.That(emitter.Published.OfType<TestCompletedEvent>().Count()).IsEqualTo(1);
  }

  // ── Test doubles ────────────────────────────────────────────────────────

  private sealed class FakeItemRepository(SagaItemAggregate aggregate, IReadOnlyList<SagaItemModel> items) : ISagaItemRepository {
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(aggregate);
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(items);
  }

  private sealed class FakeTerminalReader : ISagaItemTerminalReader {
    public Task<SagaItemTerminalOutcome> CheckAsync(Guid perItemStreamId, CancellationToken cancellationToken)
      => Task.FromResult(SagaItemTerminalOutcome.NotTerminal);
  }

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public List<IEvent> Published { get; } = [];
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.CompletedTask;
    }
    public Task PublishAsync<TEvent>(TEvent eventData, DateTimeOffset? scheduledFor) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.CompletedTask;
    }
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      Published.Add(eventData);
      return Task.FromResult(true);
    }
  }

  // ── Concrete saga service ───────────────────────────────────────────────

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
      ISagaItemRepository? itemRepository,
      ISagaItemTerminalReader? terminalReader,
      BaseSagaModel? projectionOverride)
    : BaseSagaService<TestInitiatedEvent, TestItemsDispatchedEvent, TestItemStartedEvent, TestItemCompletedEvent,
                      TestItemFailedEvent, TestCompletedEvent, TestResetEvent, TestHookStartedEvent, TestHookCompletedEvent>(
        SAGA_NAME, emitter, itemRepository, terminalReader, NullLogger<TestSagaService>.Instance) {

    private readonly BaseSagaModel? _projectionOverride = projectionOverride;

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult<BaseSagaModel?>(_projectionOverride);

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
