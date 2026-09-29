using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Hooks;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;

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
  /// <remarks>
  /// Implemented rather than left to the interface default, which reports
  /// <see cref="PerspectiveApplyRead.Unchecked"/> and reads nothing. A store that reports unchecked is
  /// telling the runner not to condition its write on anything, and a per-stream apply computed from a
  /// stale read could then overwrite a concurrent collective write here while the same apply was
  /// refused under Entity Framework.
  /// </remarks>
  public async Task<PerspectiveApplyRead> ReadForApplyAsync(
      Guid streamId, CancellationToken cancellationToken = default) {
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(PerspectiveRowVersionCommands.ReadForApplySql(tableName), conn);
    cmd.Parameters.AddWithValue("id", streamId);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    return await PerspectiveRowVersionCommands.ReadApplyAsync(reader, cancellationToken);
  }

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, new PerspectiveScope(), false, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, false, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertAsync(Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, forceUpdateScope, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken);

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
    _upsertCoreAsync(streamId, model, scope, forceUpdateScope, metadata, PerspectiveRowVersion.Unchecked, cancellationToken);

  /// <inheritdoc/>
  /// <remarks>
  /// The checked write. Implemented rather than left to the interface default, whose body drops the
  /// version and writes unconditionally -- which is the overwrite this exists to refuse.
  /// </remarks>
  public Task UpsertAsync(
      Guid streamId, TModel model, PerspectiveScope scope, bool forceUpdateScope,
      PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion,
      CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope, forceUpdateScope, metadata, expectedVersion, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), false, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken, physicalFieldValues);

  /// <inheritdoc/>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), forceUpdateScope, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken, physicalFieldValues);

  /// <inheritdoc/>
  /// <remarks>Implemented explicitly for the same reason as the metadata overload without physical fields:
  /// the interface default drops <paramref name="metadata"/>.</remarks>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, PerspectiveMetadata metadata, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), forceUpdateScope, metadata,
      PerspectiveRowVersion.Unchecked, cancellationToken, physicalFieldValues);

  /// <inheritdoc/>
  /// <remarks>The checked write's physical-fields twin: the same guard, with the physical columns in the same statement.</remarks>
  public Task UpsertWithPhysicalFieldsAsync(
      Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues,
      PerspectiveScope? scope, bool forceUpdateScope, PerspectiveMetadata metadata,
      PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) =>
    _upsertCoreAsync(streamId, model, scope ?? new PerspectiveScope(), forceUpdateScope, metadata,
      expectedVersion, cancellationToken, physicalFieldValues);

  /// <inheritdoc/>
  public async Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    await GetByStreamIdAsync(_convertPartitionKeyToGuid(partitionKey), cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, new PerspectiveScope(), false, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, false, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken);

  /// <inheritdoc/>
  public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, PerspectiveScope scope, bool forceUpdateScope, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull =>
    _upsertCoreAsync(_convertPartitionKeyToGuid(partitionKey), model, scope, forceUpdateScope, metadata: null, PerspectiveRowVersion.Unchecked, cancellationToken);

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

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "The single implementation behind the perspective-store interface's upsert overload matrix: the row identity and model, the scope decision, the incoming metadata, the expected version and the physical columns. Every overload supplies some subset, so the parameters already are the write; a request type would exist only to be unpacked on the first line.")]
  private async Task _upsertCoreAsync(
      Guid id, TModel model, PerspectiveScope scope, bool forceUpdateScope,
      PerspectiveMetadata? metadata,
      PerspectiveRowVersion expectedVersion,
      CancellationToken cancellationToken,
      IDictionary<string, object?>? physicalFieldValues = null) {
    // Validated before anything is opened: a column name goes into the statement text.
    var physical = _physicalColumns(physicalFieldValues, jsonOptions);
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

    // The ordering guard, so an event the row has already moved past is skipped rather than applied
    // backwards. Written against EXCLUDED.metadata in the upsert forms and against the bound
    // parameter in the conditional update, the only place the incoming metadata differs.
    var isVersionedTarget = typeof(IVersionedApplyTarget).IsAssignableFrom(typeof(TModel));
    string guard(string incoming) =>
      PerspectiveRowVersionCommands.OrderingGuard(tableName, incoming, isVersionedTarget);

    // A checked write lands only on the version the apply read. An existing row is updated in place
    // on that exact version -- an UPDATE, not an upsert, so a row deleted meanwhile is not brought
    // back. A row that was absent is inserted only while it is still absent. Either affects no row
    // when the row moved, which the refusal below reads as a conflict. The unchecked form is the
    // statement this store always issued, so the common path still costs one statement.
    var physicalUpdate = string.Concat(physical.Select(pf => $", {pf.Column} = @{pf.Parameter.ParameterName}"));
    var insert = $"""
      INSERT INTO {tableName} (id, data, metadata, scope, created_at, updated_at, version{physicalColumns})
      VALUES (@p_id, @p_data::jsonb, @p_metadata::jsonb, @p_scope::jsonb, @p_created, @p_updated, 1{physicalValues})
      """;
    var sql = expectedVersion.State switch {
      PerspectiveRowVersionState.Present => $"""
        UPDATE {tableName} SET
          data = @p_data::jsonb,
          metadata = @p_metadata::jsonb,
          updated_at = @p_updated,
          version = {tableName}.version + @p_versionbump{(forceUpdateScope ? ", scope = @p_scope::jsonb" : "")}{physicalUpdate}
        WHERE {tableName}.id = @p_id AND {tableName}.xmin = @p_expectedversion
          AND ({guard("@p_metadata::jsonb")})
        """,
      // The separator is explicit: a raw string literal keeps no trailing newline, so concatenating
      // onto `insert` without one would run ON CONFLICT straight into the VALUES list.
      PerspectiveRowVersionState.Absent =>
        insert + "\n      ON CONFLICT (id) DO NOTHING",
      _ => insert + "\n" + $"""
        ON CONFLICT (id) DO UPDATE SET
          {setClause}{physicalSet}
        WHERE {guard("EXCLUDED.metadata")}
        """,
    };

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

    if (expectedVersion.State == PerspectiveRowVersionState.Present) {
      cmd.Parameters.Add(PerspectiveRowVersionCommands.ExpectedVersionParameter("p_expectedversion", expectedVersion));
    }

    var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
    if (affected == 0 && expectedVersion.IsChecked) {
      await _explainRefusedWriteAsync(conn, id, expectedVersion, cancellationToken);
    }
  }

  /// <summary>
  /// A checked write affected no row. Either the row moved since the apply read it, which is a
  /// conflict and nothing was written, or it is still at the expected version and the ordering guard
  /// refused the write, which is the quiet skip it always was.
  /// </summary>
  /// <remarks>
  /// Runs only after a refused write, never on the common path, so a checked write that lands still
  /// costs the one statement it always did.
  /// </remarks>
  private async Task _explainRefusedWriteAsync(
      NpgsqlConnection conn, Guid streamId, PerspectiveRowVersion expected, CancellationToken cancellationToken) {
    await using var cmd = new NpgsqlCommand(PerspectiveRowVersionCommands.ReadVersionSql(tableName, lockRow: false), conn);
    cmd.Parameters.AddWithValue("id", streamId);
    var actual = await cmd.ExecuteScalarAsync(cancellationToken) is uint xmin
      ? PerspectiveRowVersion.Of(xmin)
      : PerspectiveRowVersion.Absent;
    if (expected.State == PerspectiveRowVersionState.Present && actual == expected) {
      return;
    }
    throw new PerspectiveRowConflictException(typeof(TModel), streamId, expected, actual);
  }

  /// <summary>
  /// One parameter per physical column. A column name must be a plain identifier, since it is written into the
  /// statement unquoted, exactly as the table's DDL declares it.
  /// </summary>
  private static List<(string Column, NpgsqlParameter Parameter)> _physicalColumns(
      IDictionary<string, object?>? values, JsonSerializerOptions jsonOptions) {
    var columns = new List<(string, NpgsqlParameter)>();
    foreach (var (column, value) in values ?? new Dictionary<string, object?>()) {
      if (!_isPlainIdentifier(column)) {
        throw new ArgumentException($"'{column}' is not a plain column name.", nameof(values));
      }
      columns.Add((column, _physicalParameter($"p_pf{columns.Count}", value, jsonOptions)));
    }
    return columns;
  }

  private static bool _isPlainIdentifier(string name) =>
    name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

  /// <summary>
  /// The driver sends the common types natively. An instant is sent in UTC, which is the same instant. An
  /// enumeration is sent as its underlying number, the form its column holds (see
  /// <see cref="PerspectivePhysicalValues"/>). A collection the JSON options can describe (a keyed list in a jsonb
  /// column) is sent as its JSON text. Anything else (a vector) is sent as its text form. Both text forms go
  /// with no declared type, so the column's own type parses them, exactly as it would parse a literal.
  /// </summary>
  private static NpgsqlParameter _physicalParameter(string name, object? value, JsonSerializerOptions jsonOptions) => value switch {
    null => new NpgsqlParameter(name, DBNull.Value),
    DateTimeOffset instant => new NpgsqlParameter(name, instant.ToUniversalTime()),
    Enum => new NpgsqlParameter(name, PerspectivePhysicalValues.ToColumnScalar(value)),
    string or Guid or bool or short or int or long or float or double or decimal
      or DateTime or DateOnly or TimeOnly or TimeSpan or Array => new NpgsqlParameter(name, value),
    System.Collections.IEnumerable when jsonOptions.TryGetTypeInfo(value.GetType(), out var typeInfo) =>
      new NpgsqlParameter(name, NpgsqlDbType.Unknown) { Value = JsonSerializer.Serialize(value, typeInfo) },
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
