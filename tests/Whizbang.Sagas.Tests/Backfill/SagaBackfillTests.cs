using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Coverage;

/// <summary>
/// Backfill tests covering surfaces that the focused suites missed:
/// attribute property roundtripping, SagaContext defaults, and
/// BaseSagaService.ResetItemAsync / TryRunHookAsync paths that the
/// lifecycle suite didn't hit.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaBackfillTests {

  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _entityId = Guid.Parse("22222222-2222-2222-2222-222222222222");

  // ── SagaAttribute(non-generic) properties ────────────────────────────

  [Test]
  public async Task SagaAttribute_StoresAllPropertiesAsync() {
    var attr = new SagaAttribute("MySaga") { IncludeHooks = false, GenerateService = false };

    await Assert.That(attr.SagaName).IsEqualTo("MySaga");
    await Assert.That(attr.IncludeHooks).IsFalse();
    await Assert.That(attr.GenerateService).IsFalse();
  }

  [Test]
  public async Task SagaAttribute_DefaultsHooksAndServiceTrueAsync() {
    var attr = new SagaAttribute("MySaga");

    await Assert.That(attr.IncludeHooks).IsTrue();
    await Assert.That(attr.GenerateService).IsTrue();
  }

  // ── SagaAttribute<TEventBase> generic form ───────────────────────────

  [Test]
  public async Task SagaAttributeGeneric_StoresAllPropertiesAsync() {
    var attr = new SagaAttribute<SagaEventBase>("GenericSaga") { IncludeHooks = false, GenerateService = false };

    await Assert.That(attr.SagaName).IsEqualTo("GenericSaga");
    await Assert.That(attr.IncludeHooks).IsFalse();
    await Assert.That(attr.GenerateService).IsFalse();
  }

  [Test]
  public async Task SagaAttributeGeneric_DefaultsHooksAndServiceTrueAsync() {
    var attr = new SagaAttribute<SagaEventBase>("GenericSaga");

    await Assert.That(attr.IncludeHooks).IsTrue();
    await Assert.That(attr.GenerateService).IsTrue();
  }

  // ── SagaContext optional AccountId ───────────────────────────────────

  [Test]
  public async Task SagaContext_WithAccountId_RoundtripsAsync() {
    var account = Guid.Parse("33333333-3333-3333-3333-333333333333");
    var ctx = new SagaContext(_sagaId, _entityId, account);

    await Assert.That(ctx.AccountId).IsEqualTo(account);
  }

  [Test]
  public async Task SagaContext_NullAccountIdDefaultsToNullAsync() {
    var ctx = new SagaContext(_sagaId, _entityId);

    await Assert.That(ctx.AccountId).IsNull();
  }

  // ── BaseSagaService.ResetItemAsync ──────────────────────────────────

  [Test]
  public async Task BaseSagaService_ResetItemAsync_PublishesResetEventAsync() {
    var emitter = new RecordingEmitter();
    var svc = new MinimalSagaService(emitter);

    await svc.ResetItemAsync(new SagaContext(_sagaId, _entityId), "item-3", SagaItemState.Failed, CancellationToken.None);

    await Assert.That(emitter.Published.Count).IsEqualTo(1);
    var evt = (MinimalReset)emitter.Published[0];
    await Assert.That(evt.ItemIdentifier).IsEqualTo("item-3");
    await Assert.That(evt.PreviousStatus).IsEqualTo(SagaItemState.Failed);
  }

  [Test]
  public async Task BaseSagaService_ResetItemAsync_EmptyIdentifier_ThrowsAsync() {
    var svc = new MinimalSagaService(new RecordingEmitter());

    await Assert.That(() => svc.ResetItemAsync(new SagaContext(_sagaId, _entityId), string.Empty, SagaItemState.Failed, CancellationToken.None))
      .ThrowsExactly<ArgumentException>();
  }

  // ── BaseSagaService.TryRunHookAsync — success, terminal short-circuit, failure ─

  [Test]
  public async Task BaseSagaService_TryRunHookAsync_PublishesStartedThenCompletedAsync() {
    var emitter = new RecordingEmitter();
    var svc = new MinimalSagaService(emitter);
    var ran = false;

    var result = await svc.TryRunHookAsync(
      new SagaContext(_sagaId, _entityId),
      sagaProjection: null,
      hookName: "pre-archive",
      displayName: null,
      work: async (ct) => { ran = true; await Task.CompletedTask; },
      CancellationToken.None);

    await Assert.That(result).IsTrue();
    await Assert.That(ran).IsTrue();
    await Assert.That(emitter.Published.Count).IsEqualTo(2);
    await Assert.That(emitter.Published[0]).IsTypeOf<MinimalHookStarted>();
    await Assert.That(emitter.Published[1]).IsTypeOf<MinimalHookCompleted>();
    await Assert.That(((MinimalHookCompleted)emitter.Published[1]).Status).IsEqualTo(SagaItemState.Completed);
  }

  [Test]
  public async Task BaseSagaService_TryRunHookAsync_ProjectionHookTerminal_ReturnsFalseAsync() {
    var emitter = new RecordingEmitter();
    var svc = new MinimalSagaService(emitter);
    var saga = new Models.BaseSagaModel();
    saga.Hooks.Add(new Models.SagaHookExecution { HookName = "pre-archive", Status = SagaItemState.Completed });

    var result = await svc.TryRunHookAsync(
      new SagaContext(_sagaId, _entityId),
      sagaProjection: saga,
      hookName: "pre-archive",
      displayName: null,
      work: (_) => throw new InvalidOperationException("should not run"),
      CancellationToken.None);

    await Assert.That(result).IsFalse();
    await Assert.That(emitter.Published.Count).IsEqualTo(0);
  }

  [Test]
  public async Task BaseSagaService_TryRunHookAsync_WorkThrows_PublishesFailedThenRethrowsAsync() {
    var emitter = new RecordingEmitter();
    var svc = new MinimalSagaService(emitter);
    var boom = new InvalidOperationException("boom");

    await Assert.That(async () => await svc.TryRunHookAsync(
      new SagaContext(_sagaId, _entityId),
      sagaProjection: null,
      hookName: "pre-archive",
      displayName: null,
      work: (_) => throw boom,
      CancellationToken.None)).ThrowsExactly<InvalidOperationException>();

    await Assert.That(emitter.Published.Count).IsEqualTo(2)
      .Because("Started + Failed Completed must both publish before rethrow so the projection records the failure even when the receptor's transaction rolls back.");
    var completed = (MinimalHookCompleted)emitter.Published[1];
    await Assert.That(completed.Status).IsEqualTo(SagaItemState.Failed);
    await Assert.That(completed.ErrorMessage).IsEqualTo("boom");
  }

  // ── BaseSagaService null/empty validation paths ──────────────────────

  [Test]
  public async Task BaseSagaService_FailItemAsync_NullErrorMessage_ThrowsAsync() {
    var svc = new MinimalSagaService(new RecordingEmitter());
    await Assert.That(() => svc.FailItemAsync(new SagaContext(_sagaId, _entityId), "item-1", null!, null, null, CancellationToken.None))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task BaseSagaService_UpdateItemAsync_PendingStatus_ThrowsAsync() {
    var svc = new MinimalSagaService(new RecordingEmitter());
    await Assert.That(() => svc.UpdateItemAsync(new SagaContext(_sagaId, _entityId), "item-1", SagaItemState.Pending, null, CancellationToken.None))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task BaseSagaService_Constructor_NullEmitter_ThrowsAsync() {
    await Assert.That(() => new MinimalSagaService(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Test fakes ───────────────────────────────────────────────────────

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public List<IEvent> Published { get; } = new();
    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent {
      Published.Add(eventData!);
      return Task.CompletedTask;
    }
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      Published.Add(eventData!);
      return Task.FromResult(true);
    }
  }

  // ── Minimal saga event types (interface-only impls) + service ────────

  private sealed class MinimalInit : ISagaInitiatedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public IReadOnlyList<string> ItemIdentifiers { get; set; } = []; public int TotalItems { get; set; } public IReadOnlyList<string>? HookNames { get; set; } }
  private sealed class MinimalItemsDispatched : ISagaItemsDispatchedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public int TotalItems { get; set; } public int SuccessfullyDispatched { get; set; } public int FailedToDispatch { get; set; } }
  private sealed class MinimalItemStarted : ISagaItemStartedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public Guid SagaId { get; set; } public string ItemIdentifier { get; set; } = ""; public string? DisplayName { get; set; } }
  private sealed class MinimalItemCompleted : ISagaItemCompletedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public Guid SagaId { get; set; } public string ItemIdentifier { get; set; } = ""; public string? DisplayName { get; set; } }
  private sealed class MinimalItemFailed : ISagaItemFailedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public Guid SagaId { get; set; } public string ItemIdentifier { get; set; } = ""; public string? DisplayName { get; set; } public string ErrorMessage { get; set; } = ""; public string? ErrorDetails { get; set; } }
  private sealed class MinimalCompleted : ISagaCompletedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public SagaStatus FinalStatus { get; set; } public string? CompletedByItemIdentifier { get; set; } public int CompletedItems { get; set; } public int FailedItems { get; set; } public int TotalItems { get; set; } }
  private sealed class MinimalReset : ISagaResetEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public string ItemIdentifier { get; set; } = ""; public SagaItemState PreviousStatus { get; set; } }
  private sealed class MinimalHookStarted : ISagaHookStartedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public string HookName { get; set; } = ""; public string? DisplayName { get; set; } }
  private sealed class MinimalHookCompleted : ISagaHookCompletedEvent { public string SagaName { get; set; } = "M"; public Guid EntityId { get; set; } public string HookName { get; set; } = ""; public string? DisplayName { get; set; } public SagaItemState Status { get; set; } public string? ErrorMessage { get; set; } public string? ErrorDetails { get; set; } }

  private sealed class MinimalSagaService(ISagaEventEmitter emitter)
    : BaseSagaService<MinimalInit, MinimalItemsDispatched, MinimalItemStarted, MinimalItemCompleted,
                      MinimalItemFailed, MinimalCompleted, MinimalReset, MinimalHookStarted, MinimalHookCompleted>(
        "M", emitter, NullLogger<MinimalSagaService>.Instance) {

    protected override MinimalInit BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
    protected override MinimalItemsDispatched BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
    protected override MinimalItemStarted BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override MinimalItemCompleted BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override MinimalItemFailed BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
    protected override MinimalCompleted BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
    protected override MinimalReset BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
    protected override MinimalHookStarted BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };
    protected override MinimalHookCompleted BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt)
      => new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }
}
