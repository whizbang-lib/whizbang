namespace Whizbang.Core.Perspectives;

/// <summary>
/// Write-only abstraction for perspective data storage.
/// Hides underlying database implementation (EF Core, Dapper, Marten, etc.).
/// Perspectives use this to update read models without knowing storage details.
/// </summary>
/// <typeparam name="TModel">The read model type to store</typeparam>
/// <docs>core-concepts/perspectives</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
public interface IPerspectiveStore<TModel> where TModel : class {
  /// <summary>
  /// Get a read model by stream ID.
  /// Returns null if the model doesn't exist yet.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) to retrieve model for</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The read model, or null if not found</returns>
  Task<TModel?> GetByStreamIdAsync(string streamId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Insert or update a read model.
  /// Creates new row if id doesn't exist, updates if it does.
  /// Automatically increments version for optimistic concurrency.
  /// Uses database-specific optimizations (e.g., ON CONFLICT for PostgreSQL) for single-roundtrip performance.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) to store model for</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
  Task UpsertAsync(string streamId, TModel model, CancellationToken cancellationToken = default);
}
