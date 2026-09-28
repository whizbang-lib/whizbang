using Whizbang.Core.Lenses;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Write-only abstraction for perspective data storage.
/// Hides underlying database implementation (EF Core, Dapper, Marten, etc.).
/// Perspectives use this to update read models without knowing storage details.
/// </summary>
/// <typeparam name="TModel">The read model type to store</typeparam>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
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
  Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Get the metadata of the perspective row for a stream ID.
  /// Returns null when the row does not exist.
  /// </summary>
  /// <remarks>
  /// Used by generated runners to detect which events have already been applied
  /// (via <see cref="PerspectiveMetadata.EventId"/>) so a re-run after a worker
  /// crash between row upsert and cursor advance does not double-apply events.
  /// Default implementation returns null so test fakes that bypass metadata
  /// remain compatible — the runner falls back to "apply all events".
  /// </remarks>
  /// <param name="streamId">Stream ID (aggregate ID)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The metadata of the existing row, or null if no row exists</returns>
  Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default)
    => Task.FromResult<PerspectiveMetadata?>(null);

  /// <summary>
  /// Read what a per-stream apply needs before it loads the model: the row's version and the metadata the
  /// runner's idempotency filter reads, in one statement.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The generated runner calls this first, then loads the model (unless the row is absent), folds the
  /// batch's events onto it and writes it back through the <see cref="UpsertAsync(Guid, TModel, PerspectiveScope, bool, PerspectiveMetadata, PerspectiveRowVersion, CancellationToken)"/>
  /// overload with the version this returned. Reading the version before the model means the model is never
  /// older than the version: a writer that commits between the two reads moves the version, so the write is
  /// refused and retried rather than landing on top of it.
  /// </para>
  /// <para>
  /// The default reports <see cref="PerspectiveApplyRead.Unchecked"/> without reading anything, and the runner
  /// then reads the model and <see cref="GetMetadataByStreamIdAsync"/> exactly as it always did.
  /// </para>
  /// </remarks>
  /// <param name="streamId">Stream ID (aggregate ID)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The row's version and metadata, or <see cref="PerspectiveApplyRead.Unchecked"/> when the store does not track versions.</returns>
  /// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowVersionTests.cs:ReadForApplyAsync_Default_IsUncheckedAndDoesNoReadAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionIntegrationTests.cs:ReadForApplyAsync_ReportsAbsence_ThenTheRowsVersionAndMetadata_AndSeesACollectiveAsync</tests>
  Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default)
    => Task.FromResult(PerspectiveApplyRead.Unchecked);

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
  Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default);

  /// <summary>
  /// Insert or update a read model with scope information.
  /// Same as UpsertAsync but populates the scope column for query filtering.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) to store model for</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="scope">Multi-tenancy and security scope to store</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default)
    => UpsertAsync(streamId, model, cancellationToken);

  /// <summary>
  /// Insert or update a read model with scope information and optional forced scope update.
  /// When <paramref name="forceUpdateScope"/> is true, the scope column is updated even on existing rows.
  /// Used by the generated perspective runner when processing <see cref="IScopeEvent"/> events.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) to store model for</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="scope">Multi-tenancy and security scope to store</param>
  /// <param name="forceUpdateScope">When true, scope is written on UPDATE (for IScopeEvent). Default: false (scope set only on INSERT).</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default)
    => UpsertAsync(streamId, model, scope, cancellationToken);

  /// <summary>
  /// Upsert a read model, persisting <paramref name="metadata"/> alongside it.
  /// </summary>
  /// <remarks>
  /// Generated perspective runners call this overload so the row's
  /// <see cref="PerspectiveMetadata.EventId"/> records the last applied event.
  /// On a re-run, the runner reads that id via <see cref="GetMetadataByStreamIdAsync"/>
  /// and skips events with id ≤ the persisted value, making projection runs
  /// idempotent across worker crashes between row upsert and cursor advance.
  /// Default implementation drops <paramref name="metadata"/> so test fakes keep working.
  /// </remarks>
  /// <param name="streamId">Stream ID (aggregate ID)</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="scope">Multi-tenancy and security scope</param>
  /// <param name="forceUpdateScope">When true, scope is written on UPDATE (for IScopeEvent).</param>
  /// <param name="metadata">Metadata of the last applied event (EventId, EventType, Timestamp, etc.)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertAsync(
      Guid streamId,
      TModel model,
      PerspectiveScope scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      CancellationToken cancellationToken = default)
    => UpsertAsync(streamId, model, scope, forceUpdateScope, cancellationToken);

  /// <summary>
  /// Upsert a read model with <paramref name="metadata"/>, but only onto the row version the apply read.
  /// </summary>
  /// <remarks>
  /// A store that tracks row versions refuses the write with <see cref="PerspectiveRowConflictException"/>
  /// when the row is no longer at <paramref name="expectedVersion"/> (it changed, appeared or was deleted since
  /// <see cref="ReadForApplyAsync"/> read it), and writes nothing. A write whose version is current but whose
  /// metadata the store's own ordering guards refuse is skipped quietly, as it always was.
  /// <see cref="PerspectiveRowVersion.Unchecked"/> writes unconditionally. The default ignores the version.
  /// </remarks>
  /// <param name="streamId">Stream ID (aggregate ID)</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="scope">Multi-tenancy and security scope</param>
  /// <param name="forceUpdateScope">When true, scope is written on UPDATE (for IScopeEvent).</param>
  /// <param name="metadata">Metadata of the last applied event</param>
  /// <param name="expectedVersion">The version <see cref="ReadForApplyAsync"/> returned for this apply.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowVersionTests.cs:UpsertAsync_WithExpectedVersion_Default_ForwardsToTheMetadataOverloadAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionIntegrationTests.cs:Upsert_WithAVersionReadBeforeACollective_IsRefused_AndTheCollectiveSurvivesAsync</tests>
  [global::System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Public store surface: the versioned twin of the metadata overload, which already carries six parameters; each is a distinct write concern and a parameter object would break every store implementation.")]
  Task UpsertAsync(
      Guid streamId,
      TModel model,
      PerspectiveScope scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      PerspectiveRowVersion expectedVersion,
      CancellationToken cancellationToken = default)
    => UpsertAsync(streamId, model, scope, forceUpdateScope, metadata, cancellationToken);

  /// <summary>
  /// Insert or update a read model with physical field values.
  /// Creates new row if id doesn't exist, updates if it does.
  /// Physical field values are applied to shadow properties or split columns.
  /// Used for [PhysicalField] and [VectorField] properties that are stored outside JSONB.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) to store model for</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="physicalFieldValues">Dictionary mapping column names to values for physical fields</param>
  /// <param name="scope">The perspective scope (tenant/user context) extracted from event hops</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Insert or update a read model with physical field values and optional forced scope update.
  /// When <paramref name="forceUpdateScope"/> is true, the scope column is updated even on existing rows.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) to store model for</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="physicalFieldValues">Dictionary mapping column names to values for physical fields</param>
  /// <param name="scope">The perspective scope (tenant/user context) extracted from event hops</param>
  /// <param name="forceUpdateScope">When true, scope is written on UPDATE (for IScopeEvent). Default: false.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope,
      bool forceUpdateScope,
      CancellationToken cancellationToken = default)
    => UpsertWithPhysicalFieldsAsync(streamId, model, physicalFieldValues, scope, cancellationToken);

  /// <summary>
  /// Upsert a read model with physical field values, persisting <paramref name="metadata"/> alongside it.
  /// Same idempotency contract as the non-physical overload.
  /// Default implementation drops <paramref name="metadata"/> so test fakes keep working.
  /// </summary>
  Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      CancellationToken cancellationToken = default)
    => UpsertWithPhysicalFieldsAsync(streamId, model, physicalFieldValues, scope, forceUpdateScope, cancellationToken);

  /// <summary>
  /// Upsert a read model with physical field values and <paramref name="metadata"/>, but only onto the row
  /// version the apply read. Same contract as the versioned non-physical overload. The default ignores the version.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID)</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="physicalFieldValues">Dictionary mapping column names to values for physical fields</param>
  /// <param name="scope">The perspective scope (tenant/user context) extracted from event hops</param>
  /// <param name="forceUpdateScope">When true, scope is written on UPDATE (for IScopeEvent).</param>
  /// <param name="metadata">Metadata of the last applied event</param>
  /// <param name="expectedVersion">The version <see cref="ReadForApplyAsync"/> returned for this apply.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRowVersionTests.cs:UpsertWithPhysicalFieldsAsync_WithExpectedVersion_Default_ForwardsToTheMetadataOverloadAsync</tests>
  [global::System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Public store surface: the versioned twin of the physical-fields metadata overload; each parameter is a distinct write concern and a parameter object would break every store implementation.")]
  Task UpsertWithPhysicalFieldsAsync(
      Guid streamId,
      TModel model,
      IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope,
      bool forceUpdateScope,
      PerspectiveMetadata metadata,
      PerspectiveRowVersion expectedVersion,
      CancellationToken cancellationToken = default)
    => UpsertWithPhysicalFieldsAsync(streamId, model, physicalFieldValues, scope, forceUpdateScope, metadata, cancellationToken);

  /// <summary>
  /// Get a read model by partition key (for multi-stream/global perspectives).
  /// Returns null if the model doesn't exist yet.
  /// </summary>
  /// <param name="partitionKey">Partition key to retrieve model for</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The read model, or null if not found</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WhenRecordExists_ReturnsModelAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WhenRecordDoesNotExist_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:GetByPartitionKeyAsync_WithStringPartitionKey_ReturnsModelAsync</tests>
  Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull;

  /// <summary>
  /// Insert or update a read model by partition key (for multi-stream/global perspectives).
  /// Creates new row if partition key doesn't exist, updates if it does.
  /// Automatically increments version for optimistic concurrency.
  /// Uses database-specific optimizations (e.g., ON CONFLICT for PostgreSQL) for single-roundtrip performance.
  /// </summary>
  /// <param name="partitionKey">Partition key to store model for</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_WhenRecordDoesNotExist_CreatesNewRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_WhenRecordExists_UpdatesExistingRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:UpsertByPartitionKeyAsync_IncrementsVersionNumber_OnEachUpdateAsync</tests>
  Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull;

  /// <summary>
  /// Insert or update a read model by partition key with scope information.
  /// Same as UpsertByPartitionKeyAsync but populates the scope column for query filtering.
  /// </summary>
  /// <param name="partitionKey">Partition key to store model for</param>
  /// <param name="model">The read model data to store</param>
  /// <param name="scope">Multi-tenancy and security scope to store</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull
    => UpsertByPartitionKeyAsync(partitionKey, model, cancellationToken);

  /// <summary>
  /// Insert or update a read model by partition key with scope information and optional forced scope update.
  /// When <paramref name="forceUpdateScope"/> is true, the scope column is updated even on existing rows.
  /// </summary>
  Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull
    => UpsertByPartitionKeyAsync(partitionKey, model, scope, cancellationToken);

  /// <summary>
  /// Ensures all pending changes are committed to the database.
  /// This is critical for PostPerspectiveInline lifecycle stage, which guarantees
  /// that perspective data is persisted and queryable before receptors fire.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// For EF Core implementations, this calls SaveChangesAsync() to commit the transaction.
  /// For other implementations (Dapper, raw SQL), this may be a no-op if changes are already committed.
  /// </remarks>
  Task FlushAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Hard deletes (purges) a model by removing it from the store entirely.
  /// This is a permanent deletion - the row is physically removed from the database.
  /// For soft delete, use UpsertAsync with a model that has DeletedAt set.
  /// </summary>
  /// <param name="streamId">Stream ID (aggregate ID) of the model to purge</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// This method is idempotent - purging a non-existent model does not throw.
  /// Use this for ModelAction.Purge scenarios where data must be permanently removed.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeAsync_WhenRecordExists_RemovesRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeAsync_WhenRecordDoesNotExist_DoesNotThrowAsync</tests>
  Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Hard deletes (purges) a model by partition key, removing it from the store entirely.
  /// This is a permanent deletion - the row is physically removed from the database.
  /// For soft delete, use UpsertByPartitionKeyAsync with a model that has DeletedAt set.
  /// </summary>
  /// <param name="partitionKey">Partition key of the model to purge</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <remarks>
  /// This method is idempotent - purging a non-existent model does not throw.
  /// Use this for ModelAction.Purge scenarios in global perspectives.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeByPartitionKeyAsync_WhenRecordExists_RemovesRecordAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeByPartitionKeyAsync_WhenRecordDoesNotExist_DoesNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCorePostgresPerspectiveStoreTests.cs:PurgeByPartitionKeyAsync_WithStringPartitionKey_RemovesRecordAsync</tests>
  Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
    where TPartitionKey : notnull;
}
