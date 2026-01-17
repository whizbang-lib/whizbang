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
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByStreamIdAsync_WhenRecordExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByStreamIdAsync_WhenRecordDoesNotExist_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByStreamIdAsync_WithStrongTypedId_ReturnsModelAsync</tests>
  public async Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
    // Query the perspective table by Id
    var row = await _context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId, cancellationToken);

    // Return the model data, or null if not found
    return row?.Data;
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_UpdatesUpdatedAtTimestamp_OnUpdateAsync</tests>
  public async Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) {
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
        streamId,
        model,
        metadata,
        scope,
        cancellationToken);
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WhenRecordExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WhenRecordDoesNotExist_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WithStringPartitionKey_ReturnsModelAsync</tests>
  public async Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(
      TPartitionKey partitionKey,
      CancellationToken cancellationToken = default)
      where TPartitionKey : notnull {

    // Convert partition key to Guid for storage
    // Supports Guid, string, int, etc. via conversion
    var partitionGuid = _convertPartitionKeyToGuid(partitionKey);

    // Query the perspective table by Id (which stores the partition key)
    var row = await _context.Set<PerspectiveRow<TModel>>()
        .FirstOrDefaultAsync(r => r.Id == partitionGuid, cancellationToken);

    // Return the model data, or null if not found
    return row?.Data;
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  public async Task UpsertByPartitionKeyAsync<TPartitionKey>(
      TPartitionKey partitionKey,
      TModel model,
      CancellationToken cancellationToken = default)
      where TPartitionKey : notnull {

    // Convert partition key to Guid for storage
    var partitionGuid = _convertPartitionKeyToGuid(partitionKey);

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
        partitionGuid,
        model,
        metadata,
        scope,
        cancellationToken);
  }

  /// <summary>
  /// Converts a partition key of any type to a Guid for storage.
  /// Supports Guid (identity), string (deterministic hash), int, etc.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 used for deterministic GUID generation, not for cryptographic security")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S4790:Using weak hashing algorithms is security-sensitive", Justification = "MD5 used for deterministic partition key hashing, not for cryptographic security. Partition keys are user-controlled identifiers, not secrets.")]
  private static Guid _convertPartitionKeyToGuid<TPartitionKey>(TPartitionKey partitionKey)
      where TPartitionKey : notnull {

    // If already a Guid, return as-is
    if (partitionKey is Guid guid) {
      return guid;
    }

    // If string, create deterministic Guid from hash
    if (partitionKey is string str) {
      // Use MD5 for deterministic Guid generation (not cryptographic)
      var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(str));
      return new Guid(hash);
    }

    // For other types (int, long, etc.), convert to string then to Guid
    var stringValue = partitionKey.ToString()!;
    var hashOther = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(stringValue));
    return new Guid(hashOther);
  }

  /// <inheritdoc/>
  public async Task FlushAsync(CancellationToken cancellationToken = default) {
    // For EF Core, ensure all tracked changes are committed to the database
    // This is critical for PostPerspectiveInline lifecycle stage which guarantees
    // data is persisted and queryable before receptors fire
    await _context.SaveChangesAsync(cancellationToken);
  }
}
