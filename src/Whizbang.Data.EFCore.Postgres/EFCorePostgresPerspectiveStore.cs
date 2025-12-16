using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
/// EF Core implementation of <see cref="IPerspectiveStore{TModel}"/> for PostgreSQL.
/// Provides write operations for perspective data with automatic versioning and timestamp management.
/// Uses database-specific upsert strategies for optimal single-roundtrip performance.
/// </summary>
/// <typeparam name="TModel">The model type stored in the perspective</typeparam>
public class EFCorePostgresPerspectiveStore<TModel> : IPerspectiveStore<TModel>
    where TModel : class {

  private readonly DbContext _context;
  private readonly string _tableName;
  private readonly IDbUpsertStrategy _upsertStrategy;

  /// <summary>
  /// Initializes a new instance of <see cref="EFCorePostgresPerspectiveStore{TModel}"/>.
  /// </summary>
  /// <param name="context">The EF Core DbContext</param>
  /// <param name="tableName">The table name for this perspective (for SQL generation)</param>
  /// <param name="upsertStrategy">The database-specific upsert strategy (optional, defaults to PostgresUpsertStrategy)</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
  public EFCorePostgresPerspectiveStore(
      DbContext context,
      string tableName,
      IDbUpsertStrategy? upsertStrategy = null) {

    _context = context ?? throw new ArgumentNullException(nameof(context));
    _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));

    // Default to PostgresUpsertStrategy for production, allow override for testing
    _upsertStrategy = upsertStrategy ?? new PostgresUpsertStrategy();
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
  public async Task UpsertAsync(Guid id, TModel model, CancellationToken cancellationToken = default) {
    // Use default metadata for generic upserts
    var metadata = new PerspectiveMetadata {
      EventType = "Unknown",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };

    var scope = new PerspectiveScope();

    // Delegate to strategy for optimal database-specific implementation
    await _upsertStrategy.UpsertPerspectiveRowAsync(
        _context,
        _tableName,
        id,
        model,
        metadata,
        scope,
        cancellationToken);
  }
}
