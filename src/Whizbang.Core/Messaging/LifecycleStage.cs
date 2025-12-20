namespace Whizbang.Core.Messaging;

/// <summary>
/// Defines the 18 lifecycle stages where receptors can execute.
/// Controls timing of receptor execution relative to database operations and message processing.
/// Stages fall into pairs: Async (non-blocking) and Inline (blocks per unit of work).
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IUnitOfWorkStrategyContractTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedUnitOfWorkStrategyTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalUnitOfWorkStrategyTests.cs</tests>
public enum LifecycleStage {
  /// <summary>
  /// Executed immediately from dispatcher channel (default).
  /// Async processing, does not block message flow.
  /// Best for: Most receptors, fire-and-forget patterns.
  /// </summary>
  ImmediateAsync,

  /// <summary>
  /// Executed before process_work_batch call.
  /// Channel is fully flushed before database call, but does not block dispatch.
  /// Best for: Pre-validation, enrichment that should complete before persistence.
  /// </summary>
  PreDistributeAsync,

  /// <summary>
  /// Executed before process_work_batch call.
  /// Blocks the unit of work (not entire queue) until all receptors complete.
  /// Best for: Critical validation that must complete before persistence.
  /// </summary>
  PreDistributeInline,

  /// <summary>
  /// Executed in parallel with process_work_batch call.
  /// Fire-and-forget pattern - does not wait for completion.
  /// Best for: Side effects that don't need to block (notifications, caching).
  /// </summary>
  DistributeAsync,

  /// <summary>
  /// Executed after process_work_batch returns.
  /// Async processing, does not block message flow.
  /// Best for: Post-processing, analytics, notifications.
  /// </summary>
  PostDistributeAsync,

  /// <summary>
  /// Executed after process_work_batch returns.
  /// Blocks the unit of work until all receptors complete.
  /// Best for: Critical post-processing that must complete before acknowledging.
  /// </summary>
  PostDistributeInline,

  /// <summary>
  /// Executed before outbox message is published to transport.
  /// Async processing, does not block outbox worker.
  /// Best for: Message enrichment, header injection.
  /// </summary>
  PreOutboxAsync,

  /// <summary>
  /// Executed before outbox message is published to transport.
  /// Blocks outbox processing for this unit until completion.
  /// Best for: Critical enrichment, authorization checks.
  /// </summary>
  PreOutboxInline,

  /// <summary>
  /// Executed after outbox message is published to transport.
  /// Async processing, does not block outbox worker.
  /// Best for: Confirmation logging, metrics.
  /// </summary>
  PostOutboxAsync,

  /// <summary>
  /// Executed after outbox message is published to transport.
  /// Blocks outbox processing for this unit until completion.
  /// Best for: Critical confirmation, transactional cleanup.
  /// </summary>
  PostOutboxInline,

  /// <summary>
  /// Executed before inbox message is processed by local receptor.
  /// Async processing, does not block inbox worker.
  /// Best for: Message validation, deduplication checks.
  /// </summary>
  PreInboxAsync,

  /// <summary>
  /// Executed before inbox message is processed by local receptor.
  /// Blocks inbox processing for this unit until completion.
  /// Best for: Critical validation, schema migration.
  /// </summary>
  PreInboxInline,

  /// <summary>
  /// Executed after inbox message is processed by local receptor.
  /// Async processing, does not block inbox worker.
  /// Best for: Cleanup, metrics, notifications.
  /// </summary>
  PostInboxAsync,

  /// <summary>
  /// Executed after inbox message is processed by local receptor.
  /// Blocks inbox processing for this unit until completion.
  /// Best for: Critical cleanup, saga completion.
  /// </summary>
  PostInboxInline,

  /// <summary>
  /// Executed before perspective checkpoint is updated.
  /// Async processing, does not block perspective worker.
  /// Best for: Pre-checkpoint validation, metrics.
  /// </summary>
  PrePerspectiveAsync,

  /// <summary>
  /// Executed before perspective checkpoint is updated.
  /// Blocks perspective processing for this unit until completion.
  /// Best for: Critical validation before checkpoint commit.
  /// </summary>
  PrePerspectiveInline,

  /// <summary>
  /// Executed after perspective checkpoint is updated.
  /// Async processing, does not block perspective worker.
  /// Best for: Notifications, derived perspective updates.
  /// </summary>
  PostPerspectiveAsync,

  /// <summary>
  /// Executed after perspective checkpoint is updated.
  /// Blocks perspective processing for this unit until completion.
  /// Best for: Critical derived updates, cross-perspective consistency.
  /// </summary>
  PostPerspectiveInline
}
