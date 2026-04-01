namespace Whizbang.Core.Messaging;

/// <summary>
/// Defines the 24 lifecycle stages where receptors can execute.
/// Controls timing of receptor execution relative to database operations and message processing.
/// Stages fall into pairs: Detached (fire-and-forget, runs in its own scope) and Inline (blocks per unit of work).
/// </summary>
/// <remarks>
/// <para>There are two mutually exclusive message paths:</para>
/// <list type="bullet">
/// <item><description><strong>Local Path</strong>: LocalImmediate stages (mediator pattern, no persistence)</description></item>
/// <item><description><strong>Distributed Path</strong>: PreOutbox (sender) + PostInbox (receiver) stages</description></item>
/// </list>
/// <para>Receptors without [FireAt] fire at default stages for the current path.</para>
/// <para>
/// <strong>Coordinator-managed behavior:</strong> When <see cref="Whizbang.Core.Lifecycle.ILifecycleCoordinator"/>
/// is registered, stage transitions are centralized. The coordinator guarantees:
/// </para>
/// <list type="bullet">
/// <item><description>Each stage fires exactly once per event (no duplicate firings)</description></item>
/// <item><description>Tags fire at ALL stages as lifecycle observers</description></item>
/// <item><description>ImmediateDetached fires automatically after each stage transition</description></item>
/// <item><description>PostLifecycle fires once via WhenAll pattern for multi-path events</description></item>
/// </list>
/// <para>
/// <strong>Pipeline:</strong>
/// <code>
/// Dispatcher          OutboxWorker          TransportConsumer     PerspectiveWorker
/// ───────────────    ─────────────────    ─────────────────    ─────────────────
/// ENTRY: dispatch    ENTRY: load from DB  ENTRY: receive        ENTRY: load from DB
///   LocalImmediate     PreOutbox            PreInbox              PrePerspective
///   PostLifecycle†     PostOutbox           PostInbox             PostPerspective
/// EXIT: done         EXIT: transport       PostLifecycle*        PostLifecycle**
///                                         EXIT: done            EXIT: done
///
/// † fires if local-only path
/// * fires for events WITHOUT perspectives
/// ** fires AFTER ALL perspectives complete
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/LocalImmediateLifecycleStageTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerPostLifecycleTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerPostLifecycleTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleCoordinatorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleCoordinatorSituationTests.cs</tests>
public enum LifecycleStage {
  /// <summary>
  /// Special value for tag hooks: Fire immediately after receptor completes in Dispatcher.
  /// This is the default for backward compatibility and is NOT a true lifecycle stage.
  /// </summary>
  /// <remarks>
  /// Use this stage for hooks that should fire synchronously after the receptor handles
  /// a message, before any lifecycle stages are invoked.
  /// </remarks>
  AfterReceptorCompletion = -1,

  /// <summary>
  /// Executed immediately from dispatcher channel (default).
  /// Detached: fire-and-forget, runs in its own scope. Does not block message flow.
  /// Best for: Most receptors, fire-and-forget patterns.
  /// </summary>
  ImmediateDetached,

  /// <summary>
  /// Executed during local dispatch when no transport is involved (mediator pattern).
  /// Detached: fire-and-forget, runs in its own scope. Does not block dispatch return.
  /// NO persistence - messages are processed in-memory only.
  /// Best for: In-process commands, domain events within a bounded context.
  /// </summary>
  /// <remarks>
  /// LocalImmediate stages are mutually exclusive with distributed (Outbox/Inbox) stages.
  /// When a message is dispatched locally, it fires at LocalImmediate stages.
  /// When a message is dispatched via transport, it fires at PreOutbox (sender) and PostInbox (receiver).
  /// </remarks>
  LocalImmediateDetached,

  /// <summary>
  /// Executed during local dispatch when no transport is involved (mediator pattern).
  /// Blocks dispatch until receptor completes.
  /// NO persistence - messages are processed in-memory only.
  /// Best for: In-process commands requiring synchronous handling, validation.
  /// </summary>
  /// <remarks>
  /// LocalImmediate stages are mutually exclusive with distributed (Outbox/Inbox) stages.
  /// When a message is dispatched locally, it fires at LocalImmediate stages.
  /// When a message is dispatched via transport, it fires at PreOutbox (sender) and PostInbox (receiver).
  /// </remarks>
  LocalImmediateInline,

