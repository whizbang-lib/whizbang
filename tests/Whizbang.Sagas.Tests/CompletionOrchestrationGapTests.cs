using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the gap-claims that drive the v0.740 framework-owns-completion work:
/// today Whizbang.Sagas can EMIT saga lifecycle events but it cannot DRIVE the
/// saga to terminal state on its own. Consumers each hand-roll receptors that watch
/// SagaItem terminal events, run a reconciler, and call
/// <see cref="BaseSagaService{TInit,TItemsDispatched,TItemStarted,TItemCompleted,TItemFailed,TCompleted,TReset,THookStarted,THookCompleted}.CompleteSagaAsync"/>.
/// When a per-item projection is stranded by the cross-pod stale-read race
/// (a stranded-saga incident observed in production), the saga sits forever — there
/// is no framework wake-up. Every consumer also has to write its own watchdog.
///
/// <para>These REDs assert the missing surface (auto-orchestration, watchdog
/// contract, watchdog auto-armament). They go GREEN once Whizbang.Sagas owns
/// the orchestration end-to-end.</para>
/// </summary>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
[Category("Unit")]
[Category("Saga")]
public class CompletionOrchestrationGapTests {

  private const string SAGA_NAME = "TestSaga";
  private static readonly Guid _sagaId = Guid.Parse("33333333-3333-3333-3333-333333333333");
  private static readonly Guid _entityId = Guid.Parse("44444444-4444-4444-4444-444444444444");

  // ── RED-7: watchdog tick contract type doesn't exist yet ─────────────

  /// <summary>
  /// Smallest possible RED — a reflection check that a contract type exists in
  /// the Whizbang.Sagas assembly. The watchdog only works once Whizbang.Sagas
  /// publishes the tick contract that consumers can opt in to receive.
  /// </summary>
  [Test]
  public async Task SagaCompletionWatchdogTickEvent_ContractTypeExistsAsync() {
    // Probe both Whizbang.Sagas (where BaseSagaModel lives) and Whizbang.Sagas.Contracts
    // (where ISagaInitiatedEvent / SagaItem lifecycle contracts live) — the watchdog
    // could naturally land in either, so the test accepts either home.
    var sagasAsm = typeof(Models.BaseSagaModel).Assembly;
    var contractsAsm = typeof(ISagaInitiatedEvent).Assembly;
    Type? contractType =
      sagasAsm.GetType("Whizbang.Sagas.ISagaCompletionWatchdogTickEvent")
      ?? sagasAsm.GetType("Whizbang.Sagas.Contracts.ISagaCompletionWatchdogTickEvent")
      ?? contractsAsm.GetType("Whizbang.Sagas.ISagaCompletionWatchdogTickEvent")
      ?? contractsAsm.GetType("Whizbang.Sagas.Contracts.ISagaCompletionWatchdogTickEvent");

    await Assert.That(contractType)
      .IsNotNull()
      .Because("Framework-managed completion recovery needs a wake-up event type that consumers can route receptors to. " +
               "Until the contract ships, every consumer hand-rolls its own watchdog event.");
  }

  // ── RED-6: InitiateSagaAsync doesn't auto-arm a watchdog ─────────────

  /// <summary>
  /// Today <see cref="BaseSagaService{TInit,TItemsDispatched,TItemStarted,TItemCompleted,TItemFailed,TCompleted,TReset,THookStarted,THookCompleted}.InitiateSagaAsync"/>
  /// publishes exactly one event (the Initiated event). After the v0.740 work,
  /// it should publish TWO — the Initiated event AND a watchdog-tick event with
  /// <c>ScheduledFor</c> set to expected completion + slack — so the framework
  /// has a guaranteed wake-up to verify completion even if every per-item
  /// terminal event is silently dropped or strands the projection.
  /// </summary>
  [Test]
  public async Task InitiateSagaAsync_AutoArmsWatchdogTickAsync() {
    var emitter = new GapRecordingEmitter();
    var svc = new GapTestSagaService(emitter);

    await svc.InitiateSagaAsync(
      new SagaContext(_sagaId, _entityId),
      itemIdentifiers: ["item-1", "item-2", "item-3"],
      hookNames: null,
      CancellationToken.None);

    await Assert.That(emitter.Published.Count)
      .IsEqualTo(2)
      .Because("InitiateSagaAsync must emit (a) the Initiated event and (b) a SagaCompletionWatchdogTickEvent " +
               "scheduled for expected-completion + slack, so the framework can recover from per-item terminal-event loss.");

    var watchdog = emitter.Published
      .Where(e => e.GetType().Name.Contains("Watchdog", StringComparison.Ordinal))
      .ToList();
    await Assert.That(watchdog.Count)
      .IsEqualTo(1)
      .Because("Exactly one watchdog tick event should be armed from InitiateSagaAsync, regardless of item count.");
  }

