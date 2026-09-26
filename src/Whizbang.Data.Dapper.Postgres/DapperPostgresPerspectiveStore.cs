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
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperPostgresPerspectiveStoreTests.cs</tests>
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
    _upsertCoreAsync(streamId, model, new PerspectiveScope(), false, metadata: null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, false, metadata: null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, forceUpdateScope, metadata: null, cancellationToken);

  /// <inheritdoc/>
  /// <remarks>
  /// Implemented EXPLICITLY rather than left to the interface default. The default body drops
  /// <paramref name="metadata"/> and delegates to the metadata-less overload, so a store that
  /// does not override it silently writes an empty metadata object on every row — losing the
  /// event type, id, correlation, causation, commit sequence, and the event timestamp that
  /// business time is derived from. Default interface methods are not virtual dispatch; the
  /// omission produces no compiler error and no runtime failure.
  /// </remarks>
  public Task UpsertAsync(
      Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope,
      PerspectiveMetadata metadata, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, forceUpdateScope, metadata, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), false, metadata: null, cancellationToken, physicalFieldValues);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), forceUpdateScope, metadata: null, cancellationToken, physicalFieldValues);

  /// <inheritdoc/>
  /// <remarks>Implemented explicitly for the same reason as the metadata overload without physical fields:
  /// the interface default drops <paramref name="metadata"/>.</remarks>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, PerspectiveMetadata metadata, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), forceUpdateScope, metadata, cancellationToken, physicalFieldValues);

  /// <inheritdoc/>
  public async Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    await GetByStreamIdAsync(_convertPartitionKeyToGuid(partitionKey), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, new PerspectiveScope(), false, metadata: null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, false, metadata: null, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, forceUpdateScope, metadata: null, cancellationToken);

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
      PerspectiveMetadata? metadata,
      CancellationToken cancellationToken,
      IDictionary<string, object?>? physicalFieldValues = null) {
    // Validated before anything is opened: a column name goes into the statement text.
    var physical = _physicalColumns(physicalFieldValues);
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
    // Persist the applied event's metadata when the caller supplied it. Callers on the
    // metadata-less overloads (direct upserts, purges by partition key) legitimately have none,
    // and those rows keep an empty object as before.
    var metadataJson = "{}";
    if (metadata is not null) {
      var metadataTypeInfo = jsonOptions.GetTypeInfo(typeof(PerspectiveMetadata))
        ?? throw new InvalidOperationException(
          "No JsonTypeInfo found for PerspectiveMetadata. Ensure InfrastructureJsonContext is in the resolver chain.");
      metadataJson = JsonSerializer.Serialize(metadata, metadataTypeInfo);
    }

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

    // Physical columns are written on insert and rewritten on update, like the document they copy.
    var physicalColumns = string.Concat(physical.Select(p => $", {p.Column}"));
    var physicalValues = string.Concat(physical.Select(p => $", @{p.Parameter.ParameterName}"));
    var physicalSet = string.Concat(physical.Select(p => $",\n        {p.Column} = EXCLUDED.{p.Column}"));

    var sql = $"""
      INSERT INTO {tableName} (id, data, metadata, scope, created_at, updated_at, version{physicalColumns})
      VALUES (@p_id, @p_data::jsonb, @p_metadata::jsonb, @p_scope::jsonb, @p_created, @p_updated, 1{physicalValues})
      ON CONFLICT (id) DO UPDATE SET
        {setClause}{physicalSet}
      """;

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("p_id", id);
    cmd.Parameters.Add(new NpgsqlParameter("p_data", NpgsqlDbType.Jsonb) { Value = dataJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_metadata", NpgsqlDbType.Jsonb) { Value = metadataJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_scope", NpgsqlDbType.Jsonb) { Value = scopeJson });
    cmd.Parameters.AddWithValue("p_created", now);
    cmd.Parameters.AddWithValue("p_updated", updatedAt);
    cmd.Parameters.AddWithValue("p_versionbump", versionBump);
    foreach (var (_, parameter) in physical) {
      cmd.Parameters.Add(parameter);
    }

    await cmd.ExecuteNonQueryAsync(cancellationToken);
  }

  /// <summary>
  /// One parameter per physical column. A column name must be a plain identifier, since it is written into the
  /// statement unquoted, exactly as the table's DDL declares it.
  /// </summary>
  private static List<(string Column, NpgsqlParameter Parameter)> _physicalColumns(IDictionary<string, object?>? values) {
    var columns = new List<(string, NpgsqlParameter)>();
    foreach (var (column, value) in values ?? new Dictionary<string, object?>()) {
      if (!_isPlainIdentifier(column)) {
        throw new ArgumentException($"'{column}' is not a plain column name.", nameof(values));
      }
      columns.Add((column, _physicalParameter($"p_pf{columns.Count}", value)));
    }
    return columns;
  }

  private static bool _isPlainIdentifier(string name) =>
    name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

  /// <summary>
  /// The driver sends the common types natively. An instant is sent in UTC, which is the same instant. Anything
  /// else (a vector, an enum) is sent as its text form with no declared type, so the column's own type parses
  /// it, exactly as it would parse a literal.
  /// </summary>
  private static NpgsqlParameter _physicalParameter(string name, object? value) => value switch {
    null => new NpgsqlParameter(name, DBNull.Value),
    DateTimeOffset instant => new NpgsqlParameter(name, instant.ToUniversalTime()),
    string or Guid or bool or short or int or long or float or double or decimal
      or DateTime or DateOnly or TimeOnly or TimeSpan or Array => new NpgsqlParameter(name, value),
    _ => new NpgsqlParameter(name, NpgsqlDbType.Unknown) { Value = value.ToString() },
  };

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