  /// <summary>
  /// <strong>Planned.</strong> Executed before process_work_batch call.
  /// Channel is fully flushed before database call, but does not block dispatch.
  /// Best for: Pre-validation, enrichment that should complete before persistence.
  /// </summary>
  /// <remarks>
  /// This stage is planned for coordinator-managed execution. It will fire for BOTH
  /// outbox (publishing) and inbox (consuming) paths. Use <see cref="MessageSource"/>
  /// to distinguish.
  /// </remarks>
  PreDistributeDetached,

  /// <summary>
  /// <strong>Planned.</strong> Executed before process_work_batch call.
  /// Blocks the unit of work (not entire queue) until all receptors complete.
  /// Best for: Critical validation that must complete before persistence.
  /// </summary>
  PreDistributeInline,

  /// <summary>
  /// <strong>Planned.</strong> Executed in parallel with process_work_batch call.
  /// Fire-and-forget pattern - does not wait for completion.
  /// Best for: Side effects that don't need to block (notifications, caching).
  /// </summary>
  DistributeDetached,

  /// <summary>
  /// <strong>Planned.</strong> Executed after process_work_batch returns.
  /// Detached: fire-and-forget, runs in its own scope. Does not block message flow.
  /// Best for: Post-processing, analytics, notifications.
  /// </summary>
  PostDistributeDetached,

  /// <summary>
  /// <strong>Planned.</strong> Executed after process_work_batch returns.
  /// Blocks the unit of work until all receptors complete.
  /// Best for: Critical post-processing that must complete before acknowledging.
  /// </summary>
  PostDistributeInline,

  /// <summary>
  /// Executed before outbox message is published to transport.
  /// Detached: fire-and-forget, runs in its own scope. Does not block outbox worker.
  /// Best for: Message enrichment, header injection.
  /// </summary>
  PreOutboxDetached,

  /// <summary>
  /// Executed before outbox message is published to transport.
  /// Blocks outbox processing for this unit until completion.
  /// Best for: Critical enrichment, authorization checks.
  /// </summary>
  PreOutboxInline,

  /// <summary>
  /// Executed after outbox message is published to transport.
  /// Detached: fire-and-forget, runs in its own scope. Does not block outbox worker.
  /// Best for: Confirmation logging, metrics.
  /// </summary>
  PostOutboxDetached,

  /// <summary>
  /// Executed after outbox message is published to transport.
  /// Blocks outbox processing for this unit until completion.
  /// Best for: Critical confirmation, transactional cleanup.
  /// </summary>
  PostOutboxInline,

  /// <summary>
  /// Executed before inbox message is processed by local receptor.
  /// Detached: fire-and-forget, runs in its own scope. Does not block inbox worker.
  /// Best for: Message validation, deduplication checks.
  /// </summary>
  PreInboxDetached,

  /// <summary>
  /// Executed before inbox message is processed by local receptor.
  /// Blocks inbox processing for this unit until completion.
  /// Best for: Critical validation, schema migration.
  /// </summary>
  PreInboxInline,

  /// <summary>
  /// Executed after inbox message is processed by local receptor.
  /// Detached: fire-and-forget, runs in its own scope. Does not block inbox worker.
  /// Best for: Cleanup, metrics, notifications.
  /// </summary>
  PostInboxDetached,

  /// <summary>
  /// Executed after inbox message is processed by local receptor.
  /// Blocks inbox processing for this unit until completion.
  /// Best for: Critical cleanup, saga completion.
  /// </summary>
  PostInboxInline,

  /// <summary>
  /// Executed before perspective checkpoint is updated.
  /// Detached: fire-and-forget, runs in its own scope. Does not block perspective worker.
  /// Best for: Pre-checkpoint validation, metrics.
  /// </summary>
  PrePerspectiveDetached,

  /// <summary>
  /// Executed before perspective checkpoint is updated.
  /// Blocks perspective processing for this unit until completion.
  /// Best for: Critical validation before checkpoint commit.
  /// </summary>
  PrePerspectiveInline,

  /// <summary>
  /// Executed after perspective checkpoint is updated.
  /// Detached: fire-and-forget, runs in its own scope. Does not block perspective worker.
  /// Best for: Notifications, derived perspective updates.
  /// </summary>
  PostPerspectiveDetached,

  /// <summary>
  /// Executed after perspective checkpoint is updated.
  /// Blocks perspective processing for this unit until completion.
  /// Best for: Critical derived updates, cross-perspective consistency.
  /// </summary>
  PostPerspectiveInline,

