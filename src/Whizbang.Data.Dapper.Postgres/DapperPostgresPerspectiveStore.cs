using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Dapper/Npgsql implementation of <see cref="IPerspectiveStore{TModel}"/> for PostgreSQL.
/// Uses raw SQL with INSERT ON CONFLICT for atomic upserts.
/// Scope is set only on INSERT by default; excluded from UPDATE unless forceUpdateScope is true.
/// </summary>
/// <typeparam name="TModel">The read model type stored in the perspective</typeparam>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPostgresPerspectiveStoreTests.cs</tests>
public sealed class DapperPostgresPerspectiveStore<TModel>(
    string connectionString,
    string tableName,
    JsonSerializerOptions jsonOptions) : IPerspectiveStore<TModel>
    where TModel : class {

  /// <inheritdoc/>
  public async Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(cancellationToken);

    var sql = $"SELECT data FROM {tableName} WHERE id = @p_id";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("p_id", streamId);

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken)) {
      return null;
    }

    var json = reader.GetString(0);
    var typeInfo = jsonOptions.GetTypeInfo(typeof(TModel))
      ?? throw new InvalidOperationException($"No JsonTypeInfo found for {typeof(TModel).Name}.");
    return (TModel?)JsonSerializer.Deserialize(json, typeInfo);
  }

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, new PerspectiveScope(), false, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, false, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, forceUpdateScope, cancellationToken);

#pragma warning disable RCS1163, IDE0060 // Interface (IPerspectiveStore) signature; Dapper store accepts parameter for API parity with EF Core split-mode store
  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
    // Dapper store does not materialize physical fields; physicalFieldValues is accepted for
    // API parity with stores that do (split-mode EF Core, etc.) but is intentionally ignored.
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), false, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    // Dapper store does not materialize physical fields; physicalFieldValues is accepted for
    // API parity with stores that do (split-mode EF Core, etc.) but is intentionally ignored.
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), forceUpdateScope, cancellationToken);
#pragma warning restore RCS1163, IDE0060

  /// <inheritdoc/>
  public async Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    await GetByStreamIdAsync(_convertPartitionKeyToGuid(partitionKey), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, new PerspectiveScope(), false, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, false, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, forceUpdateScope, cancellationToken);

  /// <inheritdoc/>
  public Task FlushAsync(CancellationToken cancellationToken = default) =>
    Task.CompletedTask; // Dapper commits immediately, no pending changes

  /// <inheritdoc/>
  public async Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(cancellationToken);

    var sql = $"DELETE FROM {tableName} WHERE id = @p_id";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("p_id", streamId);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <inheritdoc/>
  public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    PurgeAsync(_convertPartitionKeyToGuid(partitionKey), cancellationToken);

  private async Task _upsertCoreAsync(
      Guid id, TModel model, PerspectiveScope scope, bool forceUpdateScope,
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(cancellationToken);

    var dataTypeInfo = jsonOptions.GetTypeInfo(typeof(TModel))
      ?? throw new InvalidOperationException($"No JsonTypeInfo found for {typeof(TModel).Name}. Ensure the type is registered in a JsonSerializerContext.");
    var scopeTypeInfo = jsonOptions.GetTypeInfo(typeof(PerspectiveScope))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveScope. Ensure the type is registered in InfrastructureJsonContext.");

    // Per-event apply hooks: resolve once, mutate the data object (SetProperty) in place before serialization,
    // and take the updated_at / version-bump decision from the plan. The default whizbang.timestamps hook yields
    // updated_at = now + a version bump — identical to the prior hardcoded stamping, now overridable.
    var now = DateTime.UtcNow;
    var hookPlan = PerEventApplyHooks.Resolve(new ApplyHookContext {
      ModelType = typeof(TModel),
      Scope = scope,
      ApplyTimestamp = new DateTimeOffset(now, TimeSpan.Zero),
    });
    if (hookPlan.ModelFieldSetters.Count > 0) {
      PerEventApplyHooks.ApplyModelSetters(model, hookPlan.ModelFieldSetters);
    }
    var updatedAt = hookPlan.UpdatedAt?.UtcDateTime ?? now;
    var versionBump = hookPlan.BumpVersion ? 1 : 0;

    var dataJson = JsonSerializer.Serialize(model, dataTypeInfo);
    var scopeJson = JsonSerializer.Serialize(scope, scopeTypeInfo);
    const string metadataJson = "{}"; // Default metadata for Dapper store

    // Build SET clause based on forceUpdateScope. updated_at + the version bump come from the hook plan.
    var setClause = forceUpdateScope
        ? $"""
            data = EXCLUDED.data,
            metadata = EXCLUDED.metadata,
            scope = EXCLUDED.scope,
            updated_at = EXCLUDED.updated_at,
            version = {tableName}.version + @p_versionbump
          """
        : $"""
            data = EXCLUDED.data,
            metadata = EXCLUDED.metadata,
            updated_at = EXCLUDED.updated_at,
            version = {tableName}.version + @p_versionbump
          """;

    var sql = $"""
      INSERT INTO {tableName} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@p_id, @p_data::jsonb, @p_metadata::jsonb, @p_scope::jsonb, @p_created, @p_updated, 1)
      ON CONFLICT (id) DO UPDATE SET
        {setClause}
      """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("p_id", id);
    cmd.Parameters.Add(new NpgsqlParameter("p_data", NpgsqlDbType.Jsonb) { Value = dataJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_metadata", NpgsqlDbType.Jsonb) { Value = metadataJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_scope", NpgsqlDbType.Jsonb) { Value = scopeJson });
    cmd.Parameters.AddWithValue("p_created", now);
    cmd.Parameters.AddWithValue("p_updated", updatedAt);
    cmd.Parameters.AddWithValue("p_versionbump", versionBump);

    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 used for deterministic GUID generation, not for cryptographic security")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S4790:Using weak hashing algorithms is security-sensitive",
    Justification = "MD5 maps trusted, application-defined partition keys (usually GUIDs already) to a stable " +
      "row GUID — an identity mapping, not a security control. Keys are never adversary-supplied text, so " +
      "crafted-collision attacks are out of scope; and the derived GUIDs are persisted row identity in existing " +
      "databases, so an algorithm change would orphan every existing row (a versioned data migration, not a " +
      "lint fix). Revisit if partition keys ever become externally controllable.")]
  private static Guid _convertPartitionKeyToGuid<TPartitionKey>(TPartitionKey partitionKey) where TPartitionKey : notnull {
    if (partitionKey is Guid guid) {
      return guid;
    }
    var bytes = MD5.HashData(Encoding.UTF8.GetBytes(partitionKey.ToString()!));
    return new Guid(bytes);
  }
}
