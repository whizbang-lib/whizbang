using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// End-to-end lock-in for the v0.740 framework-managed saga completion + recovery loop.
/// Drives a saga through every step of the contract — initiate, dispatch some items,
/// strand others (simulating the production strand pattern), fire the watchdog, observe the
/// framework emit SagaCompletedEvent without any consumer-side orchestration.
///
/// <para>This is the test that proves the v0.740 design works as a whole. Each individual
/// piece has unit coverage in <see cref="CompletionOrchestrationGapTests"/>; this test
/// composes them into the exact production strand scenario (saga 019ef473 on 2026-06-23,
/// 350 items, 349 reached terminal, 1 stranded at Running) at unit-fixture scale.</para>
///
/// <para>Pre-v0.740, every consumer (a consumer BulkImport, EmployeeImport, Embedding,
/// JobMapping, ConsumerLibraryImport, OverlayFieldCopy, OverlayApplication, JobArchActivation,
/// OrderFieldPopulation, BulkJobStatusUpdate — 10 sagas) wired their own
/// SagaItemCompletedHandler + SagaItemFailedHandler + TryEmitAsync orchestration. The
/// framework now owns it all.</para>
/// </summary>
[Category("Integration")]
[Category("Saga")]
public class StrandedSagaRecoveryIntegrationTests {

  private const string SAGA_NAME = "BulkImportSimulator";
  private static readonly Guid _sagaId = Guid.Parse("a-production-saga");  // production forensic id
  private static readonly Guid _entityId = Guid.Parse("c0ffee00-cafe-f00d-face-feed12345678");

  // ────────────────────────────────────────────────────────────────────
  //   E2E-1: happy path. Every item completes through the framework
  //   fast path — SagaCompletedEvent fires automatically without any
  //   consumer-supplied receptor or projection loader.
  // ────────────────────────────────────────────────────────────────────

  [Test]
  public async Task HappyPath_AllItemsCompleted_FrameworkAutoCompletesSagaAsync() {
    var emitter = new RecordingEmitter();
    var svc = new SimulatorSagaService(emitter);
    var ctx = new SagaContext(_sagaId, _entityId);

    await svc.InitiateSagaAsync(ctx, ["item-a", "item-b", "item-c"], hookNames: null, CancellationToken.None);

    // Drive every per-item terminal through the framework's UpdateItemAsync — the in-memory
    // fast-path tracker advances and auto-emits SagaCompletedEvent when the count hits total.
    await svc.UpdateItemAsync(ctx, "item-a", SagaItemState.Completed, displayName: null, CancellationToken.None);
    await svc.UpdateItemAsync(ctx, "item-b", SagaItemState.Completed, displayName: null, CancellationToken.None);
    await svc.UpdateItemAsync(ctx, "item-c", SagaItemState.Completed, displayName: null, CancellationToken.None);

    var sagaCompleted = emitter.PublishedOnce
      .Select(x => x.evt).OfType<SimCompletedEvent>().SingleOrDefault();
    await Assert.That(sagaCompleted).IsNotNull()
      .Because("Framework owns completion: when every item dispatched at InitiateSagaAsync reaches a terminal state, " +
               "SagaCompletedEvent emits via PublishOnceAsync. No consumer receptor required.");
    await Assert.That(sagaCompleted!.FinalStatus).IsEqualTo(SagaStatus.Completed);
    await Assert.That(sagaCompleted.CompletedItems).IsEqualTo(3);
    await Assert.That(sagaCompleted.FailedItems).IsEqualTo(0);
    await Assert.That(sagaCompleted.TotalItems).IsEqualTo(3);
  }

  // ────────────────────────────────────────────────────────────────────
  //   E2E-2: strand recovery. Mirrors production saga 019ef473 — 349 of 350
  //   items complete; 1 (item 171) is stranded by a cross-pod stale-read
  //   race that left the projection at Running while the durable event
  //   store says Completed. Watchdog fires; framework reads the
  //   consumer's projection (which already shows TotalItems=350 because
  //   the projection's ApplyMethod aggregates from item-completion events
  //   that DID land, just on a different pod's tracker); emits
  //   SagaCompletedEvent.
  // ────────────────────────────────────────────────────────────────────