  // ── RED-1: framework doesn't drive completion without consumer receptors ─

  /// <summary>
  /// Today, calling <see cref="BaseSagaService{TInit,TItemsDispatched,TItemStarted,TItemCompleted,TItemFailed,TCompleted,TReset,THookStarted,THookCompleted}.UpdateItemAsync"/>
  /// with <see cref="SagaItemState.Completed"/> for every item in the saga
  /// publishes per-item events but the framework never observes "all items
  /// terminal" and never calls <see cref="BaseSagaService{TInit,TItemsDispatched,TItemStarted,TItemCompleted,TItemFailed,TCompleted,TReset,THookStarted,THookCompleted}.CompleteSagaAsync"/>.
  /// Every consumer has to wire its own receptors to detect terminal aggregate
  /// and emit completion. After v0.740: the framework auto-detects via the
  /// shipped orchestrator and emits the SagaCompletedEvent automatically.
  /// </summary>
  [Test]
  public async Task AllItemsCompleted_FrameworkAutoEmitsSagaCompletedEventAsync() {
    var emitter = new GapRecordingEmitter();
    var svc = new GapTestSagaService(emitter);
    var ctx = new SagaContext(_sagaId, _entityId);

    await svc.InitiateSagaAsync(ctx, ["a", "b", "c"], hookNames: null, CancellationToken.None);
    emitter.Published.Clear();
    emitter.PublishedOnce.Clear();

    // Emit terminal events for every item in the saga.
    await svc.UpdateItemAsync(ctx, "a", SagaItemState.Completed, displayName: null, CancellationToken.None);
    await svc.UpdateItemAsync(ctx, "b", SagaItemState.Completed, displayName: null, CancellationToken.None);
    await svc.UpdateItemAsync(ctx, "c", SagaItemState.Completed, displayName: null, CancellationToken.None);

    // The framework should detect "all 3 items terminal" and emit the SagaCompletedEvent
    // via PublishOnceAsync (claim-key dedup) without the consumer wiring a receptor.
    await Assert.That(emitter.PublishedOnce.Count)
      .IsEqualTo(1)
      .Because("Framework owns completion: when every item the saga was initiated with has reached a terminal " +
               "state, the SagaCompletedEvent emission must happen automatically via the orchestrator. " +
               "Today, every consumer wires this by hand — that's the duplicated 200+ lines we're removing.");
  }

  // ── RED-1b: stranded item — framework recovers via watchdog ──────────

  /// <summary>
  /// Stronger variant: even when one of the per-item completion events is lost
  /// (the strand-equivalent at the contract level), the framework's watchdog
  /// must eventually drive the saga to terminal by reconciling against its
  /// own bookkeeping. Today: stuck forever. After v0.740: watchdog tick
  /// detects mismatch between expected (all items completed) and observed,
  /// runs the reconciler, emits the SagaCompletedEvent.
  /// </summary>
  [Test]
  public async Task OneItemTerminalEventDropped_WatchdogTickRecoversSagaAsync() {
    var emitter = new GapRecordingEmitter();
    var svc = new GapTestSagaService(emitter);
    var ctx = new SagaContext(_sagaId, _entityId);

    await svc.InitiateSagaAsync(ctx, ["a", "b", "c"], hookNames: null, CancellationToken.None);
    emitter.Published.Clear();
    emitter.PublishedOnce.Clear();

    // Two of three terminal events fire; "b" is silently dropped (the strand-equivalent).
    await svc.UpdateItemAsync(ctx, "a", SagaItemState.Completed, displayName: null, CancellationToken.None);
    await svc.UpdateItemAsync(ctx, "c", SagaItemState.Completed, displayName: null, CancellationToken.None);

    // Wire a projection loader that returns the saga in its actual, durable, terminal state —
    // the authoritative truth that the in-memory tracker can't reach because "b" was dropped.
    // In production this is the consumer's saga repository / lens query; the test fakes it so
    // the recovery code path runs end-to-end without standing up a real projection store.
    svc.FakeProjection = new BaseSagaModel {
      Id = _sagaId,
      SagaName = SAGA_NAME,
      EntityId = _entityId,
      Status = SagaStatus.Running,
      TotalItems = 3,
      CompletedItems = 3,
      FailedItems = 0,
      CompletionEventDispatched = false
    };

    // Fire the watchdog. The framework's recovery path checks in-memory (sees 2/3, not terminal),
    // falls through to the projection (sees 3/3, terminal), and emits the SagaCompletedEvent.
    var recoveryDriven = await RecoveryHelpers.TryFireWatchdogAsync(svc, ctx);
    await Assert.That(recoveryDriven)
      .IsTrue()
      .Because("The framework must expose a watchdog-driven recovery path that runs reconciliation and " +
               "emits SagaCompletedEvent. Until that exists, stranded sagas have no internal way to unstuck.");

    await Assert.That(emitter.PublishedOnce.Count)
      .IsEqualTo(1)
      .Because("Watchdog recovery must emit SagaCompletedEvent exactly once even when a per-item terminal event was lost.");
  }

