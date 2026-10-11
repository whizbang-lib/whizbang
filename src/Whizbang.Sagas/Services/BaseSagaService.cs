// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Repositories;

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
  : ISagaWatchdogParticipant
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
  private readonly ISagaItemRepository? _itemRepository;
  private readonly ISagaItemTerminalReader? _terminalReader;
  private readonly SagaOptions _options;
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
  private readonly Dictionary<Guid, SagaCompletionTracker> _completionTrackers = [];

  private sealed class SagaCompletionTracker {
    public Guid EntityId;
    public int Total;
    public int Completed;
    public int Failed;
    public bool DispatchedCompletion;
  }

  /// <summary>The saga name this service emits events for — matches the value supplied to <c>[Saga("Name")]</c>.</summary>
  protected string SagaName => _sagaName;

  /// <summary>
  /// The name the framework's watchdog router addresses this saga's ticks to. Implemented
  /// explicitly so it neither shadows <see cref="SagaName"/> nor the <c>SagaName</c> constant that
  /// <c>[Saga]</c>-generated receptors bind to.
  /// </summary>
  string ISagaWatchdogParticipant.SagaName => _sagaName;

  /// <summary>
  /// Backwards-compatible constructor that wires only the emitter + logger.
  /// <c>TryRecoverViaWatchdogAsync</c>'s slow path falls back to
  /// <c>LoadProjectionAsync</c>'s <c>CompletedItems</c>/<c>FailedItems</c> directly —
  /// suitable for sagas whose consumer projection accurately tracks per-item terminal
  /// counts (i.e., the projection's <c>Apply</c> chain receives per-item events).
  /// </summary>
  protected BaseSagaService(string sagaName, ISagaEventEmitter emitter, ILogger logger)
    : this(sagaName, emitter, itemRepository: null, terminalReader: null, options: null, logger) {
  }

  /// <summary>
  /// Constructor that wires the per-item aggregate primitives.
  /// <c>TryRecoverViaWatchdogAsync</c>'s slow path consults
  /// <see cref="SagaItemCompletionReconciler.ResolveCompletionCountsAsync"/> over
  /// <paramref name="itemRepository"/> + <paramref name="terminalReader"/> to derive
  /// authoritative completion counts — required when the consumer's saga projection
  /// doesn't see per-item terminal events on its <c>Apply</c> chain (the
  /// per-item-stream design), as is the case under multi-pod fan-out.
  /// </summary>
  protected BaseSagaService(
      string sagaName,
      ISagaEventEmitter emitter,
      ISagaItemRepository? itemRepository,
      ISagaItemTerminalReader? terminalReader,
      ILogger logger)
    : this(sagaName, emitter, itemRepository, terminalReader, options: null, logger) {
  }

  /// <summary>
  /// Full constructor — adds <see cref="SagaOptions"/> for tunable knobs (chiefly
  /// the watchdog re-arm <see cref="SagaOptions.WatchdogBackoff"/> schedule used by
  /// <c>TryRecoverViaWatchdogTickAsync</c>). Pass <c>null</c> to fall back to
  /// framework defaults.
  /// </summary>
  protected BaseSagaService(
      string sagaName,
      ISagaEventEmitter emitter,
      ISagaItemRepository? itemRepository,
      ISagaItemTerminalReader? terminalReader,
      SagaOptions? options,
      ILogger logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
    _sagaName = sagaName;
    ArgumentNullException.ThrowIfNull(emitter);
    _emitter = emitter;
    _itemRepository = itemRepository;
    _terminalReader = terminalReader;
    _options = options ?? new SagaOptions();
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
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
      _completionTrackers[ctx.SagaId] = new SagaCompletionTracker {
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
    try {
      await CompleteSagaAsync(
        ctx, finalStatus, triggeringItemIdentifier, completed, failedCount, total, cancellationToken)
        .ConfigureAwait(false);
    } catch {
      _releaseCompletionDispatch(ctx.SagaId);
      throw;
    }
  }

  /// <summary>
  /// Undoes the tracker's optimistic "completion dispatched" mark after an emission that did NOT
  /// happen. The flag is claimed BEFORE the emit so two threads cannot both drive one, but the
  /// emit can still fail — and a flag left set is permanent for this instance: every later
  /// per-item terminal short-circuits, and the watchdog's in-memory fast path declines too, so a
  /// saga with no projection loader wired is stranded by one transient publish failure. Releasing
  /// it costs nothing in exactly-once terms: <see cref="CompleteSagaAsync"/> claims through
  /// <c>PublishOnceAsync</c>, which is what actually dedups a retry.
  /// </summary>
  private void _releaseCompletionDispatch(Guid sagaId) {
    lock (_completionLock) {
      if (_completionTrackers.TryGetValue(sagaId, out var tracker)) {
        tracker.DispatchedCompletion = false;
      }
    }
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
    // Reaching the framework default means no loader is wired, so a null here says "cannot see",
    // not "the saga is gone". The watchdog tick reads this to keep re-arming instead of ending
    // the chain on a saga it merely cannot observe.
    _noProjectionLoaderWired = true;
    return Task.FromResult<BaseSagaModel?>(null);
  }

  /// <summary>
  /// Set once the framework-default <see cref="LoadProjectionAsync"/> runs: this service has no
  /// projection loader, so a missing projection is not evidence of a missing saga.
  /// </summary>
  private volatile bool _noProjectionLoaderWired;

  /// <summary>What one recovery attempt found, before it is reduced to the public boolean.</summary>
  private enum WatchdogRecoveryResult {
    NotYet,
    Recovered,
    AlreadyComplete,
    SagaNotFound,
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
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogAsyncTests.cs</tests>
  public virtual async Task<bool> TryRecoverViaWatchdogAsync(SagaContext ctx, CancellationToken cancellationToken)
    => await _tryRecoverAsync(ctx, cancellationToken).ConfigureAwait(false) == WatchdogRecoveryResult.Recovered;

  /// <summary>
  /// The recovery attempt behind <see cref="TryRecoverViaWatchdogAsync"/>, keeping apart the reasons
  /// it declines: a saga still in progress, one already complete and one that does not exist. The
  /// watchdog tick acts on the difference; the public surface only says whether it recovered.
  /// </summary>
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Recovery tries the in-memory tracker first and falls back to the projection, and the fallback has its own reasons not to run: no loader, the saga gone, completion already dispatched, no items, or counts not yet terminal. The two paths and their exits are the recovery contract the summary describes.")]
  private async Task<WatchdogRecoveryResult> _tryRecoverAsync(SagaContext ctx, CancellationToken cancellationToken) {
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
      try {
        await CompleteSagaAsync(
          ctx, status, completedByItemIdentifier: "watchdog", completed, failedCount, total, cancellationToken)
          .ConfigureAwait(false);
      } catch {
        _releaseCompletionDispatch(ctx.SagaId);
        throw;
      }
      return WatchdogRecoveryResult.Recovered;
    }

    // Slow path — load the projection for TotalItems + CompletionEventDispatched (both come
    // from the saga's own stream so the consumer projection tracks them reliably). Counts
    // (CompletedItems / FailedItems) are NOT trusted from the projection when the per-item
    // aggregate primitives are wired: per-item terminal events ride per-item streams and
    // never reach the saga projection's Apply chain, so its CompletedItems can be stale 0
    // even when every item is terminal in the durable event store.
    var saga = await LoadProjectionAsync(ctx.SagaId, cancellationToken).ConfigureAwait(false);
    if (saga is null) {
      return _noProjectionLoaderWired ? WatchdogRecoveryResult.NotYet : WatchdogRecoveryResult.SagaNotFound;
    }
    if (saga.CompletionEventDispatched) {
      return WatchdogRecoveryResult.AlreadyComplete;
    }
    if (saga.TotalItems <= 0) {
      return WatchdogRecoveryResult.NotYet;
    }

    int authoritativeCompleted, authoritativeFailed;
    if (_itemRepository is not null && _terminalReader is not null) {
      // Framework-owned per-item aggregate: cheap GROUP BY on the projection table, with
      // event-store reconciliation for cross-pod-stranded rows. ResolveCompletionCountsAsync
      // returns null when the saga is genuinely still in progress — fall through to "no
      // recovery yet" without ever consulting the consumer projection's count fields.
      var agg = await _itemRepository.GetAggregateForSagaAsync(ctx.SagaId, cancellationToken).ConfigureAwait(false);
      var reconciled = await SagaItemCompletionReconciler.ResolveCompletionCountsAsync(
          ctx.SagaId, saga.TotalItems, agg,
          ct => _itemRepository.GetItemsAsync(ctx.SagaId, ct),
          _terminalReader, cancellationToken).ConfigureAwait(false);
      if (reconciled is not { } counts) {
        return WatchdogRecoveryResult.NotYet;
      }
      authoritativeCompleted = counts.Completed;
      authoritativeFailed = counts.Failed;
    } else {
      // Backwards-compatible path: trust the consumer projection's counts directly.
      if (saga.CompletedItems + saga.FailedItems < saga.TotalItems) {
        return WatchdogRecoveryResult.NotYet;
      }
      authoritativeCompleted = saga.CompletedItems;
      authoritativeFailed = saga.FailedItems;
    }

    var finalStatus = authoritativeFailed > 0 ? SagaStatus.CompletedWithFailures : SagaStatus.Completed;
    await CompleteSagaAsync(
      ctx, finalStatus, completedByItemIdentifier: "watchdog",
      authoritativeCompleted, authoritativeFailed, saga.TotalItems, cancellationToken)
      .ConfigureAwait(false);

    // Defense in depth: now that the slow path has won the SagaCompletedEvent claim, mark the
    // in-memory tracker so a late per-item terminal arriving on this instance after this
    // recovery can't drive a duplicate auto-complete attempt. PublishOnceAsync's claim key
    // already dedups at the dispatcher layer; this just avoids the wasted PublishOnceAsync
    // round-trip and keeps the in-memory view consistent with the projection.
    _markCompletionDispatched(ctx.SagaId);
    return WatchdogRecoveryResult.Recovered;
  }

  /// <summary>
  /// Watchdog-tick entry point used by the framework's tick receptor (or by a
  /// consumer-written receptor on <see cref="SagaCompletionWatchdogTickEvent"/>).
  /// Runs the same recovery as <see cref="TryRecoverViaWatchdogAsync"/> and, when the
  /// saga is still in progress, computes an adaptive next-tick delay from the
  /// observed completion rate (see <c>_computeAdaptiveNextDelay</c>) — or, when
  /// <see cref="SagaOptions.MaxConsecutiveStalls"/> is reached, publishes
  /// <see cref="SagaCompletionAbandonedEvent"/> once, under the saga's
  /// <see cref="SagaAbandonGuard"/> claim, so operators can triage the stuck saga.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Separating this from <see cref="TryRecoverViaWatchdogAsync"/> keeps the
  /// "just check completion" surface available for callers that don't want to
  /// drive the re-arm lifecycle (e.g., a per-item terminal receptor nudging
  /// recovery on every terminal event — Component 3 of the
  /// <c>sagas-framework-owns-completion</c> plan).
  /// </para>
  /// <para>
  /// The next-tick payload preserves <c>StreamId</c>, <c>SagaName</c>, and
  /// <c>EntityId</c> so the existing message-registry routing and
  /// <c>notify_instance_owners</c> wake delivery keep working without a
  /// special case. The new tick is emitted via
  /// <see cref="ISagaEventEmitter.PublishAsync{TEvent}(TEvent, DateTimeOffset?)"/>
  /// with <c>scheduledFor</c> populated so <c>wh_outbox.scheduled_for</c> is
  /// set as designed.
  /// </para>
  /// <para>
  /// A tick that finds the saga already complete (its projection carries
  /// <c>CompletionEventDispatched</c>) or missing (the projection loader returns no saga) ends the
  /// chain: no next tick, no stall counted, no stranded item resolved and no abandon event. That
  /// is the normal fate of a tick, because the per-item fast path usually completes a saga before
  /// its tick fires. The tick consults the recovery path directly for this, rather than through the
  /// overridable boolean <see cref="TryRecoverViaWatchdogAsync"/>, which cannot tell "done" from
  /// "not yet".
  /// </para>
  /// </remarks>
  /// <returns>
  /// <see cref="WatchdogTickOutcome.Recovered"/> when the slow path emitted completion;
  /// <see cref="WatchdogTickOutcome.AlreadyComplete"/> or <see cref="WatchdogTickOutcome.SagaNotFound"/>
  /// when there was nothing left to watch; <see cref="WatchdogTickOutcome.ReArmed"/> when a next tick
  /// was scheduled; <see cref="WatchdogTickOutcome.Abandoned"/> when the schedule exhausted and the
  /// abandon event was published.
  /// </returns>
  /// <docs>fundamentals/sagas/completion-orchestration#watchdog-tick-outcomes</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:FirstReArm_NoSnapshot_UsesInitialBudgetAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:ProgressBetweenTicks_NextDelayIsEtaBasedAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:NoProgressBetweenTicks_StallCounterIncrementsAndBacksOffAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_AbandonsAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_AbandonsUnderTheSagasAbandonmentClaimAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_AbandonmentAlreadyClaimed_PublishesNoSecondAbandonEventAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:ProgressAfterStalls_ResetsStallCounterAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:NextDelay_ClampedAtMaxAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:CompletedSaga_TickWithProgress_EndsTheChainAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:CompletedSaga_TickAtTheStallLimit_NeitherResolvesItemsNorAbandonsAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MissingSaga_TickEndsTheChainAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/BaseSagaServiceCoverageTests.cs:TryRecoverViaWatchdogTickAsync_WithNoProjectionLoaderWired_StillReArmsAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_ItemsWithNoRow_AreFailedAndTheSagaCompletesAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_NoItemRowAtAll_IsAbandonedUnlessTheExpectedItemsAreKnownAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_ItemsWithNoRowButNoKnownTotal_IsAbandonedAsync</tests>
  public virtual async Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(
      SagaCompletionWatchdogTickEvent tick,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(tick);
    cancellationToken.ThrowIfCancellationRequested();

    var ctx = new SagaContext(tick.StreamId, tick.EntityId);
    if (_toTerminalOutcome(await _tryRecoverAsync(ctx, cancellationToken).ConfigureAwait(false)) is { } ended) {
      return ended;
    }

    // Read the current per-item snapshot up front so the next-delay computation and the
    // event payload that carries the snapshot forward see identical numbers.
    SagaItemAggregate? currentAgg = null;
    if (_itemRepository is not null) {
      currentAgg = await _itemRepository.GetAggregateForSagaAsync(ctx.SagaId, cancellationToken).ConfigureAwait(false);
    }
    var now = DateTimeOffset.UtcNow;

    var (nextDelay, nextStallCount, shouldAbandon) = _computeAdaptiveNextDelay(tick, currentAgg, now);
    if (shouldAbandon) {
      return await _atTheStallLimitAsync(tick, ctx, currentAgg, now, cancellationToken).ConfigureAwait(false);
    }

    await _emitter.PublishAsync(_nextTick(tick, currentAgg, now, nextStallCount), now + nextDelay).ConfigureAwait(false);
    return WatchdogTickOutcome.ReArmed;
  }

  /// <summary>
  /// Re-drives a saga the completion watchdog abandoned: releases its abandonment claim and arms a
  /// fresh watchdog tick, with the whole stall budget, at once.
  /// </summary>
  /// <remarks>
  /// <para>
  /// An operator's act. Abandoning a saga decided it was not coming back on its own, and the
  /// stranded-saga sweep leaves it alone from then on. Once the cause is dealt with (a worker
  /// restored, an item re-dispatched), this puts the saga back under the watchdog: the tick checks
  /// completion straight away, and a saga still not moving is abandoned again only after
  /// <see cref="SagaOptions.MaxConsecutiveStalls"/> more stalls.
  /// </para>
  /// <para>
  /// A saga whose perspective recorded <see cref="SagaStatus.Abandoned"/> is also skipped by the sweep
  /// on that status, and cannot record a completion from it; move it back to running through the reset
  /// path as well. A saga holding no abandonment claim is left as it is and nothing is armed, so calling
  /// this for a saga that still has its chain cannot start a second one beside it.
  /// </para>
  /// </remarks>
  /// <param name="ctx">The abandoned saga.</param>
  /// <param name="cancellationToken">Cancels the re-drive.</param>
  /// <returns><see langword="true"/> when the saga held an abandonment claim and was re-driven.</returns>
  /// <docs>fundamentals/sagas/completion-orchestration#abandoned-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:ReDrive_AbandonedSaga_ReleasesTheClaimAndArmsAFreshTickAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:ReDrive_SagaNotAbandoned_ArmsNothingAsync</tests>
  public async Task<bool> ReDriveAbandonedSagaAsync(SagaContext ctx, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    if (!await _emitter.ReleaseClaimAsync(SagaAbandonGuard.ClaimKey(_sagaName, ctx.SagaId), cancellationToken).ConfigureAwait(false)) {
      return false;
    }
    await _emitter.PublishAsync(new SagaCompletionWatchdogTickEvent {
      StreamId = ctx.SagaId,
      SagaName = _sagaName,
      EntityId = ctx.EntityId,
      RescheduleCount = 0,
    }).ConfigureAwait(false);
    return true;
  }

  /// <summary>
  /// The watchdog's next tick: the same saga one reschedule later, carrying the progress observed now
  /// (none when there is no item repository to observe it from) and the stall count it continues with.
  /// </summary>
  private static SagaCompletionWatchdogTickEvent _nextTick(
      SagaCompletionWatchdogTickEvent tick, SagaItemAggregate? observed, DateTimeOffset now, int stallCount) => new() {
        StreamId = tick.StreamId,
        SagaName = tick.SagaName,
        EntityId = tick.EntityId,
        RescheduleCount = tick.RescheduleCount + 1,
        LastObservedAt = now,
        LastObservedCompleted = observed?.Completed ?? 0,
        LastObservedFailed = observed?.Failed ?? 0,
        ConsecutiveStallCount = stallCount,
      };

  /// <summary>
  /// What a tick does once the saga has made no progress across the whole stall limit: resolve what
  /// it can, complete the saga if nothing is left that could still move, and abandon it only when
  /// neither is possible.
  /// </summary>
  private async Task<WatchdogTickOutcome> _atTheStallLimitAsync(
      SagaCompletionWatchdogTickEvent tick, SagaContext ctx, SagaItemAggregate? currentAgg, DateTimeOffset now,
      CancellationToken cancellationToken) {
    var items = _itemRepository is null
      ? null
      : await _itemRepository.GetItemsAsync(ctx.SagaId, cancellationToken).ConfigureAwait(false);

    if (await _resolveStrandedItemsAsync(ctx, items, cancellationToken).ConfigureAwait(false) > 0) {
      // Stranded items were failed or re-dispatched. Complete now if that finished the saga, and
      // otherwise wake again with the stall count reset, so the new terminal events have time to
      // reach the projection before the saga is judged stuck a second time.
      if (_toTerminalOutcome(await _tryRecoverAsync(ctx, cancellationToken).ConfigureAwait(false)) is { } endedAfterResolve) {
        return endedAfterResolve;
      }
      await _emitter.PublishAsync(_nextTick(tick, currentAgg, now, stallCount: 0), now + _options.MinWatchdogDelay).ConfigureAwait(false);
      return WatchdogTickOutcome.ReArmed;
    }

    if (await _tryCompleteWithUnstartedItemsAsync(ctx, items, cancellationToken).ConfigureAwait(false)) {
      return WatchdogTickOutcome.Recovered;
    }

    // Claimed, so the abandonment is recorded for every consumer, not only one whose perspective
    // applies the event: the sweep leaves a saga holding this claim alone, and a tick from an older
    // chain that reaches the stall limit again publishes nothing.
    var abandoned = new SagaCompletionAbandonedEvent {
      StreamId = tick.StreamId,
      SagaName = tick.SagaName,
      EntityId = tick.EntityId,
      RescheduleCount = tick.RescheduleCount,
    };
    await _emitter.PublishOnceAsync(SagaAbandonGuard.ClaimKey(_sagaName, ctx.SagaId), abandoned, cancellationToken)
      .ConfigureAwait(false);
    return WatchdogTickOutcome.Abandoned;
  }

  /// <summary>
  /// Completes a saga whose recorded items are all terminal but fewer than its total, counting the
  /// items that never got a row as failed.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A fan-out that stopped partway (its worker died after starting 337 of 350 items) leaves fewer item
  /// rows than the saga's total. The reconciler counts terminal rows against the total and can never
  /// reach it, and stranded-item resolution only reaches rows that exist, so the saga used to be
  /// abandoned with every finished item discarded. At the stall limit, with every recorded row
  /// terminal, nothing else can move: the items with no row were never going to start. The saga
  /// completes as <see cref="SagaStatus.CompletedWithFailures"/>, with failed = total − completed.
  /// </para>
  /// <para>
  /// This is the fallback. A service that overrides <see cref="LoadExpectedItemIdentifiersAsync"/> has
  /// each missing item failed by name first, which records which items did not run; this then applies
  /// only to what that could not name. A saga with no item row at all is not completed on this
  /// evidence and is abandoned, and neither is one whose total cannot be read.
  /// </para>
  /// <para>
  /// A row still Pending or Running here is one the store already records as terminal (any other would
  /// have been resolved first), so it counts as the store records it.
  /// </para>
  /// </remarks>
  private async Task<bool> _tryCompleteWithUnstartedItemsAsync(
      SagaContext ctx, IReadOnlyList<SagaItemModel>? items, CancellationToken cancellationToken) {
    if (items is not { Count: > 0 }) {
      return false;
    }
    var saga = await LoadProjectionAsync(ctx.SagaId, cancellationToken).ConfigureAwait(false);
    if (saga is not { TotalItems: > 0 }) {
      return false;
    }

    var completed = items.Count(i => i.State == SagaItemState.Completed);
    if (_terminalReader is not null) {
      foreach (var item in items.Where(i => !i.IsTerminal)) {
        var stored = await _terminalReader.CheckAsync(SagaItemStreams.Of(ctx.SagaId, item.ItemIdentifier), cancellationToken)
          .ConfigureAwait(false);
        if (stored == SagaItemTerminalOutcome.Completed) {
          completed++;
        }
      }
    }

    // The recovery this tick ran first declined, so fewer than TotalItems items completed and the
    // difference is at least one.
    await CompleteSagaAsync(
      ctx, SagaStatus.CompletedWithFailures, completedByItemIdentifier: "watchdog",
      completed, saga.TotalItems - completed, saga.TotalItems, cancellationToken).ConfigureAwait(false);
    _markCompletionDispatched(ctx.SagaId);
    return true;
  }

  /// <summary>
  /// Marks the in-memory tracker once a recovery path has claimed the completion, so a late per-item
  /// terminal on this instance does not drive a second attempt.
  /// </summary>
  private void _markCompletionDispatched(Guid sagaId) {
    lock (_completionLock) {
      if (_completionTrackers.TryGetValue(sagaId, out var tracker)) {
        tracker.DispatchedCompletion = true;
      }
    }
  }

  /// <summary>
  /// The tick outcome that ends the watchdog chain for a recovery result, or <see langword="null"/>
  /// when the saga is still in progress and the tick goes on to re-arm, stall or abandon.
  /// </summary>
  private static WatchdogTickOutcome? _toTerminalOutcome(WatchdogRecoveryResult result) => result switch {
    WatchdogRecoveryResult.Recovered => WatchdogTickOutcome.Recovered,
    WatchdogRecoveryResult.AlreadyComplete => WatchdogTickOutcome.AlreadyComplete,
    WatchdogRecoveryResult.SagaNotFound => WatchdogTickOutcome.SagaNotFound,
    _ => null,
  };

  /// <summary>
  /// The sagas the stranded-saga sweep should consider: this service's incomplete sagas, each with its
  /// tenant.
  /// </summary>
  /// <remarks>
  /// Override to enumerate them from the saga projection, across tenants. The default returns none,
  /// and the sweep then does nothing for this saga, which is how every saga service behaved before
  /// the sweep existed. Returning sagas that are already complete is harmless; they are skipped.
  /// </remarks>
  /// <param name="cancellationToken">Cancels the read.</param>
  /// <returns>The incomplete sagas, each with its tenant.</returns>
  /// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs</tests>
  protected virtual Task<IReadOnlyList<IncompleteSaga>> LoadIncompleteSagasAsync(CancellationToken cancellationToken)
    => Task.FromResult<IReadOnlyList<IncompleteSaga>>([]);

  /// <inheritdoc cref="ISagaWatchdogParticipant.ArmStrandedSagasAsync"/>
  /// <remarks>
  /// <para>
  /// The sweep runs on a maintenance worker with no ambient scope, and one sweep crosses tenants. Each
  /// saga's work (its last-activity read, its aggregate read and the tick it arms) therefore runs inside
  /// that saga's tenant through <see cref="ISagaEventEmitter.RunInTenantAsync{TResult}"/>, so an item
  /// repository reading through a tenant-scoped lens sees the right tenant. A saga with no tenant is
  /// read in the worker's own context, as before.
  /// </para>
  /// <para>
  /// A failure in one saga's work is logged and the sweep moves on to the next saga, so one bad read
  /// cannot strand every saga swept after it; the failed saga is retried next cycle. Only the sweep's
  /// own cancellation stops it. The count returned is of ticks actually armed.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/sagas/completion-orchestration#stranded-sagas</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_OneSagasReadThrows_TheOthersAreStillArmedAndTheFailureIsLoggedAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_CanceledMidSweep_StopsTheSweepAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_OperationCanceledWithoutTheSweepBeingCanceled_IsTreatedAsThatSagasFailureAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_ItemRepositoryRequiresTenantScope_ReadsInTheSagasTenantAndArmsTheTickAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_SagasInDifferentTenants_EachIsReadAndArmedInItsOwnTenantAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_SagaWithNoTenant_IsReadInTheWorkersOwnContextAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_TickLostAndSagaStillStranded_IsReArmedAfterTheInterval_NotBeforeAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_SeveralInstancesInOneInterval_ArmOneTickAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_TickStillComing_IsNotReArmedHoweverManyIntervalsHavePassedAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_AbandonedSaga_IsNotReArmedInLaterIntervalsAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/StrandedSagaSweepTests.cs:Sweep_ArmsARunningSaga_BesideOneHoldingItsAbandonmentClaimAsync</tests>
  public virtual async Task<int> ArmStrandedSagasAsync(ISagaWakeLookup wakes, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(wakes);

    // An abandoned saga is not coming back on its own, which is what abandoning it decided. Re-arming
    // it once per interval published its abandonment again each time and told an operator nothing new.
    // The status covers a saga whose perspective records it; the abandonment claim covers every saga,
    // including one whose perspective does not. Re-driving it is an explicit act:
    // ReDriveAbandonedSagaAsync.
    var candidates = (await LoadIncompleteSagasAsync(cancellationToken).ConfigureAwait(false))
      .Where(c => !c.Saga.CompletionEventDispatched
               && c.Saga.TotalItems > 0
               && c.Saga.Status != SagaStatus.Abandoned)
      .ToList();
    if (candidates.Count > 0) {
      var abandoned = await _emitter.FindClaimedAsync(
          [.. candidates.Select(c => SagaAbandonGuard.ClaimKey(_sagaName, c.Saga.Id))], cancellationToken)
        .ConfigureAwait(false);
      candidates.RemoveAll(c => abandoned.Contains(SagaAbandonGuard.ClaimKey(_sagaName, c.Saga.Id)));
    }
    if (candidates.Count == 0) {
      return 0;
    }

    // One question for the whole set. Unknown is answered as "a tick is coming": arming beside a live
    // chain doubles it forever, while leaving a stranded saga for the next sweep costs one interval.
    var pending = await wakes.WithPendingWakeAsync([.. candidates.Select(c => c.Saga.Id)], cancellationToken)
      .ConfigureAwait(false);
    if (pending is null) {
      return 0;
    }

    var now = _options.TimeProvider.GetUtcNow();
    var armed = 0;
    foreach (var (saga, tenantId) in candidates.Where(c => !pending.Contains(c.Saga.Id))) {
      try {
        if (await _emitter.RunInTenantAsync(tenantId, ct => _sweepOneAsync(saga, tenantId, now, ct), cancellationToken)
            .ConfigureAwait(false)) {
          armed++;
        }
      } catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
        // One saga's failure is that saga's alone: it is retried next cycle, and the sagas after it are
        // still swept. Only the sweep's own cancellation stops the sweep.
        LogStrandedSagaSweepFailed(_logger, _sagaName, saga.Id, tenantId, ex);
      }
    }
    return armed;
  }

  /// <summary>
  /// One saga's share of the sweep, run inside its tenant: arms a tick when it has been idle past the guard.
  /// </summary>
  private async Task<bool> _sweepOneAsync(
      BaseSagaModel saga, string? tenantId, DateTimeOffset now, CancellationToken cancellationToken) {
    var lastActivity = await _lastActivityAsync(saga, cancellationToken).ConfigureAwait(false);
    if (now - lastActivity < _options.StrandedSagaIdleGuard) {
      return false;
    }
    return await _armStrandedAsync(saga, tenantId, lastActivity, now, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>The newest change to the saga or any of its items.</summary>
  private async Task<DateTimeOffset> _lastActivityAsync(BaseSagaModel saga, CancellationToken cancellationToken) {
    var itemActivity = _itemRepository is null
      ? null
      : await _itemRepository.GetLastActivityAsync(saga.Id, cancellationToken).ConfigureAwait(false);
    return itemActivity > saga.UpdatedAt ? itemActivity.Value : saga.UpdatedAt;
  }

  /// <summary>
  /// Arms one tick for a stranded saga, already at the stall limit: the saga has been still for longer
  /// than a whole stall count, so the tick resolves stranded items on arrival instead of serving that
  /// count again. Claimed on the saga, the time of its last change and the number of whole
  /// <see cref="SagaOptions.StrandedSagaRearmInterval"/>s it has been still since, so every instance and
  /// restart sweeping the same stop in one interval arrive at one tick, a saga that moves and stops
  /// again gets one more, and a saga whose tick was lost gets another once per interval (#935).
  /// </summary>
  /// <remarks>
  /// The first interval keeps the key the sweep used before re-arming existed, so a saga already armed
  /// by an earlier version in that interval is not armed twice.
  /// </remarks>
  private async Task<bool> _armStrandedAsync(
      BaseSagaModel saga, string? tenantId, DateTimeOffset lastActivity, DateTimeOffset now, CancellationToken cancellationToken) {
    var agg = _itemRepository is null
      ? null
      : await _itemRepository.GetAggregateForSagaAsync(saga.Id, cancellationToken).ConfigureAwait(false);
    var tick = new SagaCompletionWatchdogTickEvent {
      StreamId = saga.Id,
      SagaName = _sagaName,
      EntityId = saga.EntityId ?? Guid.Empty,
      LastObservedAt = now,
      LastObservedCompleted = agg?.Completed ?? saga.CompletedItems,
      LastObservedFailed = agg?.Failed ?? saga.FailedItems,
      ConsecutiveStallCount = Math.Max(0, _options.MaxConsecutiveStalls - 1),
    };
    var rearm = (now - lastActivity).Ticks / _options.StrandedSagaRearmInterval.Ticks;
    var claimKey = $"saga-watchdog-sweep:{_sagaName}:{saga.Id:N}:{lastActivity.UtcTicks}";
    if (rearm > 0) {
      claimKey += $":rearm-{rearm}";
    }
    return await _emitter.PublishOnceInTenantAsync(tenantId, claimKey, tick, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Why a stranded item is failed; carried on the item's failed event.</summary>
  private const string STRANDED_ITEM_MESSAGE =
    "No terminal event was recorded for this item across every watchdog check before the stall limit; " +
    "the worker processing it was most likely lost.";

  /// <summary>Why an expected item that never got a row is failed; carried on the item's failed event.</summary>
  private const string UNSTARTED_ITEM_MESSAGE =
    "The item never started: no item row or terminal event was recorded for it before the stall limit, " +
    "so the fan-out that would have started it was most likely lost.";

  /// <summary>
  /// Resolves items left non-terminal once the saga has made no progress across the stall limit.
  /// </summary>
  /// <remarks>
  /// <para>
  /// At the stall limit no item has moved across every watchdog check. An item still
  /// non-terminal then, with no terminal event in its per-item stream either, was being processed by a worker
  /// that no longer exists: the message that would finish it went with the worker, so nothing will
  /// retry it and nothing will dead-letter it. Left alone, the saga could only be abandoned —
  /// discarding every item that did finish.
  /// </para>
  /// <para>
  /// Each such item is offered to <see cref="TryRedriveStrandedItemAsync"/>; one that is not
  /// re-dispatched is failed with a reason, so the saga completes with the failure visible instead of
  /// hanging on it. An item the store already records as terminal is skipped: there the projection
  /// is merely behind, which the reconciler resolves. Without an item repository nothing can be
  /// enumerated, and the saga is abandoned exactly as before.
  /// </para>
  /// <para>
  /// When the service can say which items the saga was started with
  /// (<see cref="LoadExpectedItemIdentifiersAsync"/>), each one with no row at all is resolved the same
  /// way, as an item that never started.
  /// </para>
  /// </remarks>
  /// <returns>How many items were failed or re-dispatched.</returns>
  private async Task<int> _resolveStrandedItemsAsync(
      SagaContext ctx, IReadOnlyList<SagaItemModel>? items, CancellationToken cancellationToken) {
    if (items is null) {
      return 0;
    }

    var resolved = 0;
    foreach (var item in items.Where(i => i.State is SagaItemState.Pending or SagaItemState.Running)) {
      var details = $"Started {item.StartedAt:O}; attempts {item.AttemptCount}; state {item.State}.";
      resolved += await _resolveItemAsync(ctx, item, STRANDED_ITEM_MESSAGE, details, cancellationToken).ConfigureAwait(false);
    }

    var expected = await LoadExpectedItemIdentifiersAsync(ctx, cancellationToken).ConfigureAwait(false);
    if (expected is not null) {
      var recorded = items.Select(i => i.ItemIdentifier).ToHashSet(StringComparer.Ordinal);
      foreach (var identifier in expected.Where(id => !recorded.Contains(id)).Distinct(StringComparer.Ordinal)) {
        var unstarted = new SagaItemModel {
          SagaId = ctx.SagaId,
          SagaName = _sagaName,
          ItemIdentifier = identifier,
          State = SagaItemState.Pending,
        };
        resolved += await _resolveItemAsync(ctx, unstarted, UNSTARTED_ITEM_MESSAGE, "No item row was recorded for it.", cancellationToken)
          .ConfigureAwait(false);
      }
    }
    return resolved;
  }

  /// <summary>
  /// Re-drives or fails one item that nothing will finish, unless the store already records it as
  /// terminal.
  /// </summary>
  /// <returns>1 when the item was re-dispatched or failed, 0 when the store already had it.</returns>
  private async Task<int> _resolveItemAsync(
      SagaContext ctx, SagaItemModel item, string message, string details, CancellationToken cancellationToken) {
    if (_terminalReader is not null) {
      var stored = await _terminalReader.CheckAsync(SagaItemStreams.Of(ctx.SagaId, item.ItemIdentifier), cancellationToken)
        .ConfigureAwait(false);
      if (stored != SagaItemTerminalOutcome.NotTerminal) {
        return 0;
      }
    }

    if (!await TryRedriveStrandedItemAsync(ctx, item, cancellationToken).ConfigureAwait(false)) {
      await FailItemAsync(ctx, item.ItemIdentifier, message, details, item.DisplayName, cancellationToken).ConfigureAwait(false);
    }
    return 1;
  }

  /// <summary>
  /// The identifiers of every item the saga was started with, or <see langword="null"/> when this
  /// service cannot say.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Consulted only at the watchdog's stall limit. An item listed here with no item row at all was
  /// never started: the fan-out that would have started it stopped partway, typically because its
  /// worker died. Each such item is offered to <see cref="TryRedriveStrandedItemAsync"/> and, if not
  /// re-dispatched, failed with a reason, exactly as an item whose worker was lost is, so the saga can
  /// complete with every failure visible against the item that failed.
  /// </para>
  /// <para>
  /// Override when the identifiers can be read back, for example from the saga's initiation event or
  /// the command that started it. The default returns <see langword="null"/>; the watchdog then
  /// counts the items with no row as failed, without naming them, once every recorded row is terminal.
  /// </para>
  /// </remarks>
  /// <param name="ctx">The saga.</param>
  /// <param name="cancellationToken">Cancels the read.</param>
  /// <returns>The expected item identifiers, or <see langword="null"/>.</returns>
  /// <docs>fundamentals/sagas/completion-orchestration#items-that-never-started</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_ExpectedItemsKnown_FailsEachItemThatNeverGotARowAsync</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_ItemsWithNoRow_AreFailedAndTheSagaCompletesAsync</tests>
  protected virtual Task<IReadOnlyList<string>?> LoadExpectedItemIdentifiersAsync(SagaContext ctx, CancellationToken cancellationToken)
    => Task.FromResult<IReadOnlyList<string>?>(null);

  /// <summary>
  /// Offers a stranded item back to the saga service before it is failed.
  /// </summary>
  /// <remarks>
  /// Called when the saga has made no progress across the watchdog's stall limit and this item has no
  /// terminal event anywhere — its worker was most likely lost. A service that can safely re-dispatch
  /// the item's work (its handler is idempotent) should do so and return <see langword="true"/>; the
  /// item then stays in progress and the watchdog keeps watching. The default returns
  /// <see langword="false"/>, and the item is failed with a reason so the saga can finish.
  /// </remarks>
  /// <param name="ctx">The saga the item belongs to.</param>
  /// <param name="item">The stranded item, as the item projection last recorded it.</param>
  /// <param name="cancellationToken">Cancels the attempt.</param>
  /// <returns><see langword="true"/> when the item's work was re-dispatched.</returns>
  /// <docs>fundamentals/sagas/completion-orchestration#stranded-items</docs>
  /// <tests>tests/Whizbang.Sagas.Tests/Services/TryRecoverViaWatchdogTickAsyncTests.cs:MaxConsecutiveStalls_ConsumerRedrivesTheItem_ItIsNotFailedAsync</tests>
  protected virtual Task<bool> TryRedriveStrandedItemAsync(SagaContext ctx, SagaItemModel item, CancellationToken cancellationToken)
    => Task.FromResult(false);

  /// <summary>
  /// Adaptive next-tick delay computation. Three branches:
  /// <list type="bullet">
  /// <item><description><b>First tick (no snapshot):</b> use <see cref="ComputeInitialWatchdogBudget"/>
  ///   over the current item total — the same "expected completion + slack" heuristic the
  ///   initial post-Initiate tick uses. Stall count stays at 0 because we haven't yet measured a
  ///   delta to compare against.</description></item>
  /// <item><description><b>Progress observed (delta &gt; 0):</b> compute items-per-second from
  ///   <c>delta / elapsed</c>, project remaining work to an ETA, add the configured safety margin.
  ///   Stall count resets to 0 — a saga that's moving doesn't trigger the abandon counter even
  ///   if it's moving slowly.</description></item>
  ///   <item><description><b>No progress (delta == 0):</b> increment stall count. If it reaches
  ///   <see cref="SagaOptions.MaxConsecutiveStalls"/> return <c>shouldAbandon=true</c>. Otherwise
  ///   widen the next interval by <c>MinWatchdogDelay * Multiplier^stallCount</c> — only stuck
  ///   sagas back off exponentially.</description></item>
  /// </list>
  /// The returned delay is always clamped to
  /// <c>[MinWatchdogDelay, MaxWatchdogDelay]</c> so a very fast burst or a near-zero rate
  /// can't produce an unbounded interval.
  /// </summary>
  private (TimeSpan delay, int nextStallCount, bool shouldAbandon) _computeAdaptiveNextDelay(
      SagaCompletionWatchdogTickEvent tick,
      SagaItemAggregate? agg,
      DateTimeOffset now) {
    var min = _options.MinWatchdogDelay;
    var max = _options.MaxWatchdogDelay;

    // Branch 1: no prior measurement (the post-Initiate tick was the first; this is its re-arm).
    if (tick.LastObservedAt is not { } lastAt) {
      var initial = ComputeInitialWatchdogBudget(agg?.Total ?? 0);
      return (_clamp(initial, min, max), 0, false);
    }

    var previousTerminal = tick.LastObservedCompleted + tick.LastObservedFailed;
    var currentTerminal = (agg?.Completed ?? 0) + (agg?.Failed ?? 0);
    var delta = currentTerminal - previousTerminal;

    // Branch 2: progress made — ETA-based.
    if (delta > 0 && agg is not null) {
      var elapsed = now - lastAt;
      if (elapsed > TimeSpan.Zero) {
        var rate = delta / elapsed.TotalSeconds;
        var remaining = agg.Total - currentTerminal;
        if (rate > 0 && remaining > 0) {
          var eta = TimeSpan.FromSeconds(remaining / rate) + _options.WatchdogSafetyMargin;
          return (_clamp(eta, min, max), 0, false);
        }
      }
      // Degenerate rate (clock skew, instantaneous burst): fall through to "everything's done"
      // semantics — use the floor so the next tick double-checks completion quickly.
      return (min, 0, false);
    }

    // Branch 3: stall — no terminal events landed between ticks.
    var nextStallCount = tick.ConsecutiveStallCount + 1;
    if (nextStallCount >= _options.MaxConsecutiveStalls) {
      return (TimeSpan.Zero, nextStallCount, shouldAbandon: true);
    }
    var multiplier = Math.Pow(_options.StallBackoffMultiplier, nextStallCount);
    var stalledDelay = TimeSpan.FromTicks((long)(min.Ticks * multiplier));
    return (_clamp(stalledDelay, min, max), nextStallCount, false);
  }

  private static TimeSpan _clamp(TimeSpan value, TimeSpan min, TimeSpan max) {
    if (value < min) {
      return min;
    }
    return value > max ? max : value;
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
    var won = await _emitter.PublishOnceAsync(claimKey, evt, cancellationToken).ConfigureAwait(false);
    await _requestContinuationsAsync(ctx, finalStatus, cancellationToken).ConfigureAwait(false);
    return won;
  }

  /// <summary>
  /// Asks for each declared continuation whose trigger matches the final status.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Attempted whether or not this caller won the completion claim, and each request carries its own
  /// claim key. If only the completion winner asked, a process dying between the two publishes would
  /// strand the chain for good: the completion claim is taken, so no retry and no watchdog tick
  /// re-emits it. Letting every terminal caller ask, deduped by the continuation's own claim, removes
  /// that single point of failure and still yields one request.
  /// </para>
  /// <para>
  /// A failed request does not fail the completion. The saga did finish, the completion event is the
  /// durable record of that, and propagating a transient publish failure here would roll back the
  /// caller's transaction and re-run the terminal path. The watchdog can drive the request again; an
  /// undone completion is worse.
  /// </para>
  /// </remarks>
  private async Task _requestContinuationsAsync(
      SagaContext ctx, SagaStatus finalStatus, CancellationToken cancellationToken) {
    var continuations = SagaContinuationRegistry.For(_sagaName);
    if (continuations.Count == 0) {
      return;
    }

    foreach (var continuation in continuations) {
      if (!continuation.StartsAfter(finalStatus)) {
        continue;
      }

      var request = new SagaContinuationRequestedEvent {
        SagaName = continuation.SagaName,
        EntityId = ctx.EntityId,
        StreamId = ctx.SagaId,
        ParentSagaName = _sagaName,
        ParentSagaId = ctx.SagaId,
        ParentFinalStatus = finalStatus,
      };
      var continuationClaim =
        SagaContinuationGuard.ClaimKey(_sagaName, ctx.SagaId, continuation.SagaName);

      try {
        await _emitter.PublishOnceAsync(continuationClaim, request, cancellationToken)
          .ConfigureAwait(false);
        LogContinuationRequested(_logger, continuation.SagaName, _sagaName, ctx.SagaId, null);
      } catch (Exception ex) {
        LogContinuationRequestFailed(_logger, continuation.SagaName, _sagaName, ctx.SagaId, ex);
      }
    }
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

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Stranded-saga sweep could not check {SagaName} saga {SagaId} in tenant {TenantId}; the other sagas were still swept and this one retries next cycle")]
  private static partial void LogStrandedSagaSweepFailed(ILogger logger, string SagaName, Guid SagaId, string? TenantId, Exception exception);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Requested continuation saga {ContinuationSagaName} after {SagaName} {SagaId}")]
  private static partial void LogContinuationRequested(ILogger logger, string ContinuationSagaName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Could not request continuation saga {ContinuationSagaName} after {SagaName} {SagaId} — the saga still completed; the watchdog can drive the request again")]
  private static partial void LogContinuationRequestFailed(ILogger logger, string ContinuationSagaName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Hook {HookName} already terminal on saga {SagaName} {SagaId} — skip")]
  private static partial void LogHookSkipped(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Hook {HookName} failed on saga {SagaName} {SagaId} — publishing Failed completion before rethrow")]
  private static partial void LogHookFailed(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Hook {HookName} completed on saga {SagaName} {SagaId}")]
  private static partial void LogHookCompleted(ILogger logger, string HookName, string SagaName, Guid SagaId, Exception? exception);
}
