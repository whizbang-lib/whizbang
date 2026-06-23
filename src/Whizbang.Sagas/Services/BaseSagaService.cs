using Microsoft.Extensions.Logging;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;

namespace Whizbang.Sagas.Services;

/// <summary>
/// Abstract base for per-saga lifecycle services. Consumers (or the
/// <c>[Saga&lt;TBase&gt;("Name")]</c> source generator) provide concrete
/// event types via the nine generic type parameters; concrete
/// subclasses fill in the <c>Build*Event</c> factory methods that
/// construct those types.
/// </summary>
/// <remarks>
/// <para>
/// Publishing each event as a derived runtime type makes Whizbang's
/// message registry route the event only to the projections that
/// subscribed to that specific type — the saga-name dispatch fan-out
/// is type-narrowed instead of relying on every projection ignoring
/// events with the wrong <see cref="ISagaEvent.SagaName"/>.
/// </para>
/// <para>
/// The factory pattern (<c>BuildInitiatedEvent</c>, etc.) avoids the
/// <c>new TInit()</c> + property-set sequence the
/// <c>new()</c> generic constraint would otherwise force; required
/// members and complex constructors all work cleanly.
/// </para>
/// <para>
/// Terminal completion uses
/// <see cref="ISagaEventEmitter.PublishOnceAsync"/> with the
/// <see cref="SagaCompletionGuard"/> claim-key convention so N concurrent
/// terminal handlers collapse to exactly one emission. All other
/// lifecycle events use <see cref="ISagaEventEmitter.PublishAsync"/>.
/// </para>
/// </remarks>
public abstract partial class BaseSagaService<TInit, TItemsDispatched, TItemStarted, TItemCompleted, TItemFailed, TCompleted, TReset, THookStarted, THookCompleted>
  where TInit : class, ISagaInitiatedEvent
  where TItemsDispatched : class, ISagaItemsDispatchedEvent
  where TItemStarted : class, ISagaItemStartedEvent
  where TItemCompleted : class, ISagaItemCompletedEvent
  where TItemFailed : class, ISagaItemFailedEvent
  where TCompleted : class, ISagaCompletedEvent
  where TReset : class, ISagaResetEvent
  where THookStarted : class, ISagaHookStartedEvent
  where THookCompleted : class, ISagaHookCompletedEvent {

  private readonly string _sagaName;
  private readonly ISagaEventEmitter _emitter;
  private readonly ILogger _logger;

  // ── Framework-owned completion tracking (in-memory fast path) ────────
  //
  // When InitiateSagaAsync is called the framework records the saga's expected total.
  // As UpdateItemAsync(Completed) / FailItemAsync fire, the counters advance. When the
  // sum reaches the total the framework auto-emits SagaCompletedEvent via PublishOnceAsync
  // — exactly-once thanks to the claim-key dedup, so even in multi-pod scenarios where
  // each instance has its own in-memory view, only one emission lands.
  //
  // This is the "single-pod fast path." For multi-pod recovery (a per-item terminal event
  // dropped before the right pod saw it) the watchdog tick fires, the orchestrator loads
  // the consumer's saga projection through ISagaProjectionLoader, runs the event-store
  // reconciler, and emits SagaCompletedEvent from the watchdog path. The watchdog is the
  // safety net; the in-memory path keeps the common case fast and dependency-free.
  private readonly Lock _completionLock = new();
  private readonly Dictionary<Guid, _SagaCompletionTracker> _completionTrackers = [];

  private sealed class _SagaCompletionTracker {
    public Guid EntityId;
    public int Total;
    public int Completed;
    public int Failed;
    public bool DispatchedCompletion;
  }

  /// <summary>The saga name this service emits events for — matches the value supplied to <c>[Saga("Name")]</c>.</summary>
  protected string SagaName => _sagaName;

  protected BaseSagaService(string sagaName, ISagaEventEmitter emitter, ILogger logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    _sagaName = sagaName;
    _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  // ── Factory methods (consumer or generator fills in) ─────────────────

  protected abstract TInit BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt);
  protected abstract TItemsDispatched BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt);
  protected abstract TItemStarted BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt);
  protected abstract TItemCompleted BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt);
  protected abstract TItemFailed BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt);
  protected abstract TCompleted BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt);
  protected abstract TReset BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt);
  protected abstract THookStarted BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt);
  protected abstract THookCompleted BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt);

  // ── Lifecycle methods ────────────────────────────────────────────────

  public async Task InitiateSagaAsync(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(itemIdentifiers);
    cancellationToken.ThrowIfCancellationRequested();

    // Register the saga with the in-memory completion tracker before any item events fire.
    lock (_completionLock) {
      _completionTrackers[ctx.SagaId] = new _SagaCompletionTracker {
        EntityId = ctx.EntityId,
        Total = itemIdentifiers.Count
      };
    }

    var evt = BuildInitiatedEvent(ctx, itemIdentifiers, hookNames, DateTimeOffset.UtcNow);
    await _emitter.PublishAsync(evt).ConfigureAwait(false);

    // Framework-managed completion: arm a watchdog tick so the saga has a guaranteed
    // wake-up even if every per-item terminal event is silently dropped or strands the
    // projection. The orchestrator that handles this event runs the reconciler and
    // emits SagaCompletedEvent via PublishOnceAsync when the saga has reached terminal
    // state — or re-arms the watchdog with exponential backoff if not.
    //
    // ScheduledFor budget: "expected completion + slack" computed from item count. A
    // saga with N items typically finishes in O(N) — the heuristic budgets a small
    // base + per-item allowance so the first tick fires after the optimistic case
    // SHOULD have completed. Recovery loops (re-arms) layer their own exponential
    // backoff on top of this (30s → 2m → 8m → 30m → abandon).
    var watchdog = new SagaCompletionWatchdogTickEvent {
      SagaName = _sagaName,
      EntityId = ctx.EntityId,
      StreamId = ctx.SagaId,
      RescheduleCount = 0
    };
    var watchdogBudget = ComputeInitialWatchdogBudget(itemIdentifiers.Count);
    await _emitter.PublishAsync(watchdog, DateTimeOffset.UtcNow + watchdogBudget).ConfigureAwait(false);
  }

  /// <summary>
  /// Initial watchdog delay heuristic. Returns the time-from-now the first watchdog tick
  /// should fire at, given the saga's item count. <c>30s + (TotalItems * 100ms)</c> is a
  /// deliberate over-estimate — better to be late and let the per-item terminal events
  /// finish driving completion via the fast path than fire early and uselessly re-arm.
  /// Consumers that want a different budget override this method on their subclass.
  /// </summary>
  protected virtual TimeSpan ComputeInitialWatchdogBudget(int totalItems) {
    return TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(100L * totalItems);
  }

  public async Task ItemsDispatchedAsync(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    var evt = BuildItemsDispatchedEvent(ctx, totalItems, successfullyDispatched, failedToDispatch, DateTimeOffset.UtcNow);
    await _emitter.PublishAsync(evt).ConfigureAwait(false);
  }

  public async Task UpdateItemAsync(SagaContext ctx, string itemIdentifier, SagaItemState newStatus, string? displayName, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(itemIdentifier);
    cancellationToken.ThrowIfCancellationRequested();
    var now = DateTimeOffset.UtcNow;
    switch (newStatus) {
      case SagaItemState.Running:
        await _emitter.PublishAsync(BuildItemStartedEvent(ctx, itemIdentifier, displayName, now)).ConfigureAwait(false);
        break;
      case SagaItemState.Completed:
        await _emitter.PublishAsync(BuildItemCompletedEvent(ctx, itemIdentifier, displayName, now)).ConfigureAwait(false);
        await _tryAutoCompleteAsync(ctx, itemIdentifier, failed: false, cancellationToken).ConfigureAwait(false);
        break;
      case SagaItemState.Failed:
        throw new InvalidOperationException(
          "Use FailItemAsync to fail an item; UpdateItemAsync does not carry error context.");
      default:
        throw new InvalidOperationException(
          $"UpdateItemAsync only supports Running and Completed transitions. Got: {newStatus}");
    }
  }

  public async Task FailItemAsync(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(itemIdentifier);
    ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
    cancellationToken.ThrowIfCancellationRequested();
    await _emitter.PublishAsync(
      BuildItemFailedEvent(ctx, itemIdentifier, errorMessage, errorDetails, displayName, DateTimeOffset.UtcNow))
      .ConfigureAwait(false);
    await _tryAutoCompleteAsync(ctx, itemIdentifier, failed: true, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Framework-owned completion fast path. Called after every per-item terminal event
  /// (<see cref="UpdateItemAsync"/>(Completed), <see cref="FailItemAsync"/>) — advances
  /// the in-memory tracker; when (Completed + Failed) reaches the InitiateSagaAsync-supplied
  /// total, emits the SagaCompletedEvent via <see cref="CompleteSagaAsync"/>. Exactly-once
  /// across racing instances is guaranteed by <see cref="CompleteSagaAsync"/>'s
  /// PublishOnceAsync claim key, so the in-memory view doesn't have to be globally
  /// consistent — only the first emission lands.
  /// </summary>
  private async Task _tryAutoCompleteAsync(
      SagaContext ctx,
      string triggeringItemIdentifier,
      bool failed,
      CancellationToken cancellationToken) {
    int total, completed, failedCount;
    bool shouldEmit;
    lock (_completionLock) {
      if (!_completionTrackers.TryGetValue(ctx.SagaId, out var tracker) || tracker.DispatchedCompletion) {
        return;
      }
      if (failed) {
        tracker.Failed++;
      } else {
        tracker.Completed++;
      }
      total = tracker.Total;
      completed = tracker.Completed;
      failedCount = tracker.Failed;
      shouldEmit = total > 0 && completed + failedCount >= total;
      if (shouldEmit) {
        tracker.DispatchedCompletion = true;
      }
    }
    if (!shouldEmit) {
      return;
    }
    var finalStatus = failedCount > 0 ? SagaStatus.CompletedWithFailures : SagaStatus.Completed;
    await CompleteSagaAsync(
      ctx, finalStatus, triggeringItemIdentifier, completed, failedCount, total, cancellationToken)
      .ConfigureAwait(false);
  }

  /// <summary>
  /// Consumer-overridden hook used by the framework's watchdog recovery path to read the saga's
  /// authoritative state from the durable projection. Override and return the saga projection
  /// (typically loaded through the consumer's repository). The default returns <c>null</c>,
  /// which limits recovery to the in-memory fast path — useful for unit fixtures that don't
  /// need a real projection store.
  /// </summary>
  /// <remarks>
  /// In production this is wired to the consumer's <see cref="ILensQuery{T}"/>-backed saga
  /// repository so the watchdog can see what every pod's perspective worker has applied —
  /// the only source of truth resilient to single-instance in-memory loss (pod restart,
  /// per-item terminal events that landed on a different pod's tracker, …).
  /// </remarks>
  protected virtual Task<BaseSagaModel?> LoadProjectionAsync(Guid sagaId, CancellationToken cancellationToken) {
    return Task.FromResult<BaseSagaModel?>(null);
  }

  /// <summary>
  /// Framework recovery surface invoked when a watchdog tick fires. First tries the in-memory
  /// completion tracker (fast path — same path the per-item terminal events use); if the
  /// in-memory view doesn't show terminal but the consumer has wired a projection loader via
  /// <see cref="LoadProjectionAsync"/>, falls back to the projection — which catches the case
  /// where a per-item terminal event was dropped before the right pod's tracker saw it.
  /// </summary>
  /// <returns>
  /// <c>true</c> if recovery drove an emission attempt (regardless of whether THIS caller's
  /// <see cref="ISagaEventEmitter.PublishOnceAsync"/> won the claim — multiple watchdog ticks
  /// across pods can race, the claim dedups). <c>false</c> when no recovery was attempted
  /// (saga not registered, already terminal, projection unavailable, or projection counts
  /// not yet at terminal).
  /// </returns>
  public virtual async Task<bool> TryRecoverViaWatchdogAsync(SagaContext ctx, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();

    // Fast path — re-check the in-memory tracker. If a per-item terminal event came in
    // between the watchdog being armed and now, the in-memory state already reflects it.
    int total, completed, failedCount;
    bool inMemoryTerminal;
    lock (_completionLock) {
      if (!_completionTrackers.TryGetValue(ctx.SagaId, out var tracker) || tracker.DispatchedCompletion) {
        // Tracker missing (e.g. process restart since InitiateSaga) or already emitted — fall through to projection.
        total = 0;
        completed = 0;
        failedCount = 0;
        inMemoryTerminal = false;
      } else {
        total = tracker.Total;
        completed = tracker.Completed;
        failedCount = tracker.Failed;
        inMemoryTerminal = total > 0 && completed + failedCount >= total;
        if (inMemoryTerminal) {
          tracker.DispatchedCompletion = true;
        }
      }
    }
    if (inMemoryTerminal) {
      var status = failedCount > 0 ? SagaStatus.CompletedWithFailures : SagaStatus.Completed;
      await CompleteSagaAsync(
        ctx, status, completedByItemIdentifier: "watchdog", completed, failedCount, total, cancellationToken)
        .ConfigureAwait(false);
      return true;
    }

    // Slow path — load the projection. Consumer-overridden LoadProjectionAsync returns the
    // authoritative state when wired; default returns null.
    var saga = await LoadProjectionAsync(ctx.SagaId, cancellationToken).ConfigureAwait(false);
    if (saga is null) {
      return false;
    }
    if (saga.CompletionEventDispatched) {
      return false;
    }
    if (saga.TotalItems <= 0) {
      return false;
    }
    if (saga.CompletedItems + saga.FailedItems < saga.TotalItems) {
      return false;
    }
    var finalStatus = saga.FailedItems > 0 ? SagaStatus.CompletedWithFailures : SagaStatus.Completed;
    await CompleteSagaAsync(
      ctx, finalStatus, completedByItemIdentifier: "watchdog",
      saga.CompletedItems, saga.FailedItems, saga.TotalItems, cancellationToken)
      .ConfigureAwait(false);
    return true;
  }

  /// <summary>
  /// Emits the saga's terminal completion event exactly once — routes
  /// through <see cref="ISagaEventEmitter.PublishOnceAsync"/> with the
  /// saga claim-key convention so concurrent terminal handlers collapse
  /// to a single emission.
  /// </summary>
  /// <returns><c>true</c> if this caller won the claim and the event was published; <c>false</c> if another caller already won it.</returns>
  public async Task<bool> CompleteSagaAsync(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    var evt = BuildCompletedEvent(ctx, finalStatus, completedByItemIdentifier, completedItems, failedItems, totalItems, DateTimeOffset.UtcNow);
    var claimKey = SagaCompletionGuard.ClaimKey(_sagaName, ctx.SagaId);
    return await _emitter.PublishOnceAsync(claimKey, evt, cancellationToken).ConfigureAwait(false);
  }

  public async Task ResetItemAsync(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(itemIdentifier);
    cancellationToken.ThrowIfCancellationRequested();
    var evt = BuildResetEvent(ctx, itemIdentifier, previousStatus, DateTimeOffset.UtcNow);
    await _emitter.PublishAsync(evt).ConfigureAwait(false);
  }

  /// <summary>
  /// Runs <paramref name="work"/> bracketed by Hook Started and Completed
  /// events. Skips silently (returns <c>false</c>) when the hook has
  /// already terminated on <paramref name="sagaProjection"/> — relies on
  /// projection-side <c>Hooks</c> state for dedup. Returns <c>true</c>
  /// when the work actually ran.
  /// </summary>
  /// <remarks>
  /// Re-throws if <paramref name="work"/> throws — but publishes a
  /// Hook Completed (Failed) event first so the projection records the
  /// failure even when the receptor's transaction rolls back.
  /// </remarks>
  public async Task<bool> TryRunHookAsync(
      SagaContext ctx,
      Models.BaseSagaModel? sagaProjection,
      string hookName,
      string? displayName,
      Func<CancellationToken, Task> work,
      CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(hookName);
    ArgumentNullException.ThrowIfNull(work);
    cancellationToken.ThrowIfCancellationRequested();

    if (sagaProjection?.Hooks.FirstOrDefault(h => h.HookName == hookName) is { IsTerminal: true }) {
      LogHookSkipped(_logger, hookName, _sagaName, ctx.SagaId, null);
      return false;
    }

    var now = DateTimeOffset.UtcNow;
    await _emitter.PublishAsync(BuildHookStartedEvent(ctx, hookName, displayName, now)).ConfigureAwait(false);

    try {
      await work(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) {
      LogHookFailed(_logger, hookName, _sagaName, ctx.SagaId, ex);
      await _emitter.PublishAsync(
        BuildHookCompletedEvent(ctx, hookName, SagaItemState.Failed, ex.Message, ex.ToString(), DateTimeOffset.UtcNow))
        .ConfigureAwait(false);
      throw;
    }

    await _emitter.PublishAsync(
      BuildHookCompletedEvent(ctx, hookName, SagaItemState.Completed, null, null, DateTimeOffset.UtcNow))
      .ConfigureAwait(false);

    LogHookCompleted(_logger, hookName, _sagaName, ctx.SagaId, null);
    return true;
  }

  // ── LoggerMessage source-gen partials ────────────────────────────────

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Hook {HookName} already terminal on saga {SagaName} {SagaId} — skip")]
  private static partial void LogHookSkipped(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Hook {HookName} failed on saga {SagaName} {SagaId} — publishing Failed completion before rethrow")]
  private static partial void LogHookFailed(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Hook {HookName} completed on saga {SagaName} {SagaId}")]
  private static partial void LogHookCompleted(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);
}
