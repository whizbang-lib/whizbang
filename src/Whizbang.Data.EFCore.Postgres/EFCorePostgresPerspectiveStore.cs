using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
/// EF Core implementation of <see cref="IPerspectiveStore{TModel}"/> for PostgreSQL.
/// Provides write operations for perspective data with automatic versioning and timestamp management.
/// Uses database-specific upsert strategies for optimal single-roundtrip performance.
/// </summary>
/// <typeparam name="TModel">The model type stored in the perspective</typeparam>
/// <remarks>
/// Initializes a new instance of <see cref="EFCorePostgresPerspectiveStore{TModel}"/>.
/// </remarks>
/// <param name="context">The EF Core DbContext</param>
/// <param name="tableName">The table name for this perspective (for SQL generation)</param>
/// <param name="upsertStrategy">The database-specific upsert strategy (optional, defaults to PostgresUpsertStrategy)</param>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync</tests>
public class EFCorePostgresPerspectiveStore<TModel>(
    DbContext context,
    string tableName,
    IDbUpsertStrategy? upsertStrategy = null) : IPerspectiveStore<TModel>
    where TModel : class {

  private readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));
  private readonly string _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
  private readonly IDbUpsertStrategy _upsertStrategy = upsertStrategy ?? new PostgresUpsertStrategy();

  private static PerspectiveMetadata _defaultMetadata => new() {
    EventType = "Unknown",
    EventId = Guid.NewGuid().ToString(),
    Timestamp = DateTime.UtcNow
  };

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByStreamIdAsync_WhenRecordExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByStreamIdAsync_WhenRecordDoesNotExist_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByStreamIdAsync_WithStrongTypedId_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPhysicalFieldReloadTests.cs:GetByStreamIdAsync_ReturnsThePromotedFieldsFromTheirColumnsAsync</tests>
  public Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    _loadAsync(streamId, cancellationToken);

  /// <summary>
  /// Reads the row from the shadow table a blue-green rebuild redirected this flow to
  /// (<see cref="PerspectiveTableRedirect"/>): its document, its metadata and, for a Split model, its promoted
  /// columns. With SQL, because EF Core cannot map a JSON complex property over a raw query; the document is read
  /// with the persistence options the atomic upsert wrote it with.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "The only text is a quoted table identifier from the EF model and the rebuild's own shadow-table name, and plain column identifiers the generator registered; the id is a parameter.")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("csharpsquid", "S2077:Formatting SQL queries is security-sensitive",
    Justification = "The only text is a quoted table identifier from the EF model and the rebuild's own shadow-table name, and plain column identifiers the generator registered; the id is a parameter.")]
  private async Task<(TModel? Model, PerspectiveMetadata? Metadata)> _readRedirectedAsync(
      string shadow, Guid id, CancellationToken cancellationToken) {
    SplitPhysicalFieldRegistry.TryGet<TModel>(out var split);
    var columns = split?.Columns ?? [];
    var physical = string.Concat(columns.Select(c => ", " + (c.IsVector ? c.Name + "::real[]" : c.Name)));
    var connection = (Npgsql.NpgsqlConnection)_context.Database.GetDbConnection();
    var opened = connection.State != System.Data.ConnectionState.Open;
    if (opened) {
      await connection.OpenAsync(cancellationToken);
    }
    try {
      await using var cmd = connection.CreateCommand();
      // The promoted columns follow the document, where the shared column reader expects them; metadata comes last.
      cmd.CommandText = "SELECT data::text" + physical + ", metadata::text FROM " + shadow + " WHERE id = @id";
      cmd.Transaction = (Npgsql.NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();
      cmd.Parameters.AddWithValue("id", id);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      if (!await reader.ReadAsync(cancellationToken)) {
        return (null, null);
      }
      var options = Perspectives.PerspectiveDocumentSerialization.Options;
      var model = (TModel?)System.Text.Json.JsonSerializer.Deserialize(reader.GetString(0), options.GetTypeInfo(typeof(TModel)));
      var metadata = (PerspectiveMetadata?)System.Text.Json.JsonSerializer.Deserialize(
        reader.GetString(1 + columns.Count), options.GetTypeInfo(typeof(PerspectiveMetadata)));
      if (model is not null && split is not null) {
        model = split.Hydrate(model, new Whizbang.Data.Postgres.Perspectives.NpgsqlPhysicalColumnReader(reader, columns));
      }
      return (model, metadata);
    } finally {
      if (opened) {
        await connection.CloseAsync();
      }
    }
  }

  /// <summary>
  /// Deletes the row from the shadow table when a blue-green rebuild redirected this flow, and says whether it did;
  /// the mapped table is never touched by a rebuild.
  /// </summary>
  private async Task<bool> _purgeRedirectedAsync(Guid id, CancellationToken cancellationToken) {
    if (!PerspectiveTableRedirect.IsActive || PerspectiveRowVersionSql.RedirectedTable<TModel>(_context) is not { } shadow) {
      return false;
    }
    var sql = "DELETE FROM " + shadow + " WHERE id = @id";
    await _context.Database.ExecuteSqlRawAsync(sql, [new Npgsql.NpgsqlParameter("id", id)], cancellationToken);
    return true;
  }

  /// <summary>
  /// Reads the model stored under <paramref name="id"/>, with its promoted fields when it is stored Split.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A Split field lives only in its column, which EF Core maps as a shadow property: a no-tracking read
  /// discards it, and the model the next event is applied to would carry its default, which the write that
  /// follows would store over the column (issue #977). A model with a generated
  /// <see cref="SplitPhysicalFieldMap{TModel}"/> is therefore read tracked, its columns are copied from the
  /// entry into the model, and the row is detached so nothing stays tracked for the write.
  /// </para>
  /// <para>
  /// A row the context already holds unchanged is let go first, so the read materializes it afresh rather
  /// than returning the held instance, whose document may be the one a write stripped. A row held with a
  /// pending change is returned as it is and stays tracked for its write. A row that comes back detached was
  /// hydrated as it was tracked, by the lens hydrator a shared context can carry, and is returned as it is:
  /// its columns are no longer on an entry to read.
  /// </para>
  /// <para>
  /// Both reads go through <see cref="_readRowAsync"/>, so a document EF Core cannot materialize is reported
  /// with its path either way (issue #985).
  /// </para>
  /// </remarks>
  private async Task<TModel?> _loadAsync(Guid id, CancellationToken cancellationToken) {
    if (PerspectiveTableRedirect.IsActive && PerspectiveRowVersionSql.RedirectedTable<TModel>(_context) is { } shadow) {
      return (await _readRedirectedAsync(shadow, id, cancellationToken)).Model;
    }
    if (!SplitPhysicalFieldRegistry.TryGet<TModel>(out var split)) {
      var document = await _readRowAsync(id, tracked: false, cancellationToken);
      return PerspectiveDataCoalescer.CoalescedData(document); // WORKAROUND(dotnet/efcore#38625)
    }

    var held = _context.Set<PerspectiveRow<TModel>>().Local.FirstOrDefault(r => r.Id == id);
    if (held is not null && _context.Entry(held).State == EntityState.Unchanged) {
      _context.Entry(held).State = EntityState.Detached;
    }

    var row = await _readRowAsync(id, tracked: true, cancellationToken);
    // Unchanged is a row this read materialized. A held row with a pending change came back as it is, and
    // stays tracked for its write.
    var entry = row is null ? null : _context.Entry(row);
    if (entry?.State == EntityState.Unchanged) {
      row!.Data = split.Hydrate(row.Data, new EntityEntryPhysicalColumnReader(entry));
      entry.State = EntityState.Detached;
    }

    return PerspectiveDataCoalescer.CoalescedData(row); // WORKAROUND(dotnet/efcore#38625)
  }

  /// <summary>
  /// Reads one row, explaining a failed read of its stored documents as the refusal it is.
  /// </summary>
  /// <remarks>
  /// Entity Framework's materializer raises a bare reader error, with no path, for a stored value of
  /// the wrong JSON type. <see cref="Perspectives.MappedDocumentReadFailure"/> finds the value and
  /// raises it as a <see cref="System.Text.Json.JsonException"/>, which is what lets the worker
  /// classify the stream as holding a stored document no reader takes, announce it once with the path,
  /// and park it. Any other failure is rethrown as it was raised. A Split model is read
  /// <paramref name="tracked"/>, so its promoted columns are on the entry to copy from.
  /// </remarks>
  private async Task<PerspectiveRow<TModel>?> _readRowAsync(Guid id, bool tracked, CancellationToken cancellationToken) {
    PerspectiveRow<TModel>? row = null;
    Exception? failure = null;
    try {
      var rows = _context.Set<PerspectiveRow<TModel>>();
      row = await (tracked ? rows : rows.AsNoTracking())
          .OrderBy(r => r.Id)
          .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    } catch (Exception raised) when (Perspectives.MappedDocumentReadFailure.MayBeDocumentRead(raised)) {
      // Explained after the catch rather than inside it: an await in a catch that also rethrows makes the
      // compiler move the handler out of the catch region, and the rethrow's sequence point lands where
      // nothing reaches it.
      failure = raised;
    }
    var explained = failure is null
        ? null
        : await Perspectives.MappedDocumentReadFailure.ExplainAsync<TModel>(_context, id, failure, cancellationToken);
    // Awaiting a faulted task rethrows through ExceptionDispatchInfo, so a failure the document does not
    // explain keeps the stack it was raised with.
    await (failure is null ? Task.CompletedTask : Task.FromException(explained ?? failure));
    return row;
  }

  /// <inheritdoc/>
  public async Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
    if (PerspectiveTableRedirect.IsActive && PerspectiveRowVersionSql.RedirectedTable<TModel>(_context) is { } shadow) {
      return (await _readRedirectedAsync(shadow, streamId, cancellationToken)).Metadata;
    }
    var row = await _context.Set<PerspectiveRow<TModel>>()
        .AsNoTracking()
        .OrderBy(r => r.Id)
        .FirstOrDefaultAsync(r => r.Id == streamId, cancellationToken);

    return row?.Metadata;
  }

  /// <inheritdoc/>
  /// <remarks>
  /// On PostgreSQL the version is the row's <c>xmin</c>, read in one statement with the metadata the runner's
  /// idempotency filter needs (so the runner no longer reads the row a second time for it). Any other provider
  /// reports <see cref="PerspectiveApplyRead.Unchecked"/> and reads nothing.
  /// </remarks>
  /// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionIntegrationTests.cs:ReadForApplyAsync_ReportsAbsence_ThenTheRowsVersionAndMetadata_AndSeesACollectiveAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionIntegrationTests.cs:Upsert_WithACheckedVersion_OnANonPostgresProvider_IsNotCheckedAsync</tests>
  public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    PerspectiveRowVersionSql.Supports(_context)
      ? PerspectiveRowVersionSql.ReadForApplyAsync(
          _context, PerspectiveRowVersionSql.QualifiedTable<TModel>(_context), streamId, cancellationToken)
      : Task.FromResult(PerspectiveApplyRead.Unchecked);

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) =>
    _upsertStrategy.UpsertPerspectiveRowAsync(
        _context, _tableName, streamId, model, _defaultMetadata, new PerspectiveScope(), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default) =>
    _upsertStrategy.UpsertPerspectiveRowAsync(
        _context, _tableName, streamId, model, _defaultMetadata, scope, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertStrategy.UpsertPerspectiveRowAsync(
        _context, _tableName, streamId, model, _defaultMetadata, scope, forceUpdateScope, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(
      Guid streamId,
      TModel model,
      PerspectiveScope scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      CancellationToken cancellationToken = default) =>
    UpsertAsync(streamId, model, scope, forceUpdateScope, metadata, PerspectiveRowVersion.Unchecked, cancellationToken);

  /// <inheritdoc/>
  /// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionIntegrationTests.cs:Upsert_WithAVersionReadBeforeACollective_IsRefused_AndTheCollectiveSurvivesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionIntegrationTests.cs:CollectiveCommittedBetweenReadAndWrite_IsNotOverwritten_FinalRowReflectsBothAsync</tests>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Implements the versioned IPerspectiveStore overload, whose shape is fixed by the interface.")]
  public async Task UpsertAsync(
      Guid streamId,
      TModel model,
      PerspectiveScope scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      PerspectiveRowVersion expectedVersion,
      CancellationToken cancellationToken = default) {
    try {
      await _upsertStrategy.UpsertPerspectiveRowAsync(
          _context, _tableName, streamId, model, metadata, scope, forceUpdateScope, expectedVersion, cancellationToken);
    } catch (Exception failure) {
      // Every write funnels through here, so this is the one place a refusal can be explained in
      // terms of the model rather than of a SQL state. Anything unrecognized (a row-version conflict
      // included) is rethrown as it stands: see PerspectiveWriteErrors for why only a value with no
      // representation at all is worth reshaping.
      var translated = PerspectiveWriteErrors.Translate<TModel>(failure);
      if (ReferenceEquals(translated, failure)) {
        throw;
      }

      throw translated;
    }
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WhenRecordExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WhenRecordDoesNotExist_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WithStringPartitionKey_ReturnsModelAsync</tests>
  public Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(
      TPartitionKey partitionKey,
      CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    // Convert partition key to Guid for storage (supports Guid, string, int, etc.); the row's Id stores it.
    _loadAsync(_convertPartitionKeyToGuid(partitionKey), cancellationToken);

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(
      TPartitionKey partitionKey,
      TModel model,
      CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertStrategy.UpsertPerspectiveRowAsync(
        _context, _tableName, _convertPartitionKeyToGuid(partitionKey), model,
        _defaultMetadata, new PerspectiveScope(), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(
      TPartitionKey partitionKey,
      TModel model,
      PerspectiveScope scope,
      CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertStrategy.UpsertPerspectiveRowAsync(
        _context, _tableName, _convertPartitionKeyToGuid(partitionKey), model,
        _defaultMetadata, scope, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(
      TPartitionKey partitionKey,
      TModel model,
      PerspectiveScope scope,
      bool forceUpdateScope,
      CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertStrategy.UpsertPerspectiveRowAsync(
        _context, _tableName, _convertPartitionKeyToGuid(partitionKey), model,
        _defaultMetadata, scope, forceUpdateScope, cancellationToken);

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

  /// <summary>
  /// Insert or update a read model with physical field values.
  /// Physical fields are stored in shadow properties configured by the EF Core model.
  /// Creates new row if id doesn't exist, updates if it does.
  /// Automatically increments version for optimistic concurrency.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) to store model for</param>
  /// <param name="model">The read model data to store (full model or filtered for Split mode)</param>
  /// <param name="physicalFieldValues">Dictionary of column name to value for physical fields</param>
  /// <param name="scope">The perspective scope (tenant/user context) from event hops</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_WhenRecordDoesNotExist_CreatesShadowPropertiesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PhysicalFieldUpsertStrategyTests.cs:UpsertWithPhysicalFields_WhenRecordExists_UpdatesShadowPropertiesAsync</tests>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null,
      CancellationToken cancellationToken = default) =>
    _upsertStrategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context, _tableName, streamId, model, _defaultMetadata, scope ?? new PerspectiveScope(),
        physicalFieldValues, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope,
      bool forceUpdateScope,
      CancellationToken cancellationToken = default) =>
    _upsertStrategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context, _tableName, streamId, model, _defaultMetadata, scope ?? new PerspectiveScope(),
        physicalFieldValues, forceUpdateScope, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      CancellationToken cancellationToken = default) =>
    _upsertStrategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context, _tableName, streamId, model, metadata, scope ?? new PerspectiveScope(),
        physicalFieldValues, forceUpdateScope, cancellationToken);

  /// <inheritdoc/>
  /// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyCoverageTests.cs:UpsertWithPhysicalFieldsAsync_OnTheVersionItRead_WritesTheColumns_AndARowThatMovedIsRefusedAsync</tests>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Implements the versioned IPerspectiveStore overload, whose shape is fixed by the interface.")]
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      PerspectiveRowVersion expectedVersion,
      CancellationToken cancellationToken = default) =>
    _upsertStrategy.UpsertPerspectiveRowWithPhysicalFieldsAsync(
        _context, _tableName, streamId, model, metadata, scope ?? new PerspectiveScope(),
        physicalFieldValues, forceUpdateScope, expectedVersion, cancellationToken);

  /// <inheritdoc/>
  public async Task FlushAsync(CancellationToken cancellationToken = default) {
    // For EF Core, ensure all tracked changes are committed to the database
    // This is critical for PostPerspectiveInline lifecycle stage which guarantees
    // data is persisted and queryable before receptors fire
    await _context.SaveChangesAsync(cancellationToken);
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeAsync_WhenRecordExists_RemovesRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeAsync_WhenRecordDoesNotExist_DoesNotThrowAsync</tests>
  public async Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) {
    if (await _purgeRedirectedAsync(streamId, cancellationToken)) {
      return;
    }
    // Use ExecuteDeleteAsync to bypass change tracker JSON serialization,
    // which fails on entities with complex collections in deleted state (EF Core bug with Npgsql JSON columns)
    if (_context.Database.IsRelational()) {
      await _context.Set<PerspectiveRow<TModel>>()
          .Where(r => r.Id == streamId)
          .ExecuteDeleteAsync(cancellationToken);
    } else {
      var row = await _context.Set<PerspectiveRow<TModel>>()
          .OrderBy(r => r.Id)
          .FirstOrDefaultAsync(r => r.Id == streamId, cancellationToken);
      if (row != null) {
        _context.Set<PerspectiveRow<TModel>>().Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
      }
    }
  }

  /// <inheritdoc/>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeByPartitionKeyAsync_WhenRecordExists_RemovesRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeByPartitionKeyAsync_WhenRecordDoesNotExist_DoesNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeByPartitionKeyAsync_WithStringPartitionKey_RemovesRecordAsync</tests>
  public async Task PurgeByPartitionKeyAsync<TPartitionKey>(
      TPartitionKey partitionKey,
      CancellationToken cancellationToken = default)
      where TPartitionKey : notnull {

    // Convert partition key to Guid for storage
    var partitionGuid = _convertPartitionKeyToGuid(partitionKey);

    if (await _purgeRedirectedAsync(_convertPartitionKeyToGuid(partitionKey), cancellationToken)) {
      return;
    }
    // Use ExecuteDeleteAsync to bypass change tracker JSON serialization,
    // which fails on entities with complex collections in deleted state (EF Core bug with Npgsql JSON columns)
    if (_context.Database.IsRelational()) {
      await _context.Set<PerspectiveRow<TModel>>()
          .Where(r => r.Id == partitionGuid)
          .ExecuteDeleteAsync(cancellationToken);
    } else {
      var row = await _context.Set<PerspectiveRow<TModel>>()
          .OrderBy(r => r.Id)
          .FirstOrDefaultAsync(r => r.Id == partitionGuid, cancellationToken);
      if (row != null) {
        _context.Set<PerspectiveRow<TModel>>().Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
      }
    }
  }
}