  // ── Helpers + fakes ──────────────────────────────────────────────────

  /// <summary>
  /// Stub that today returns false (no surface). After v0.740 GREEN, the framework
  /// exposes a real watchdog-recovery method that this stub will delegate to. The
  /// stub keeps the test code source-stable across the RED→GREEN transition.
  /// </summary>
  private static class RecoveryHelpers {
    public static Task<bool> TryFireWatchdogAsync<TSvc>(TSvc service, SagaContext ctx) {
      var recoverMethod = typeof(TSvc).GetMethod(
        "TryRecoverViaWatchdogAsync",
        BindingFlags.Public | BindingFlags.Instance,
        binder: null,
        types: [typeof(SagaContext), typeof(CancellationToken)],
        modifiers: null);
      if (recoverMethod is null) {
        return Task.FromResult(false);
      }
      var result = recoverMethod.Invoke(service, [ctx, CancellationToken.None]);
      if (result is Task<bool> t) {
        return t;
      }
      return Task.FromResult(false);
    }
  }

  private sealed class GapRecordingEmitter : ISagaEventEmitter {
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

  private sealed class GapInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public int TotalItems { get; set; }
    public IReadOnlyList<string>? HookNames { get; set; }
  }
  private sealed class GapItemsDispatchedEvent : ISagaItemsDispatchedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public int TotalItems { get; set; }
    public int SuccessfullyDispatched { get; set; }
    public int FailedToDispatch { get; set; }
  }
  private sealed class GapItemStartedEvent : ISagaItemStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class GapItemCompletedEvent : ISagaItemCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class GapItemFailedEvent : ISagaItemFailedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetails { get; set; }
  }
  private sealed class GapCompletedEvent : ISagaCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public SagaStatus FinalStatus { get; set; }
    public string? CompletedByItemIdentifier { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class GapResetEvent : ISagaResetEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public SagaItemState PreviousStatus { get; set; }
  }
  private sealed class GapHookStartedEvent : ISagaHookStartedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class GapHookCompletedEvent : ISagaHookCompletedEvent {
    public string SagaName { get; set; } = SAGA_NAME;
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
    public SagaItemState Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
  }

  private sealed class GapTestSagaService(ISagaEventEmitter emitter)
    : BaseSagaService<GapInitiatedEvent, GapItemsDispatchedEvent, GapItemStartedEvent, GapItemCompletedEvent,
                      GapItemFailedEvent, GapCompletedEvent, GapResetEvent, GapHookStartedEvent, GapHookCompletedEvent>(
        SAGA_NAME, emitter, NullLogger<GapTestSagaService>.Instance) {

    /// <summary>Fake projection the test sets to drive the watchdog recovery slow path.</summary>
    internal BaseSagaModel? FakeProjection { get; set; }

    protected override Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken) =>
      Task.FromResult(FakeProjection);

    protected override GapInitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
    protected override GapItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
    protected override GapItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override GapItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override GapItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
    protected override GapCompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
    protected override GapResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
    protected override GapHookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };
    protected override GapHookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }

}
