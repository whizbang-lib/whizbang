namespace Whizbang.Core.Messaging;

/// <summary>
/// Interface for checking whether the database is ready for work coordinator operations.
/// Implementations can check connectivity, schema availability, or other readiness criteria.
/// </summary>
/// <remarks>
/// This interface is used by the WorkCoordinatorPublisherWorker to determine if
/// ProcessWorkBatchAsync should be called. When the database is not ready, work processing
/// is skipped and messages remain buffered in memory until the database becomes available.
///
/// Examples of readiness checks:
/// - PostgreSQL: Check if connection is available and required tables exist
/// - SQL Server: Check if database is accessible and schema is initialized
/// - Cassandra: Check if keyspace exists and is reachable
/// - MongoDB: Check if connection is established and collections exist
/// </remarks>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithRunningDatabaseAndSchema_ReturnsTrueAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithInvalidConnectionString_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithMissingTables_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_MultipleCalls_ReturnsConsistentResultAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_ChecksAllRequiredTables_VerifiesInboxOutboxEventStoreAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ProcessWorkBatchAsync_SkippedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseReady_ProcessWorkBatchAsync_CalledAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ConsecutiveChecks_TracksCountAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_ExceedsThreshold_LogsWarningAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseBecomesReady_ResetsConsecutiveCounterAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:DatabaseNotReady_MessagesBuffered_UntilReadyAsync</tests>
public interface IDatabaseReadinessCheck {
  /// <summary>
  /// Checks if the database is ready for work coordinator operations.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token to cancel the readiness check.</param>
  /// <returns>True if the database is ready, false otherwise.</returns>
  /// <remarks>
  /// This method should be fast and lightweight. If the check requires network I/O,
  /// consider implementing caching or circuit breaker patterns to avoid excessive overhead.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithRunningDatabaseAndSchema_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithInvalidConnectionString_ReturnsFalseAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithMissingTables_ReturnsFalseAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_MultipleCalls_ReturnsConsistentResultAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_WithCancellation_ThrowsOperationCanceledExceptionAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_ChecksAllRequiredTables_VerifiesInboxOutboxEventStoreAsync</tests>
  Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
