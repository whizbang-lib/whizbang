using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Repositories;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// A saga whose events are generated but whose service is written by hand, so a test can drive the
/// real <see cref="BaseSagaService{TInit,TItemsDispatched,TItemStarted,TItemCompleted,TItemFailed,TCompleted,TReset,THookStarted,THookCompleted}"/>
/// through a real dispatcher and choose what its projection reads return.
/// </summary>
[Saga("StreamProbe", GenerateService = false)]
public partial class ProbeSaga;

/// <summary>The hand-written service over <see cref="ProbeSaga"/>'s generated events.</summary>
internal sealed class ProbeSagaService(
    ISagaEventEmitter emitter,
    IReadOnlyList<IncompleteSaga> incomplete,
    SagaOptions? options = null,
    ISagaItemRepository? itemRepository = null,
    ISagaItemTerminalReader? terminalReader = null)
  : BaseSagaService<ProbeSaga.InitiatedEvent, ProbeSaga.ItemsDispatchedEvent, ProbeSaga.ItemStartedEvent,
                    ProbeSaga.ItemCompletedEvent, ProbeSaga.ItemFailedEvent, ProbeSaga.CompletedEvent,
                    ProbeSaga.ResetEvent, ProbeSaga.HookStartedEvent, ProbeSaga.HookCompletedEvent>(
      ProbeSaga.SagaName, emitter, itemRepository, terminalReader, options, NullLogger<ProbeSagaService>.Instance) {

  protected override Task<IReadOnlyList<IncompleteSaga>> LoadIncompleteSagasAsync(CancellationToken cancellationToken)
    => Task.FromResult(incomplete);

  protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
    => Task.FromResult(incomplete.FirstOrDefault(i => i.Saga.Id == sagaId)?.Saga);

  protected override ProbeSaga.InitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
  protected override ProbeSaga.ItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
  protected override ProbeSaga.ItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
  protected override ProbeSaga.ItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
  protected override ProbeSaga.ItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  protected override ProbeSaga.CompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
  protected override ProbeSaga.ResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
  protected override ProbeSaga.HookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };
  protected override ProbeSaga.HookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
    new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
}
