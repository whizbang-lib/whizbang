using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Coverage for two <see cref="BaseSagaService{T1,T2,T3,T4,T5,T6,T7,T8,T9}"/> branches the sibling
/// suites (<see cref="BaseSagaServiceTests"/>, <see cref="Services.TryRecoverViaWatchdogAsyncTests"/>,
/// <see cref="Backfill.SagaBackfillTests"/>) never drive: the in-memory tracker's failed-item
/// increment, and the slow-recovery-path guard against a projection that has no items yet.
/// </summary>
/// <code-under-test>src/Whizbang.Sagas/Services/BaseSagaService.cs</code-under-test>
public class BaseSagaServiceCoverageTests {

  private const string SAGA_NAME = "CoverageSaga";
  private static readonly Guid _sagaId = Guid.Parse("33333333-3333-3333-3333-333333333333");
  private static readonly Guid _entityId = Guid.Parse("44444444-4444-4444-4444-444444444444");

  // The in-memory completion tracker advances on BOTH completed and failed terminals — that is
  // what lets a saga where every item ends in failure still auto-complete instead of hanging
  // forever waiting for a "completed" count that will never arrive. Every existing suite only
  // ever drives the tracker through UpdateItemAsync(Completed); none call FailItemAsync after
  // InitiateSagaAsync for the same saga, so the tracker.Failed++ increment inside
  // _tryAutoCompleteAsync has never actually run. If it regressed to counting only completions,
  // an all-failure saga would never dispatch SagaCompletedEvent on the fast path and would sit
  // stranded until a watchdog tick eventually rescued it minutes later.
  [Test]
  public async Task FailItemAsync_AfterInitiate_TrackerCountsFailuresTowardCompletionAsync() {
    var emitter = new _recordingEmitter();
    var svc = new _coverageSagaService(emitter);
    var ctx = new SagaContext(_sagaId, _entityId);

    await svc.InitiateSagaAsync(ctx, itemIdentifiers: ["a", "b"], hookNames: null, CancellationToken.None);
    await svc.FailItemAsync(ctx, "a", errorMessage: "boom-a", errorDetails: null, displayName: null, CancellationToken.None);

    await Assert.That(emitter.PublishedOnce).IsEmpty()
      .Because("only 1 of 2 items has reached a terminal state — completing here would close a saga whose second item is still outstanding");

    await svc.FailItemAsync(ctx, "b", errorMessage: "boom-b", errorDetails: null, displayName: null, CancellationToken.None);

    await Assert.That(emitter.PublishedOnce.Count).IsEqualTo(1)
      .Because("both items are now terminal (failed); the tracker's failed count must reach Total exactly like completions would");
    var completed = (_testCompletedEvent)emitter.PublishedOnce[0].evt;
    await Assert.That(completed.FinalStatus).IsEqualTo(SagaStatus.CompletedWithFailures);
    await Assert.That(completed.CompletedItems).IsEqualTo(0);
    await Assert.That(completed.FailedItems).IsEqualTo(2);
    await Assert.That(completed.TotalItems).IsEqualTo(2);
  }

  // The slow (projection-backed) recovery path guards on saga.TotalItems <= 0 before ever
  // consulting completed/failed counts. This covers a downstream saga that has a row (so
  // LoadProjectionAsync returns non-null) but hasn't yet had UpdateTotalItems applied — the
  // real count arrives later from an upstream saga's completion. Without this guard, a
  // zero-total projection would read as vacuously "done" (0 + 0 >= 0) and the watchdog would
  // fire a completion event for a saga that has not actually started its work.
  [Test]
  public async Task TryRecoverViaWatchdogAsync_ProjectionWithNoTotalItemsYet_DoesNotCompleteAsync() {
    var projection = new BaseSagaModel {
      Id = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      TotalItems = 0,
      CompletedItems = 0,
      FailedItems = 0,
      CompletionEventDispatched = false,
    };
    var emitter = new _recordingEmitter();
    var svc = new _coverageSagaService(emitter, projection);

    // No InitiateSagaAsync call precedes this — the in-memory tracker has nothing registered
    // for _sagaId, so TryRecoverViaWatchdogAsync falls straight through to the slow path that
    // consults LoadProjectionAsync.
    var recovered = await svc.TryRecoverViaWatchdogAsync(new SagaContext(_sagaId, _entityId), CancellationToken.None);

    await Assert.That(recovered).IsFalse()
      .Because("a downstream saga awaiting its real item count from an upstream completion must not be treated as vacuously done");
    await Assert.That(emitter.PublishedOnce).IsEmpty();
  }

  // ── Test doubles ───────────────────────────────────────────────────────

  private sealed class _recordingEmitter : ISagaEventEmitter {
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

  private sealed class _testInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public int TotalItems { get; set; }
    public IReadOnlyList<string>? HookNames { get; set; }
  }
  private sealed class _testItemsDispatchedEvent : ISagaItemsDispatchedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public int TotalItems { get; set; }
    public int SuccessfullyDispatched { get; set; }
    public int FailedToDispatch { get; set; }
  }
  private sealed class _testItemStartedEvent : ISagaItemStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class _testItemCompletedEvent : ISagaItemCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class _testItemFailedEvent : ISagaItemFailedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetails { get; set; }
  }
  private sealed class _testCompletedEvent : ISagaCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public SagaStatus FinalStatus { get; set; }
    public string? CompletedByItemIdentifier { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class _testResetEvent : ISagaResetEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public SagaItemState PreviousStatus { get; set; }
  }
  private sealed class _testHookStartedEvent : ISagaHookStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class _testHookCompletedEvent : ISagaHookCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
    public SagaItemState Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
  }

  private sealed class _coverageSagaService(ISagaEventEmitter emitter, BaseSagaModel? projection = null)
    : BaseSagaService<_testInitiatedEvent, _testItemsDispatchedEvent, _testItemStartedEvent, _testItemCompletedEvent,
                      _testItemFailedEvent, _testCompletedEvent, _testResetEvent, _testHookStartedEvent, _testHookCompletedEvent>(
        SAGA_NAME, emitter, NullLogger<_coverageSagaService>.Instance) {

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(projection);

    protected override _testInitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };

    protected override _testItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };

    protected override _testItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };

    protected override _testItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };

    protected override _testItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };

    protected override _testCompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };

    protected override _testResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };

    protected override _testHookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };

    protected override _testHookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }
}
