namespace Whizbang.Core;

/// <summary>
/// Provides globally unique identifiers for WhizbangId types.
/// Implement this interface to customize ID generation strategy (e.g., UUIDv7, sequential, testing).
/// </summary>
/// <docs>core-concepts/message-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:ImmediateWorkCoordinatorStrategy_EnqueueOutboxMessage_FlushesImmediatelyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntervalWorkCoordinatorStrategyTests.cs:IntervalWorkCoordinatorStrategy_EnqueueOutboxMessage_FlushesAfterIntervalAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyTests.cs:ScopedWorkCoordinatorStrategy_EnqueueOutboxMessage_FlushesOnScopeDisposeAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_DeletesSuccessfulMessagesAsync</tests>
public interface IWhizbangIdProvider {
  /// <summary>
  /// Generates a new globally unique identifier.
  /// Default implementation uses UUIDv7 for time-ordered, database-friendly IDs.
  /// </summary>
  /// <returns>A new Guid value.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ImmediateWorkCoordinatorStrategyTests.cs:ImmediateWorkCoordinatorStrategy_EnqueueOutboxMessage_FlushesImmediatelyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_DeletesSuccessfulMessagesAsync</tests>
  Guid NewGuid();
}
