using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

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
    _upsertCoreAsync(streamId, model, new PerspectiveScope(), false, null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, false, null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, forceUpdateScope, null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), false, physicalFieldValues, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), forceUpdateScope, physicalFieldValues, cancellationToken);

  /// <inheritdoc/>
  public async Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    await GetByStreamIdAsync(_convertPartitionKeyToGuid(partitionKey), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, new PerspectiveScope(), false, null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, false, null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, forceUpdateScope, null, cancellationToken);

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
      IDictionary<string, object?>? physicalFieldValues,
      CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(cancellationToken);

    var dataTypeInfo = jsonOptions.GetTypeInfo(typeof(TModel))
      ?? throw new InvalidOperationException($"No JsonTypeInfo found for {typeof(TModel).Name}. Ensure the type is registered in a JsonSerializerContext.");
    var scopeTypeInfo = jsonOptions.GetTypeInfo(typeof(PerspectiveScope))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveScope. Ensure the type is registered in InfrastructureJsonContext.");

    var dataJson = JsonSerializer.Serialize(model, dataTypeInfo);
    var scopeJson = JsonSerializer.Serialize(scope, scopeTypeInfo);
    var metadataJson = "{}"; // Default metadata for Dapper store
    var now = DateTime.UtcNow;

    // Build SET clause based on forceUpdateScope
    var setClause = forceUpdateScope
        ? $"""
            data = EXCLUDED.data,
            metadata = EXCLUDED.metadata,
            scope = EXCLUDED.scope,
            updated_at = EXCLUDED.updated_at,
            version = {tableName}.version + 1
          """
        : $"""
            data = EXCLUDED.data,
            metadata = EXCLUDED.metadata,
            updated_at = EXCLUDED.updated_at,
            version = {tableName}.version + 1
          """;

    var sql = $"""
      INSERT INTO {tableName} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@p_id, @p_data::jsonb, @p_metadata::jsonb, @p_scope::jsonb, @p_now, @p_now, 1)
      ON CONFLICT (id) DO UPDATE SET
        {setClause}
      """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("p_id", id);
    cmd.Parameters.Add(new NpgsqlParameter("p_data", NpgsqlDbType.Jsonb) { Value = dataJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_metadata", NpgsqlDbType.Jsonb) { Value = metadataJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_scope", NpgsqlDbType.Jsonb) { Value = scopeJson });
    cmd.Parameters.AddWithValue("p_now", now);

    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 used for deterministic GUID generation, not for cryptographic security")]
  private static Guid _convertPartitionKeyToGuid<TPartitionKey>(TPartitionKey partitionKey) where TPartitionKey : notnull {
    if (partitionKey is Guid guid) {
      return guid;
    }
    var bytes = MD5.HashData(Encoding.UTF8.GetBytes(partitionKey.ToString()!));
    return new Guid(bytes);
  }
}