  [Test]
  public async Task SlotThreeStrand_WatchdogReadsProjection_EmitsCompletedEventAsync() {
    var emitter = new RecordingEmitter();
    // Wire the framework's projection-loader hook through the test fixture: the projection
    // (post-strand-cleanup) reports TotalItems=350, CompletedItems=350. This is what a
    // production consumer's saga repository would return after the per-item terminal events
    // have rolled up — even if the in-memory tracker on THIS instance is stale because a
    // per-item terminal landed on a different pod.
    var svc = new SimulatorSagaService(emitter) {
      FakeProjection = new BaseSagaModel {
        Id = _sagaId,
        SagaName = SAGA_NAME,
        EntityId = _entityId,
        Status = SagaStatus.Running,
        TotalItems = 350,
        CompletedItems = 350,
        FailedItems = 0,
        CompletionEventDispatched = false
      }
    };
    var ctx = new SagaContext(_sagaId, _entityId);

    // Initiate the saga and drive 349 of 350 items to terminal in the in-memory tracker —
    // simulates the "all but one item event landed on this pod" scenario.
    var allItems = Enumerable.Range(1, 350).Select(i => $"item-{i}").ToList();
    await svc.InitiateSagaAsync(ctx, allItems, hookNames: null, CancellationToken.None);
    foreach (var id in allItems.Where(id => id != "item-171")) {
      await svc.UpdateItemAsync(ctx, id, SagaItemState.Completed, displayName: null, CancellationToken.None);
    }

    // Fast-path check before watchdog: in-memory says 349/350 → no SagaCompletedEvent yet.
    await Assert.That(emitter.PublishedOnce.Count)
      .IsEqualTo(0)
      .Because("Pre-watchdog state: in-memory tracker says 349 < 350. Fast-path doesn't fire. Recovery requires the " +
               "watchdog tick — proving the production strand requires the framework's slow path, not just the in-memory " +
               "fast path.");

    // Fire the watchdog. The framework's slow path reads the projection (which the consumer's
    // repository populated from durable per-item terminal events) and sees the saga is actually
    // terminal. SagaCompletedEvent emits.
    var recovered = await svc.TryRecoverViaWatchdogAsync(ctx, CancellationToken.None);

    await Assert.That(recovered)
      .IsTrue()
      .Because("production strand pattern (saga 019ef473): in-memory tracker says 349/350 but durable projection says " +
               "350/350. The watchdog must drive completion via the slow path so a strand can't leave a saga stuck " +
               "forever — which was the whole point of bringing watchdog recovery into the framework.");

    var sagaCompleted = emitter.PublishedOnce
      .Select(x => x.evt).OfType<SimCompletedEvent>().SingleOrDefault();
    await Assert.That(sagaCompleted).IsNotNull()
      .Because("Recovery's job is to actually emit SagaCompletedEvent, not just report success — the saga's downstream " +
               "subscribers (UI refresh, billing, audit) depend on the event firing exactly once.");
    await Assert.That(sagaCompleted!.CompletedItems).IsEqualTo(350)
      .Because("Aggregate counts come from the projection (the source of truth), not the in-memory tracker — that's " +
               "why the consumer-supplied loader hook exists.");
    await Assert.That(sagaCompleted.TotalItems).IsEqualTo(350);
  }

  // ────────────────────────────────────────────────────────────────────
  //   E2E-3: exactly-once across racing watchdog + fast-path. When a
  //   per-item terminal event finally lands on this pod AFTER the
  //   watchdog already drove completion, the in-memory tracker's
  //   auto-complete must not double-emit.
  // ────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ExactlyOnce_WatchdogFirstThenLateItemArrival_NoDoubleEmissionAsync() {
    var emitter = new RecordingEmitter();
    var svc = new SimulatorSagaService(emitter) {
      FakeProjection = new BaseSagaModel {
        Id = _sagaId,
        SagaName = SAGA_NAME,
        EntityId = _entityId,
        Status = SagaStatus.Running,
        TotalItems = 3,
        CompletedItems = 3,
        FailedItems = 0,
        CompletionEventDispatched = false
      }
    };
    var ctx = new SagaContext(_sagaId, _entityId);

