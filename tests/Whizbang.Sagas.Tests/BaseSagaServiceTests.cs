using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the publish-event-per-lifecycle-step contract on
/// <see cref="BaseSagaService{TInit, TItemsDispatched, TItemStarted, TItemCompleted, TItemFailed, TCompleted, TReset, THookStarted, THookCompleted}"/>.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class BaseSagaServiceTests {

  private const string SAGA_NAME = "TestSaga";
  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _entityId = Guid.Parse("22222222-2222-2222-2222-222222222222");

  // ── InitiateSagaAsync emits InitiatedEvent ───────────────────────────

  [Test]
  public async Task InitiateSagaAsync_PublishesInitiatedEventWithItemListAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter);

    await svc.InitiateSagaAsync(new SagaContext(_sagaId, _entityId), itemIdentifiers: ["a", "b", "c"], hookNames: null, CancellationToken.None);

    // Initiated event + framework-armed watchdog tick (the second emission, see
    // CompletionOrchestrationGapTests.InitiateSagaAsync_AutoArmsWatchdogTickAsync).
    await Assert.That(emitter.Published.Count).IsEqualTo(2);
    var evt = (TestInitiatedEvent)emitter.Published[0];
    await Assert.That(evt.SagaName).IsEqualTo(SAGA_NAME);
    await Assert.That(evt.EntityId).IsEqualTo(_entityId);
    await Assert.That(evt.ItemIdentifiers.Count).IsEqualTo(3);
    await Assert.That(evt.TotalItems).IsEqualTo(3);
    await Assert.That(emitter.Published[1]).IsTypeOf<SagaCompletionWatchdogTickEvent>();
  }

  [Test]
  public async Task InitiateSagaAsync_NullHookNames_DoesNotThrowAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter);

    await svc.InitiateSagaAsync(new SagaContext(_sagaId, _entityId), itemIdentifiers: ["only"], hookNames: null, CancellationToken.None);

    var evt = (TestInitiatedEvent)emitter.Published[0];
    await Assert.That(evt.HookNames is null || evt.HookNames.Count == 0).IsTrue();
  }

  // ── ItemsDispatchedAsync ─────────────────────────────────────────────

  [Test]
  public async Task ItemsDispatchedAsync_PublishesCountsAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter);

    await svc.ItemsDispatchedAsync(new SagaContext(_sagaId, _entityId), totalItems: 100, successfullyDispatched: 98, failedToDispatch: 2, CancellationToken.None);

    var evt = (TestItemsDispatchedEvent)emitter.Published[0];
    await Assert.That(evt.TotalItems).IsEqualTo(100);
    await Assert.That(evt.SuccessfullyDispatched).IsEqualTo(98);
    await Assert.That(evt.FailedToDispatch).IsEqualTo(2);
  }

  // ── UpdateItemAsync routes by status ─────────────────────────────────

  [Test]
  public async Task UpdateItemAsync_Running_PublishesItemStartedEventAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter);

    await svc.UpdateItemAsync(new SagaContext(_sagaId, _entityId), itemIdentifier: "item-1", SagaItemState.Running, displayName: "Item 1", CancellationToken.None);

    await Assert.That(emitter.Published[0]).IsTypeOf<TestItemStartedEvent>();
    var evt = (TestItemStartedEvent)emitter.Published[0];
    await Assert.That(evt.ItemIdentifier).IsEqualTo("item-1");
    await Assert.That(evt.DisplayName).IsEqualTo("Item 1");
  }

  [Test]
  public async Task UpdateItemAsync_Completed_PublishesItemCompletedEventAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter);

    await svc.UpdateItemAsync(new SagaContext(_sagaId, _entityId), itemIdentifier: "item-1", SagaItemState.Completed, displayName: null, CancellationToken.None);

    await Assert.That(emitter.Published[0]).IsTypeOf<TestItemCompletedEvent>();
  }

  [Test]
  public async Task UpdateItemAsync_Failed_ThrowsBecauseRequiresErrorContextAsync() {
    var svc = new TestSagaService(new RecordingEmitter());

    await Assert.That(() => svc.UpdateItemAsync(new SagaContext(_sagaId, _entityId), itemIdentifier: "item-1", SagaItemState.Failed, displayName: null, CancellationToken.None))
      .ThrowsExactly<InvalidOperationException>()
      .Because("Failed transitions carry error context; forcing callers through FailItemAsync prevents silently dropping error info on the failed path.");
  }

  // ── FailItemAsync ────────────────────────────────────────────────────

  [Test]
  public async Task FailItemAsync_PublishesItemFailedEventWithErrorAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter);

    await svc.FailItemAsync(new SagaContext(_sagaId, _entityId), itemIdentifier: "item-1", errorMessage: "boom", errorDetails: "stack", displayName: "Item 1", CancellationToken.None);

    var evt = (TestItemFailedEvent)emitter.Published[0];
    await Assert.That(evt.ErrorMessage).IsEqualTo("boom");
    await Assert.That(evt.ErrorDetails).IsEqualTo("stack");
  }

  // ── CompleteSagaAsync uses PublishOnce with claim-guard ──────────────

  [Test]
  public async Task CompleteSagaAsync_RoutesThroughPublishOnceWithSagaClaimKeyAsync() {
    var emitter = new RecordingEmitter();
    var svc = new TestSagaService(emitter);

    await svc.CompleteSagaAsync(new SagaContext(_sagaId, _entityId), finalStatus: SagaStatus.Completed, completedByItemIdentifier: "item-last", completedItems: 10, failedItems: 0, totalItems: 10, CancellationToken.None);

    await Assert.That(emitter.PublishedOnce.Count).IsEqualTo(1)
      .Because("CompleteSagaAsync must use PublishOnceAsync — not PublishAsync — so concurrent terminal handlers collapse to exactly one emission.");
    await Assert.That(emitter.PublishedOnce[0].claimKey).IsEqualTo($"saga-completed:{SAGA_NAME}:{_sagaId}")
      .Because("The claim key follows the SagaCompletionGuard convention; routing through any other shape would collide with other emitters or split the claim across saga types.");
  }

  // ── Recording fake ───────────────────────────────────────────────────

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public List<IEvent> Published { get; } = [];
    public List<(string claimKey, IEvent evt)> PublishedOnce { get; } = [];

    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Published.Add(eventData!);
      return Task.CompletedTask;
    }
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      PublishedOnce.Add((claimKey, eventData!));
      return Task.FromResult(true);
    }
  }

  // ── Test event types (implement Whizbang.Sagas.Contracts interfaces) ─

  private sealed class TestInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public int TotalItems { get; set; }
    public IReadOnlyList<string>? HookNames { get; set; }
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

  // ── Watchdog recovery guards ──────────────────────────────────────────

  /// <summary>A service whose projection lookup returns whatever the test hands it.</summary>
  private sealed class ProjectionSagaService(ISagaEventEmitter emitter, BaseSagaModel? projection)
    : BaseSagaService<TestInitiatedEvent, TestItemsDispatchedEvent, TestItemStartedEvent, TestItemCompletedEvent,
                      TestItemFailedEvent, TestCompletedEvent, TestResetEvent, TestHookStartedEvent, TestHookCompletedEvent>(
        SAGA_NAME, emitter, NullLogger<ProjectionSagaService>.Instance) {

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(projection);


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

  [Test]
  public async Task Watchdog_WhenCompletionAlreadyDispatched_DoesNotEmitAgainAsync() {
    // The watchdog exists to rescue a saga whose completion never went out. Re-emitting one that
    // DID go out is the opposite failure: every consumer of the completion event sees it twice,
    // and for a saga that triggers downstream work that means the work runs twice.
    var emitter = new RecordingEmitter();
    var saga = new BaseSagaModel {
      Id = Guid.CreateVersion7(),
      SagaName = SAGA_NAME,
      TotalItems = 3,
      CompletedItems = 3,
      CompletionEventDispatched = true,
    };
    var service = new ProjectionSagaService(emitter, saga);

    var recovered = await service.TryRecoverViaWatchdogAsync(
      new SagaContext { SagaId = saga.Id }, CancellationToken.None);

    await Assert.That(recovered).IsFalse()
      .Because("a completion that already went out must not be re-emitted — downstream consumers "
             + "would run the work it triggers a second time");
  }

  [Test]
  public async Task Watchdog_WithNoProjection_ReportsNothingToRecoverAsync() {
    // No projection means the watchdog cannot tell whether the saga is done, and guessing would
    // emit a completion for a saga still in progress.
    var service = new ProjectionSagaService(new RecordingEmitter(), projection: null);

    var recovered = await service.TryRecoverViaWatchdogAsync(
      new SagaContext { SagaId = Guid.CreateVersion7() }, CancellationToken.None);

    await Assert.That(recovered).IsFalse();
  }

  [Test]
  public async Task Watchdog_WithItemsStillOutstanding_DoesNotCompleteEarlyAsync() {
    // The backwards-compatible path trusts the projection's counts. Completing while items remain
    // outstanding closes a saga whose work has not finished.
    var saga = new BaseSagaModel {
      Id = Guid.CreateVersion7(),
      SagaName = SAGA_NAME,
      TotalItems = 5,
      CompletedItems = 2,
      FailedItems = 1,
    };
    var service = new ProjectionSagaService(new RecordingEmitter(), saga);

    var recovered = await service.TryRecoverViaWatchdogAsync(
      new SagaContext { SagaId = saga.Id }, CancellationToken.None);

    await Assert.That(recovered).IsFalse()
      .Because("three of five items are terminal — completing here closes a saga whose work is "
             + "still running");
  }

  // ── Test saga service (subclass of BaseSagaService) ──────────────────

  private sealed class TestSagaService(ISagaEventEmitter emitter)
    : BaseSagaService<TestInitiatedEvent, TestItemsDispatchedEvent, TestItemStartedEvent, TestItemCompletedEvent,
                      TestItemFailedEvent, TestCompletedEvent, TestResetEvent, TestHookStartedEvent, TestHookCompletedEvent>(
        SAGA_NAME, emitter, NullLogger<TestSagaService>.Instance) {

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