  /// <summary>
  /// Executed once per event after ALL perspectives have completed processing it.
  /// Detached: fire-and-forget, runs in its own scope. Does not block perspective worker.
  /// Unlike PostPerspective (which fires per-perspective), this fires exactly once
  /// after the last perspective signals completion (WhenAll pattern).
  /// Best for: Cross-perspective aggregation, notifications that need all data committed.
  /// </summary>
  /// <remarks>
  /// Managed by <see cref="Whizbang.Core.Lifecycle.ILifecycleCoordinator"/> via WhenAll.
  /// Fires before PostLifecycle stages.
  /// </remarks>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#post-all-perspectives</docs>
  PostAllPerspectivesDetached,

  /// <summary>
  /// Executed once per event after ALL perspectives have completed processing it.
  /// Blocks perspective processing for this unit until completion.
  /// Unlike PostPerspective (which fires per-perspective), this fires exactly once
  /// after the last perspective signals completion (WhenAll pattern).
  /// Best for: Critical cross-perspective consistency, notifications requiring all perspectives committed.
  /// </summary>
  /// <remarks>
  /// Managed by <see cref="Whizbang.Core.Lifecycle.ILifecycleCoordinator"/> via WhenAll.
  /// Fires before PostLifecycle stages.
  /// </remarks>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#post-all-perspectives</docs>
  PostAllPerspectivesInline,

  /// <summary>
  /// Executed once per event after all perspectives in the batch have processed it.
  /// Detached: fire-and-forget, runs in its own scope. Does not block perspective worker.
  /// For events without perspectives, fires immediately after PostInbox.
  /// Best for: Notifications, final event processing, cross-perspective aggregation.
  /// </summary>
  /// <remarks>
  /// The <see cref="Whizbang.Core.Lifecycle.ILifecycleCoordinator"/> guarantees this stage fires
  /// exactly once per event. For Route.Both() events, PostLifecycle fires only after
  /// all processing paths complete (WhenAll pattern).
  /// </remarks>
  PostLifecycleDetached,

  /// <summary>
  /// Executed once per event after all perspectives in the batch have processed it.
  /// Blocks perspective processing for this unit until completion.
  /// For events without perspectives, fires immediately after PostInbox.
  /// Best for: Critical final processing, guaranteed-delivery notifications.
  /// </summary>
  /// <remarks>
  /// The <see cref="Whizbang.Core.Lifecycle.ILifecycleCoordinator"/> guarantees this stage fires
  /// exactly once per event. For Route.Both() events, PostLifecycle fires only after
  /// all processing paths complete (WhenAll pattern).
  /// </remarks>
  PostLifecycleInline
}

/// <summary>
/// Extension methods for <see cref="LifecycleStage"/>.
/// </summary>
public static class LifecycleStageExtensions {
  /// <summary>
  /// Returns true if this is a Detached (fire-and-forget) lifecycle stage.
  /// Detached stages run in their own scope and do not block the worker pipeline.
  /// </summary>
  public static bool IsDetached(this LifecycleStage stage) => stage is
    LifecycleStage.ImmediateDetached or
    LifecycleStage.LocalImmediateDetached or
    LifecycleStage.PreDistributeDetached or
    LifecycleStage.DistributeDetached or
    LifecycleStage.PostDistributeDetached or
    LifecycleStage.PreOutboxDetached or
    LifecycleStage.PostOutboxDetached or
    LifecycleStage.PreInboxDetached or
    LifecycleStage.PostInboxDetached or
    LifecycleStage.PrePerspectiveDetached or
    LifecycleStage.PostPerspectiveDetached or
    LifecycleStage.PostAllPerspectivesDetached or
    LifecycleStage.PostLifecycleDetached;

  /// <summary>
  /// Returns true if this is an Inline (pipeline-blocking) lifecycle stage.
  /// Inline stages block the worker until all receptors complete.
  /// </summary>
  public static bool IsInline(this LifecycleStage stage) => stage is
    LifecycleStage.LocalImmediateInline or
    LifecycleStage.PreDistributeInline or
    LifecycleStage.PostDistributeInline or
    LifecycleStage.PreOutboxInline or
    LifecycleStage.PostOutboxInline or
    LifecycleStage.PreInboxInline or
    LifecycleStage.PostInboxInline or
    LifecycleStage.PrePerspectiveInline or
    LifecycleStage.PostPerspectiveInline or
    LifecycleStage.PostAllPerspectivesInline or
    LifecycleStage.PostLifecycleInline;
}