    await svc.InitiateSagaAsync(ctx, ["a", "b", "c"], hookNames: null, CancellationToken.None);
    await svc.UpdateItemAsync(ctx, "a", SagaItemState.Completed, displayName: null, CancellationToken.None);
    await svc.UpdateItemAsync(ctx, "b", SagaItemState.Completed, displayName: null, CancellationToken.None);
    // "c" hasn't arrived yet in the in-memory tracker — Completed count is 2/3.

    // Watchdog drives completion from the projection.
    await svc.TryRecoverViaWatchdogAsync(ctx, CancellationToken.None);
    var firstEmissionCount = emitter.PublishedOnce.Count;
    await Assert.That(firstEmissionCount).IsEqualTo(1);

    // "c" finally arrives on this pod after watchdog already won. Fast path tries to auto-emit.
    await svc.UpdateItemAsync(ctx, "c", SagaItemState.Completed, displayName: null, CancellationToken.None);

    // No additional PublishOnce — the framework's DispatchedCompletion flag in the in-memory
    // tracker remembers "we already won the claim" and short-circuits the second attempt. Even
    // if it didn't, IDispatcher.PublishOnceAsync's claim key dedups at the dispatcher layer.
    await Assert.That(emitter.PublishedOnce.Count)
      .IsEqualTo(firstEmissionCount)
      .Because("Exactly-once across racing wake-ups: the fast-path tracker remembers DispatchedCompletion=true after a " +
               "successful PublishOnceAsync win, so a late-arriving per-item terminal can't drive a duplicate emission. " +
               "Defense in depth — the dispatcher's claim key also dedups at its own layer.");
  }

  // ────────────────────────────────────────────────────────────────────
  //   Test fixture
  // ────────────────────────────────────────────────────────────────────

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

  // Minimal saga event types implementing the shipped Whizbang.Sagas.Contracts interfaces.
  private sealed class SimInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public int TotalItems { get; set; }
    public IReadOnlyList<string>? HookNames { get; set; }
  }
  private sealed class SimItemsDispatchedEvent : ISagaItemsDispatchedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public int TotalItems { get; set; }
    public int SuccessfullyDispatched { get; set; }
    public int FailedToDispatch { get; set; }
  }
  private sealed class SimItemStartedEvent : ISagaItemStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class SimItemCompletedEvent : ISagaItemCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class SimItemFailedEvent : ISagaItemFailedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetails { get; set; }
  }
  private sealed class SimCompletedEvent : ISagaCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public SagaStatus FinalStatus { get; set; }
    public string? CompletedByItemIdentifier { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class SimResetEvent : ISagaResetEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public SagaItemState PreviousStatus { get; set; }
  }
  private sealed class SimHookStartedEvent : ISagaHookStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class SimHookCompletedEvent : ISagaHookCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
    public SagaItemState Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
  }

  private sealed class SimulatorSagaService(ISagaEventEmitter emitter)
    : BaseSagaService<SimInitiatedEvent, SimItemsDispatchedEvent, SimItemStartedEvent, SimItemCompletedEvent,
                      SimItemFailedEvent, SimCompletedEvent, SimResetEvent, SimHookStartedEvent, SimHookCompletedEvent>(
        SAGA_NAME, emitter, NullLogger<SimulatorSagaService>.Instance) {

    /// <summary>Test-only projection loader hook to drive the slow-path recovery.</summary>
    internal BaseSagaModel? FakeProjection { get; set; }

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken) =>
      Task.FromResult(FakeProjection);

    protected override SimInitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
    protected override SimItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
    protected override SimItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override SimItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override SimItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
    protected override SimCompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
    protected override SimResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
    protected override SimHookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };
    protected override SimHookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }
}
