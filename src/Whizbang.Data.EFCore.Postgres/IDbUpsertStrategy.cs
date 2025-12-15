using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Abstraction for database-specific upsert operations on perspective rows.
/// Each database provider (PostgreSQL, SQL Server, MySQL, etc.) implements its own optimal strategy.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
public interface IDbUpsertStrategy {

  /// <summary>
  /// Performs an atomic upsert (insert or update) of a perspective row.
  /// </summary>
  /// <typeparam name="TModel">The model type stored in the perspective</typeparam>
  /// <param name="context">The EF Core DbContext</param>
  /// <param name="tableName">The table name for the perspective rows</param>
  /// <param name="id">The unique identifier for the perspective row</param>
  /// <param name="model">The model data to store</param>
  /// <param name="metadata">Metadata about the event that created/updated this row</param>
  /// <param name="scope">Multi-tenancy and security scope information</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task representing the asynchronous operation</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
  Task UpsertPerspectiveRowAsync<TModel>(
      DbContext context,
      string tableName,
      string id,
      TModel model,
      PerspectiveMetadata metadata,
      PerspectiveScope scope,
      CancellationToken cancellationToken = default)
      where TModel : class;
}
