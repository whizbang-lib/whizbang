using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Coverage for <see cref="BaseSagaService{T1,T2,T3,T4,T5,T6,T7,T8,T9}"/> behavior the sibling
/// suites (<see cref="BaseSagaServiceTests"/>, <see cref="Services.TryRecoverViaWatchdogAsyncTests"/>,
/// <see cref="Backfill.SagaBackfillTests"/>) never drive: the in-memory tracker's failed-item
/// increment, the slow-recovery-path guard against a projection that has no items yet, the
/// protected saga-name accessor consumer subclasses read, and the framework-default
/// <c>LoadProjectionAsync</c> that a service with no projection loader wired falls back to.
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

  // The protected SagaName accessor is the surface a consumer subclass reads when it builds its
  // own saga-scoped payloads (audit rows, correlation tags, a hand-rolled completion claim). It
  // has to hand back the exact string the framework itself uses, because the framework stamps
  // that string on the watchdog tick it arms and folds it into the completion claim key. If the
  // accessor ever drifted from the constructor argument, consumer-emitted events would carry a
  // saga name that routes nowhere and a consumer-built claim key would stop deduplicating
  // against the framework's own — a silent duplicate-completion hole.
  [Test]
  public async Task SagaName_MatchesTheNameTheFrameworkStampsAndClaimsWithAsync() {
    var emitter = new _recordingEmitter();
    var svc = new _coverageSagaService(emitter);
    var ctx = new SagaContext(_sagaId, _entityId);

    await svc.InitiateSagaAsync(ctx, itemIdentifiers: ["a"], hookNames: null, CancellationToken.None);
    await svc.CompleteSagaAsync(
      ctx, SagaStatus.Completed, completedByItemIdentifier: "a",
      completedItems: 1, failedItems: 0, totalItems: 1, CancellationToken.None);

    await Assert.That(svc.ExposedSagaName).IsEqualTo(SAGA_NAME)
      .Because("the accessor exposes the name the service was constructed with, unmodified");

    var watchdog = emitter.Published.OfType<SagaCompletionWatchdogTickEvent>().Single();
    await Assert.That(watchdog.SagaName).IsEqualTo(svc.ExposedSagaName)
      .Because("the watchdog tick the framework arms is routed by saga name — a subclass reading a different name would arm ticks the receptor never matches");

    await Assert.That(emitter.PublishedOnce.Single().claimKey)
      .IsEqualTo(SagaCompletionGuard.ClaimKey(svc.ExposedSagaName, _sagaId))
      .Because("the completion claim key is derived from the same name, so a subclass that claims with the accessor's value collapses onto the framework's claim instead of publishing a second completion");
  }

  // A saga service built without overriding LoadProjectionAsync (the backwards-compatible
  // constructor, or a fixture that only needs the in-memory fast path) has no durable state for
  // the watchdog to read. The framework default returns no projection, and the slow path must
  // treat that as "cannot judge" rather than "nothing outstanding": completing here would emit
  // SagaCompletedEvent for a saga whose real progress nobody has looked at.
  [Test]
  public async Task TryRecoverViaWatchdogAsync_WithNoProjectionLoaderWired_DeclinesRecoveryAsync() {
    var emitter = new _recordingEmitter();
    var svc = new _defaultLoaderSagaService(emitter);

    // No InitiateSagaAsync precedes this, so the in-memory tracker holds nothing for _sagaId and
    // recovery drops straight through the fast path into the projection-backed slow path.
    var recovered = await svc.TryRecoverViaWatchdogAsync(new SagaContext(_sagaId, _entityId), CancellationToken.None);

    await Assert.That(recovered).IsFalse()
      .Because("no projection means no authoritative completion state — the watchdog has nothing to recover from and must say so");
    await Assert.That(emitter.PublishedOnce).IsEmpty()
      .Because("declining has to be silent: no completion claim may be taken on the strength of an absent projection");
    await Assert.That(emitter.Published).IsEmpty()
      .Because("a declined recovery emits nothing at all — not even a lifecycle event");
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

  // Keeps the framework's own LoadProjectionAsync — the "consumer wired no projection loader"
  // shape. _coverageSagaService below is the same service with a loader supplied.
  private class _defaultLoaderSagaService(ISagaEventEmitter emitter)
    : BaseSagaService<_testInitiatedEvent, _testItemsDispatchedEvent, _testItemStartedEvent, _testItemCompletedEvent,
                      _testItemFailedEvent, _testCompletedEvent, _testResetEvent, _testHookStartedEvent, _testHookCompletedEvent>(
        SAGA_NAME, emitter, NullLogger<_defaultLoaderSagaService>.Instance) {

    /// <summary>Surfaces the protected <c>SagaName</c> accessor a consumer subclass would read.</summary>
    public string ExposedSagaName => SagaName;

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

  private sealed class _coverageSagaService(ISagaEventEmitter emitter, BaseSagaModel? projection = null)
    : _defaultLoaderSagaService(emitter) {

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken)
      => Task.FromResult(projection);
  }
}
