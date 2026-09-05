using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IWorkCoordinator for lease-based work coordination.
/// Uses the PostgreSQL process_work_batch function for atomic operations.
/// </summary>
/// <typeparam name="TDbContext">DbContext type containing outbox, inbox, and service instance tables</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Work coordinator diagnostic logging - I/O bound database operations where LoggerMessage overhead isn't justified")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1845:Use span-based 'string.Concat'", Justification = "Debug logging with substring truncation - span-based operations not worth complexity for diagnostic output")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Schema name comes from EF Core model configuration (Model.FindEntityType().GetSchema()), not user input. Schema-qualified function names are required for multi-tenant PostgreSQL databases.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType intentionally supports `Array | Uuid`, `Array | Integer`, etc. per the Npgsql API design — the Array bit is combined with the element type. The enum is not marked [Flags] upstream but the API expects bitwise composition.")]
public class EFCoreWorkCoordinator<TDbContext>(
  TDbContext dbContext,
  JsonSerializerOptions jsonOptions,
  ILogger<EFCoreWorkCoordinator<TDbContext>>? logger = null,
  WorkCoordinatorMetrics? metrics = null,
  WorkCoordinatorGate? gate = null,
  IServiceInstanceProvider? instanceProvider = null,
  Microsoft.Extensions.Options.IOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>? coreOptions = null
) : IWorkCoordinator
  where TDbContext : DbContext {
  private const string DEFAULT_SCHEMA = "public";
  private const string PERSPECTIVE_CURSORS_TABLE = "wh_perspective_cursors";
  private const string PARAM_INSTANCE_ID = "p_instance_id";

  // Slice 5 of zero-idle-polling — opportunistic heartbeat update inside
  // CompleteOutboxPublishedAsync skips when the freshness guard says the row
  // was UPDATEd within this many seconds. Same value as the SQL-side guard
  // in migration 010 (register_instance_heartbeat) so the two paths agree
  // on what "fresh" means.
  private const int OPPORTUNISTIC_HEARTBEAT_FRESHNESS_SECONDS = 10;

  private readonly TDbContext _dbContext = _initDbContext(dbContext);
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<EFCoreWorkCoordinator<TDbContext>>? _logger = logger;
  private readonly WorkCoordinatorMetrics? _metrics = metrics;
  private readonly WorkCoordinatorGate? _gate = gate;
  private readonly IServiceInstanceProvider? _instanceProvider = instanceProvider;
  // v0.657 slice 2: enforce EmptyStreamIdPolicy at StoreOutboxMessagesAsync /
  // StoreInboxMessagesAsync time. When the options aren't wired into DI,
  // default to the same Reject value as WhizbangCoreOptions itself.
  private readonly Whizbang.Core.Configuration.EmptyStreamIdPolicy _emptyStreamIdPolicy =
    coreOptions?.Value.EmptyStreamIdPolicy ?? Whizbang.Core.Configuration.EmptyStreamIdPolicy.Reject;

  private static TDbContext _initDbContext(TDbContext ctx) {
    ArgumentNullException.ThrowIfNull(ctx);
    ctx.Database.SetCommandTimeout(TimeSpan.FromMinutes(3));
    return ctx;
  }

  /// <summary>
  /// Gets the schema from the provided value, falling back to the default if empty/null.
  /// Logs a warning when falling back to the default schema.
  /// </summary>
  /// <param name="schema">The schema value to check.</param>
  /// <param name="defaultSchema">The default schema to use as fallback.</param>
  /// <param name="logger">Optional logger for warning messages.</param>
  /// <returns>The schema if valid, or the default schema.</returns>
  internal static string GetSchemaWithFallback(
    string? schema,
    string defaultSchema,
    ILogger<EFCoreWorkCoordinator<TDbContext>>? logger) {
    if (string.IsNullOrWhiteSpace(schema)) {
      logger?.LogWarning(
        "Schema not found or empty for OutboxRecord entity type, falling back to default schema '{DefaultSchema}'",
        defaultSchema);
      return defaultSchema;
    }

    return schema;
  }

  /// <summary>
  /// Builds a schema-qualified identifier for SQL. Handles empty/public schema correctly.
  /// NEVER produces a leading dot - uses unqualified name for public schema.
  /// </summary>
  /// <param name="schema">The schema name (should come from GetSchemaWithFallback).</param>
  /// <param name="identifier">The function or table name.</param>
  /// <returns>Schema-qualified identifier like "\"myschema\".function_name" or just "function_name" for public.</returns>
#pragma warning disable RCS1158 // Static helper intentionally shared across all generic instantiations — does not depend on TDbContext.
  internal static string BuildSchemaQualifiedName(string schema, string identifier) {
    // CRITICAL: Never produce a leading dot
    if (string.IsNullOrWhiteSpace(schema) || schema == DEFAULT_SCHEMA) {
      return identifier;
    }
    // Quote schema name to handle PostgreSQL reserved words
    return $"\"{schema}\".{identifier}";
  }
#pragma warning restore RCS1158

  private string _serializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) { return "[]"; }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageFailure[]. Ensure the type is registered.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  private string _serializeNewOutboxMessages(OutboxMessage[] messages) {
    if (messages.Length == 0) { return "[]"; }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(OutboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for OutboxMessage[]. Ensure the type is registered.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  private string _serializeNewInboxMessages(InboxMessage[] messages) {
    if (messages.Length == 0) { return "[]"; }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for InboxMessage[]. Ensure the type is registered.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  private string _serializePerspectiveCompletions(PerspectiveCursorCompletion[] completions) {
    if (completions.Length == 0) { return "[]"; }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCursorCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCursorCompletion[]. Ensure the type is registered.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <summary>
  /// Reports perspective cursor completion directly (out-of-band).
  /// Calls complete_perspective_cursor_work SQL function directly without full work batch processing.
  /// Creates its own database connection to allow calling after the scoped DbContext is disposed.
  /// </summary>
  /// <inheritdoc />
  public async Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "deregister_instance");

#pragma warning disable S2077
    var sql = $"SELECT {functionName}({{0}})";
#pragma warning restore S2077

    await _dbContext.Database.ExecuteSqlRawAsync(sql, [instanceId], cancellationToken);
  }

  /// <inheritdoc />
  public async ValueTask<OutstandingWork?> CountOutstandingWorkAsync(
      Guid instanceId, CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "count_outstanding_work");

    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText = $"SELECT inbox_rows, outbox_rows, perspective_rows FROM {functionName}(@instanceId)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("instanceId", instanceId);

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken)) {
      // No row means the function answered nothing, which is not the same as "holds nothing".
      // Null keeps the budget disengaged rather than licensing a full-size claim off a
      // measurement that was never taken.
      return null;
    }
    return new OutstandingWork {
      InboxRows = reader.GetInt64(0),
      OutboxRows = reader.GetInt64(1),
      PerspectiveRows = reader.GetInt64(2)
    };
  }

  /// <inheritdoc />
  /// <summary>
  /// Publishes the host's debug-retention option into <c>wh_settings</c>, where the maintenance
  /// sweep reads it.
  /// </summary>
  /// <remarks>
  /// The sweep already guards its purge on <c>debug_mode</c>, but nothing wrote that row — so the
  /// documented option marked rows in process and the sweep deleted them anyway on its next pass.
  /// Both values are written: leaving a stale true would disable the purge permanently.
  /// </remarks>
  /// <param name="debugMode">Whether completed rows should be retained.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the setting is stored.</returns>
  public async Task SyncDebugRetentionSettingAsync(
      bool debugMode, CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var settings = BuildSchemaQualifiedName(schema, "wh_settings");

    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    // value_type is NOT NULL and updated_at records when the host last published its option, so a
    // stale value is visible rather than indistinguishable from a fresh one.
    cmd.CommandText = $@"
      INSERT INTO {settings} (setting_key, setting_value, value_type, description, updated_at, updated_by)
      VALUES (@k, @v, 'boolean', 'Retain completed messages for debugging; published from WorkCoordinatorOptions.DebugMode', NOW(), 'whizbang')
      ON CONFLICT (setting_key) DO UPDATE
        SET setting_value = EXCLUDED.setting_value,
            value_type    = EXCLUDED.value_type,
            updated_at    = NOW(),
            updated_by    = EXCLUDED.updated_by";
#pragma warning restore S2077
    var pk = cmd.CreateParameter(); pk.ParameterName = "k";
    pk.Value = Whizbang.Core.Workers.DebugRetentionBridge.SettingKey;
    cmd.Parameters.Add(pk);
    var pv = cmd.CreateParameter(); pv.ParameterName = "v";
    pv.Value = Whizbang.Core.Workers.DebugRetentionBridge.SettingValueFor(debugMode);
    cmd.Parameters.Add(pv);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  public async ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var inbox = BuildSchemaQualifiedName(schema, "wh_inbox");

    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = scope.Connection;
    await using var cmd = conn.CreateCommand();

    // BOUNDED counts, deliberately. The caller needs to know whether these are zero, not their
    // exact size, and this runs on the checkpoint cadence against a table that can hold a million
    // rows during a bulk operation. An unbounded count(*) there would make a gate meant to protect
    // the store into a periodic full scan of it. The cap is high enough that the logged number is
    // still useful ("at least N") and low enough to stay cheap.
#pragma warning disable S2077
    // The third column is the lag measure: age of the oldest unprocessed row. ORDER BY received_at
    // LIMIT 1 walks idx_inbox_received_at and stops at the first unprocessed row; completed rows are
    // deleted on the normal path, so the walk stays short even when the queue is large.
    // Settledness must use the CLAIM's eligibility predicate: rows parked with a future
    // scheduled_for (operator quarantine, tag-bound coalescing) are deliberately not claimable,
    // and counting them reported an idle service as busy forever — housekeeping deferred on
    // ServiceBusy for a day against ~10,000 parked rows while the true claimable backlog was zero.
    // The leased count stays unfiltered: a valid lease is in-flight work regardless of schedule.
    cmd.CommandText = $@"
      SELECT
        (SELECT count(*) FROM (SELECT 1 FROM {inbox} WHERE processed_at IS NULL
           AND (scheduled_for IS NULL OR scheduled_for <= now()) LIMIT 1000) a),
        (SELECT count(*) FROM (SELECT 1 FROM {inbox}
           WHERE instance_id IS NOT NULL AND lease_expiry > now() LIMIT 1000) b),
        COALESCE(EXTRACT(EPOCH FROM (now() - (
          SELECT received_at FROM {inbox} WHERE processed_at IS NULL
            AND (scheduled_for IS NULL OR scheduled_for <= now())
          ORDER BY received_at LIMIT 1))), 0)";
#pragma warning restore S2077

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken)) {
      // No row means the query answered nothing, which is NOT the same as "the service is settled".
      // Null keeps the caller's gate closed rather than licensing action off a measurement that was
      // never taken.
      return null;
    }
    return new ServiceBacklog {
      UnprocessedInboxRows = reader.GetInt64(0),
      ActiveLeasedRows = reader.GetInt64(1),
      // Clamped at zero: clock skew between writer and reader must not report negative lag.
      OldestUnprocessedAge = TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(2))),
    };
  }

  /// <inheritdoc />
  public async Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "record_heartbeat");

    var metadataJson = request.Metadata is { } meta
      ? meta.GetRawText()
      : "{}";

    // record_heartbeat returns BOOLEAN (migration 106): false means this instance_id has been
    // tombstoned in wh_instance_evictions and the caller must stop heartbeating. ExecuteSqlRawAsync
    // discards the function's own return value (it reports affected ROWS, not the result), so the
    // scalar has to be read directly — the same pattern already used for other scalar-returning
    // functions on this coordinator (see NotifyScheduledRetryDueAsync).
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText = $"SELECT {functionName}(@instanceId, @serviceName, @hostName, @processId, @metadata::jsonb)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("instanceId", request.InstanceId);
    cmd.Parameters.AddWithValue("serviceName", request.ServiceName);
    cmd.Parameters.AddWithValue("hostName", request.HostName);
    cmd.Parameters.AddWithValue("processId", request.ProcessId);
    cmd.Parameters.AddWithValue("metadata", metadataJson);

    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is bool accepted && accepted;
  }

  /// <inheritdoc />
  public async Task<int> NotifyScheduledRetryDueAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "notify_scheduled_retry_due");

#pragma warning disable S2077
    var sql = $"SELECT COALESCE(SUM(stream_count), 0)::int FROM {functionName}()";
#pragma warning restore S2077

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is int n ? n : 0;
  }

  /// <inheritdoc />
  public async Task<int> CompleteOutboxPublishedAsync(
    IReadOnlyList<Guid> ids, bool debugMode, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ids);
    if (ids.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "complete_outbox_published");

    var idArray = ids is Guid[] arr ? arr : [.. ids];
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_ids, @p_debug_mode)";
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_debug_mode", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = debugMode });
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    // Slice 5 of zero-idle-polling — piggyback an opportunistic heartbeat row
    // UPDATE so that during continuous work-completion activity the pod's
    // last_heartbeat_at stays fresh without waiting for the 30 s timer-driven
    // HeartbeatWorker tick. The SQL-side freshness guard makes repeated
    // calls within 10 s no-op (0 rows updated, no WAL pressure).
    await _opportunisticHeartbeatAsync(conn, cancellationToken).ConfigureAwait(false);
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <inheritdoc />
  public async Task<EphemeralReclassificationResult> ReclassifyEventsEphemeralAsync(
    IReadOnlyList<string> eventTypeNames, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeNames);
    if (eventTypeNames.Count == 0) {
      return EphemeralReclassificationResult.Empty;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "reclassify_events_ephemeral");
    var names = eventTypeNames as string[] ?? [.. eventTypeNames];

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText = $"SELECT events_reclassified, streams_reclassified, streams_blocked FROM {functionName}(@p_names)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("p_names", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = names });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return EphemeralReclassificationResult.Empty;
    }
    return new EphemeralReclassificationResult(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
  }

  /// <inheritdoc />
  public async Task<long> CountSourcedEventsForTypesAsync(
    IReadOnlyList<string> eventTypeNames, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeNames);
    if (eventTypeNames.Count == 0) {
      return 0L;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var eventStore = BuildSchemaQualifiedName(schema, "wh_event_store");
    var normalizeFn = BuildSchemaQualifiedName(schema, "normalize_event_type");
    var names = eventTypeNames as string[] ?? [.. eventTypeNames];

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText =
      $"SELECT count(*) FROM {eventStore} es " +
      "WHERE (es.flags & 8) = 0 " +
      $"AND es.event_type IN (SELECT {normalizeFn}(t) FROM unnest(@p_names) AS t)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("p_names", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = names });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<TypeDefinitionInfo>> GetTypeDefinitionsAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var table = BuildSchemaQualifiedName(schema, "wh_type_definitions");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText =
      $"SELECT definition_id, event_type, encode(settings_hash, 'hex'), encode(schema_hash, 'hex'), schema_version FROM {table}";
#pragma warning restore S2077
    var list = new List<TypeDefinitionInfo>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new TypeDefinitionInfo(
        reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
    }
    return list;
  }

  /// <inheritdoc />
  public async Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
    string eventTypeName, string settingsHashHex, string schemaHashHex, int schemaVersion,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "register_type_definition");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText = $"SELECT definition_id, is_new, previous_definition_id FROM {fn}(@t, @sh, @sch, @v)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlTypes.NpgsqlDbType.Text) { Value = eventTypeName });
    cmd.Parameters.Add(new NpgsqlParameter("sh", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = Convert.FromHexString(settingsHashHex) });
    cmd.Parameters.Add(new NpgsqlParameter("sch", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = Convert.FromHexString(schemaHashHex) });
    cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlTypes.NpgsqlDbType.Integer) { Value = schemaVersion });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return TypeDefinitionRegistration.None;
    }
    var prev = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? (int?)null : reader.GetInt32(2);
    return new TypeDefinitionRegistration(reader.GetInt32(0), reader.GetBoolean(1), prev);
  }

  /// <inheritdoc />
  public async Task RecordDefinitionLineageAsync(
    int fromDefinitionId, int toDefinitionId, DefinitionRelationship relationship, string? migrationRef,
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "record_definition_lineage");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText = $"SELECT {fn}(@from, @to, @rel, @ref)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.Integer) { Value = fromDefinitionId });
    cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.Integer) { Value = toDefinitionId });
    cmd.Parameters.Add(new NpgsqlParameter("rel", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = (short)relationship });
    cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)migrationRef ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyCollection<Guid>> GetStateBasedStreamIdsAsync(
    IReadOnlyList<Guid> streamIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return Array.Empty<Guid>();
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var eventStore = BuildSchemaQualifiedName(schema, "wh_event_store");
    var ids = streamIds as Guid[] ?? [.. streamIds];
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    // StateBased = Ephemeral (8) OR Compacted (16). The rebuild/rewind guards refuse both — a compacted stream
    // replays only to its Compacted origin, an ephemeral stream's bodies are reaped — so neither is
    // rebuildable-from-events. The reaper stays on flags&8 (self-destruct); a compacted event is never reaped.
#pragma warning disable S2077
    cmd.CommandText = $"SELECT DISTINCT stream_id FROM {eventStore} WHERE stream_id = ANY(@ids) AND (flags & 24) <> 0";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ids });
    var result = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      result.Add(reader.GetGuid(0));
    }
    return result;
  }

  /// <inheritdoc />
  public async Task SyncEphemeralTypeGraceAsync(
    IReadOnlyList<EphemeralTypeGrace> graceOverrides, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(graceOverrides);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "sync_ephemeral_type_grace");
    var names = new string[graceOverrides.Count];
    var graces = new int[graceOverrides.Count];
    for (var i = 0; i < graceOverrides.Count; i++) {
      names[i] = graceOverrides[i].EventTypeName;
      graces[i] = graceOverrides[i].GraceSeconds;
    }
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {fn}(@names, @graces)";
    cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = names });
    cmd.Parameters.Add(new NpgsqlParameter("graces", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) { Value = graces });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task SyncPerspectiveRetentionAsync(
    IReadOnlyList<PerspectiveRetentionDeclaration> declarations, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(declarations);
    if (declarations.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "sync_perspective_retention");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    foreach (var declaration in declarations) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText =
        $"SELECT {fn}(@clr, @enrolled, @ttl, @maxage, @cap, @capkey); " +
        "UPDATE " + BuildSchemaQualifiedName(schema, "wh_perspective_registry") +
        " SET row_cap_per_scope = @cap, row_cap_scope_key = @capkey WHERE clr_type_name = @clr";
      cmd.Parameters.Add(new NpgsqlParameter("clr", declaration.ClrTypeName));
      cmd.Parameters.Add(new NpgsqlParameter("enrolled", declaration.Enrolled));
      cmd.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlTypes.NpgsqlDbType.Integer) {
        Value = (object?)declaration.TtlSeconds ?? DBNull.Value
      });
      cmd.Parameters.Add(new NpgsqlParameter("maxage", NpgsqlTypes.NpgsqlDbType.Integer) {
        Value = (object?)declaration.MaxAgeSeconds ?? DBNull.Value
      });
      cmd.Parameters.Add(new NpgsqlParameter("cap", NpgsqlTypes.NpgsqlDbType.Integer) {
        Value = (object?)declaration.CapPerScope ?? DBNull.Value
      });
      cmd.Parameters.Add(new NpgsqlParameter("capkey", NpgsqlTypes.NpgsqlDbType.Text) {
        Value = (object?)declaration.CapScopeKey ?? DBNull.Value
      });
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var body = BuildSchemaQualifiedName(schema, "wh_event_body");
    var store = BuildSchemaQualifiedName(schema, "wh_event_store");
    var grace = BuildSchemaQualifiedName(schema, "wh_ephemeral_type_grace");
    var assoc = BuildSchemaQualifiedName(schema, "wh_message_associations");
    var cursors = BuildSchemaQualifiedName(schema, "wh_perspective_cursors");
    var snaps = BuildSchemaQualifiedName(schema, "wh_perspective_snapshots");
    var perspEvents = BuildSchemaQualifiedName(schema, "wh_perspective_events");
    var settings = BuildSchemaQualifiedName(schema, "wh_settings");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077
    cmd.CommandText =
      $"SELECT DISTINCT es.stream_id, ma.target_name, c.last_event_id " +
      $"FROM {body} eb " +
      $"JOIN {store} es ON es.event_id = eb.event_id " +
      $"LEFT JOIN {grace} g ON g.event_type = es.event_type " +
      $"JOIN {assoc} ma ON ma.normalized_message_type = es.event_type AND ma.association_type = 'perspective' " +
      $"JOIN {cursors} c ON c.stream_id = es.stream_id AND c.perspective_name = ma.target_name " +
      // #13b4 safety gate: scope to EPHEMERAL events explicitly — once sourced bodies live in
      // wh_event_body (full split), consumed sourced events must not become snapshot targets.
      "WHERE (es.flags & 8) = 8 " +
      $"AND es.created_at < NOW() - (COALESCE(g.grace_seconds, " +
      $"    (SELECT setting_value::int FROM {settings} WHERE setting_key = 'ephemeral_rewind_grace_seconds'), 300) " +
      $"  * INTERVAL '1 second') " +
      "AND es.commit_sequence IS NOT NULL AND c.last_event_id IS NOT NULL " +
      $"AND NOT EXISTS (SELECT 1 FROM {perspEvents} pe WHERE pe.event_id = eb.event_id AND pe.processed_at IS NULL) " +
      $"AND NOT EXISTS (SELECT 1 FROM {snaps} s WHERE s.stream_id = es.stream_id " +
      "  AND s.perspective_name = ma.target_name AND s.snapshot_commit_sequence >= es.commit_sequence)";
#pragma warning restore S2077
    var list = new List<EphemeralSnapshotTarget>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new EphemeralSnapshotTarget(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2)));
    }
    return list;
  }

  /// <inheritdoc />
  public async Task<bool> IntegrityTryBeginReportAsync(
      IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    await _integrityLedgerBoolAsync("wh_integrity_try_begin_report", cmd => {
      _bindDivergenceKey(cmd, key);
      cmd.Parameters.AddWithValue("p_origin_lo", originLo);
      cmd.Parameters.AddWithValue("p_origin_hi", originHi);
      cmd.Parameters.AddWithValue("p_local_lo", localLo);
      cmd.Parameters.AddWithValue("p_local_hi", localHi);
      cmd.Parameters.AddWithValue("p_now", now);
      cmd.Parameters.AddWithValue("p_cooldown_seconds", (int)cooldown.TotalSeconds);
    }, failOpen: true, cancellationToken).ConfigureAwait(false);

  /// <inheritdoc />
  public async Task<bool> IntegrityTryBeginRepairAsync(
      IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      CancellationToken cancellationToken = default) =>
    await _integrityLedgerBoolAsync("wh_integrity_try_begin_repair", cmd => {
      _bindDivergenceKey(cmd, key);
      cmd.Parameters.AddWithValue("p_now", now);
      cmd.Parameters.AddWithValue("p_base_backoff_secs", (int)baseBackoff.TotalSeconds);
      cmd.Parameters.AddWithValue("p_max_attempts", maxAttempts);
    }, failOpen: false, cancellationToken).ConfigureAwait(false);

  /// <inheritdoc />
  public async Task IntegrityMarkHealedAsync(
      IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
    _ = await _integrityLedgerBoolAsync("wh_integrity_mark_healed",
      cmd => _bindDivergenceKey(cmd, key), failOpen: false, cancellationToken).ConfigureAwait(false);

  private static void _bindDivergenceKeyArrays(
      Npgsql.NpgsqlCommand cmd, Guid originServiceId,
      IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys) {
    cmd.Parameters.AddWithValue("p_origin_service_id", originServiceId);
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_tenant_scopes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = keys.Select(k => k.TenantScope).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_event_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = keys.Select(k => k.EventType).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = keys.Select(k => k.StreamId).ToArray()
    });
  }

  /// <summary>The array sibling of <c>_integrityLedgerBoolAsync</c>: one batch function call
  /// returning BOOLEAN[]. Null on ANY failure — the caller then loops the single-key path, whose
  /// per-operation fail-open/fail-closed semantics remain the authority.</summary>
  private async Task<IReadOnlyList<bool>?> _integrityLedgerBoolArrayAsync(
      string fn, Action<Npgsql.NpgsqlCommand> bind, CancellationToken ct) {
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
      var qualified = BuildSchemaQualifiedName(schema, fn);
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), ct);
      var conn = __scope.Connection;
      await using var cmd = conn.CreateCommand();
      bind(cmd);
      var args = string.Join(",", cmd.Parameters
        .Cast<Npgsql.NpgsqlParameter>()
        .Select(p => "@" + p.ParameterName));
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT {qualified}({args})";
#pragma warning restore S2077
      var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
      return result as bool[];
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; falls back to the single-key functions.
      _logger?.LogWarning(ex,
        "Integrity ledger batch {Function} failed; falling back to single-key calls for this chunk.", fn);
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<bool>?> IntegrityTryBeginReportBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityReportObservation> observations,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    _integrityLedgerBoolArrayAsync("wh_integrity_try_begin_report_batch", cmd => {
      cmd.Parameters.AddWithValue("p_origin_service_id", originServiceId);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_tenant_scopes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = observations.Select(o => o.Key.TenantScope).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_event_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = observations.Select(o => o.Key.EventType).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = observations.Select(o => o.Key.StreamId).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin_los", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = observations.Select(o => o.OriginLo).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin_his", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = observations.Select(o => o.OriginHi).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_local_los", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = observations.Select(o => o.LocalLo).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_local_his", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = observations.Select(o => o.LocalHi).ToArray()
      });
      cmd.Parameters.AddWithValue("p_now", now);
      cmd.Parameters.AddWithValue("p_cooldown_seconds", (int)cooldown.TotalSeconds);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<bool>?> IntegrityTryBeginRepairBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants,
      CancellationToken cancellationToken = default) =>
    _integrityLedgerBoolArrayAsync("wh_integrity_try_begin_repair_batch", cmd => {
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
      cmd.Parameters.AddWithValue("p_now", now);
      cmd.Parameters.AddWithValue("p_base_backoff_secs", (int)baseBackoff.TotalSeconds);
      cmd.Parameters.AddWithValue("p_max_attempts", maxAttempts);
      cmd.Parameters.AddWithValue("p_max_grants", maxGrants);
    }, cancellationToken);

  /// <inheritdoc />
  /// <remarks>Best-effort: a window stamp that cannot reach the ledger degrades to a coarser
  /// dispatch range later, never a failed comparison now.</remarks>
  public async Task IntegrityStampRepairWindowsAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      long windowFrom, long windowUntil, CancellationToken cancellationToken = default) {
    if (keys.Count == 0) {
      return;
    }
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
      var qualified = BuildSchemaQualifiedName(schema, "wh_integrity_stamp_repair_windows");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCommand();
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
      cmd.Parameters.AddWithValue("p_window_from", windowFrom);
      cmd.Parameters.AddWithValue("p_window_until", windowUntil);
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT {qualified}(@p_origin_service_id,@p_tenant_scopes,@p_event_types,@p_stream_ids,@p_window_from,@p_window_until)";
#pragma warning restore S2077
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the drain derives a coarser window instead.
      _logger?.LogWarning(ex, "Integrity window stamp failed; the drain will derive a coarser range for these buckets.");
#pragma warning restore CA1848
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
      IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      int limit, CancellationToken cancellationToken = default) {
    if (originIds.Count == 0 || limit <= 0) {
      return [];
    }
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
      var qualified = BuildSchemaQualifiedName(schema, "wh_integrity_claim_repair_drain");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCommand();
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = originIds.ToArray()
      });
      cmd.Parameters.AddWithValue("p_now", now);
      cmd.Parameters.AddWithValue("p_base_backoff_secs", (int)baseBackoff.TotalSeconds);
      cmd.Parameters.AddWithValue("p_max_attempts", maxAttempts);
      cmd.Parameters.AddWithValue("p_limit", limit);
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT origin_service_id, tenant_scope, event_type, stream_id, window_from, window_until FROM {qualified}(@p_origin_ids,@p_now,@p_base_backoff_secs,@p_max_attempts,@p_limit)";
#pragma warning restore S2077
      var items = new List<IntegrityRepairDrainItem>(limit);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        items.Add(new IntegrityRepairDrainItem(
          reader.GetGuid(0),
          reader.GetString(1),
          reader.GetString(2),
          reader.GetGuid(3),
          await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(4),
          await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(5)));
      }
      return items;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the drain simply waits for the next pass.
      _logger?.LogWarning(ex, "Integrity repair-drain claim failed; nothing dispatched this pass.");
#pragma warning restore CA1848
      return [];
    }
  }

  /// <inheritdoc />
  public async Task<bool> IntegrityMarkHealedBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      CancellationToken cancellationToken = default) =>
    await IntegrityMarkHealedBatchWithAgesAsync(originServiceId, keys, cancellationToken).ConfigureAwait(false) is not null;

  /// <inheritdoc />
  public async Task<IReadOnlyList<double>?> IntegrityMarkHealedBatchWithAgesAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      CancellationToken cancellationToken = default) {
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
      var qualified = BuildSchemaQualifiedName(schema, "wh_integrity_mark_healed_batch");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      var conn = __scope.Connection;
      await using var cmd = conn.CreateCommand();
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT {qualified}(@p_origin_service_id,@p_tenant_scopes,@p_event_types,@p_stream_ids)";
#pragma warning restore S2077
      var ages = new List<double>(keys.Count);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)) {
          ages.Add(reader.GetDouble(0));
        }
      }
      return ages;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; falls back to the single-key functions.
      _logger?.LogWarning(ex,
        "Integrity ledger batch wh_integrity_mark_healed_batch failed; falling back to single-key calls for this chunk.");
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// Degrades to the empty reading rather than throwing: a metrics refresh that cannot reach the
  /// ledger must never take down the caller. It is logged, because a gauge silently pinned at zero
  /// reads exactly like a healthy system — the failure mode this whole change exists to avoid.
  /// </remarks>
  public async Task<Whizbang.Core.Observability.LedgerGaugeSnapshot> GetIntegrityLedgerSummaryAsync(
      int maxRepairAttempts, CancellationToken cancellationToken = default) {
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
      var fn = BuildSchemaQualifiedName(schema, "wh_integrity_ledger_summary");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant.
      cmd.CommandText = $"SELECT unhealed_buckets, repair_exhausted, oldest_unhealed_secs FROM {fn}(@max)";
#pragma warning restore S2077
      cmd.Parameters.AddWithValue("max", maxRepairAttempts);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        return Whizbang.Core.Observability.LedgerGaugeSnapshot.Empty;
      }
      var snapshot = new Whizbang.Core.Observability.LedgerGaugeSnapshot {
        UnhealedBuckets = reader.GetInt64(0),
        RepairExhausted = reader.GetInt64(1),
        OldestUnhealedAgeSeconds = reader.GetDouble(2),
      };
      await reader.DisposeAsync().ConfigureAwait(false);
      // Per-origin verified watermarks for the sealed_through gauge — a handful of rows read in
      // the same breath (same connection scope, same cadence) as the ledger summary.
      var sealsSchema = BuildSchemaQualifiedName(schema, "wh_integrity_seals");
      await using var sealsCmd = __scope.Connection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table name built from a validated schema constant.
      sealsCmd.CommandText = $"SELECT origin_service_id, sealed_through FROM {sealsSchema}";
#pragma warning restore S2077
      var seals = new List<Whizbang.Core.Observability.OriginSeal>();
      await using (var sealsReader = await sealsCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
        while (await sealsReader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          seals.Add(new Whizbang.Core.Observability.OriginSeal(sealsReader.GetGuid(0), sealsReader.GetInt64(1)));
        }
      }
      return snapshot with { Seals = seals };
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; a source-generated message would require making this type partial.
      _logger?.LogWarning(ex,
        "Integrity ledger summary failed; convergence gauges will read as healthy until this is resolved.");
#pragma warning restore CA1848
      return Whizbang.Core.Observability.LedgerGaugeSnapshot.Empty;
    }
  }

  private static void _bindDivergenceKey(Npgsql.NpgsqlCommand cmd, IntegrityRepairLedger.DivergenceKey key) {
    cmd.Parameters.AddWithValue("p_origin_service_id", key.OriginServiceId);
    cmd.Parameters.AddWithValue("p_tenant_scope", (object?)key.TenantScope ?? string.Empty);
    cmd.Parameters.AddWithValue("p_event_type", key.EventType);
    cmd.Parameters.AddWithValue("p_stream_id", key.StreamId);
  }

  /// <summary>
  /// Calls a ledger function. On failure the caller's prior behaviour is preserved via
  /// <paramref name="failOpen"/>: reporting proceeds (over-reporting is recoverable), repair does
  /// not (an unbounded repair request against real data is not).
  /// </summary>
  private async Task<bool> _integrityLedgerBoolAsync(
      string fn, Action<Npgsql.NpgsqlCommand> bind, bool failOpen, CancellationToken ct) {
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
      var qualified = BuildSchemaQualifiedName(schema, fn);
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), ct);
      var conn = __scope.Connection;
      await using var cmd = conn.CreateCommand();
      // Bind first, then build the call from the bound names. Positional $n placeholders require
      // POSITIONAL parameters in Npgsql; binding by name against them fails at execute time with
      // "bind message supplies 0 parameters", which the catch below would have turned into a
      // silent fail-open — reporting unbounded and repair permanently off, i.e. worse than the
      // in-memory ledger this replaces. Deriving the argument list from the parameters themselves
      // makes the two impossible to disagree.
      bind(cmd);
      var args = string.Join(",", cmd.Parameters
        .Cast<Npgsql.NpgsqlParameter>()
        .Select(p => "@" + p.ParameterName));
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT {qualified}({args})";
#pragma warning restore S2077
      var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
      return result is bool b ? b : failOpen;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      // Never silent: a swallowed failure here degrades convergence invisibly, which is exactly
      // how a broken ledger would masquerade as a working one.
#pragma warning disable CA1848 // Rare error path; a source-generated message would require making this type partial.
      _logger?.LogWarning(ex,
        "Integrity ledger call {Function} failed; continuing with failOpen={FailOpen}. " +
        "Convergence bounding is degraded until this is resolved.", fn, failOpen);
#pragma warning restore CA1848
      return failOpen;
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "wh_tables_needing_rewrite");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT table_name, bloat_ratio, requested FROM {fn}()";
#pragma warning restore S2077
    var results = new List<TableRewriteCandidate>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new TableRewriteCandidate(reader.GetString(0), (double)reader.GetDecimal(1), reader.GetBoolean(2)));
    }
    return results;
  }

  /// <inheritdoc />
  /// <remarks>
  /// VACUUM FULL cannot be parameterised and cannot run inside a transaction, so the table name is
  /// validated against the framework's own naming rule and quoted before interpolation, and the
  /// command runs on its own connection outside any ambient transaction. Callers only ever pass
  /// names this coordinator itself returned from <see cref="GetTablesNeedingRewriteAsync"/>, but
  /// the check is here rather than at the call site because that is where the injection risk
  /// actually lands.
  /// </remarks>
  public async Task<double?> RewriteTableAsync(string tableName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    if (!_isFrameworkTableName(tableName)) {
      throw new ArgumentException(
        $"Refusing to rewrite '{tableName}': only framework tables matching wh_[a-z0-9_] may be rewritten.",
        nameof(tableName));
    }

    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var qualified = BuildSchemaQualifiedName(schema, tableName);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;

    await using (var vacuum = conn.CreateCommand()) {
#pragma warning disable S2077 // Table name validated against ^wh_[a-z0-9_]+$ above; VACUUM takes no parameters
      vacuum.CommandText = $"VACUUM (FULL) {qualified}";
#pragma warning restore S2077
      vacuum.CommandTimeout = 0;   // a large table can take minutes; the operator opted into this
      await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Re-measure so the caller can verify the rewrite actually helped rather than assuming it did.
    await using (var analyze = conn.CreateCommand()) {
#pragma warning disable S2077
      analyze.CommandText = $"ANALYZE {qualified}";
#pragma warning restore S2077
      await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    await using var measure = conn.CreateCommand();
    // pg_class.reltuples, NOT pg_stat_user_tables.n_live_tup: the stats collector is
    // asynchronous, so n_live_tup can lag the VACUUM FULL that just ran and the
    // effectiveness comparison then races the collector — a real rewrite read as
    // "ineffective" under load. reltuples is written by VACUUM/ANALYZE transactionally in
    // the catalog, so the re-measure sees exactly the rewrite it performed.
    measure.CommandText = """
      SELECT (pg_relation_size(c.oid)::NUMERIC / NULLIF(c.reltuples::NUMERIC, 0)) / GREATEST(w.expected, 1)
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      JOIN LATERAL (
        SELECT COALESCE(sum(s.avg_width), 0) + 28 AS expected
        FROM pg_stats s WHERE s.schemaname = n.nspname AND s.tablename = c.relname
      ) w ON TRUE
      WHERE n.nspname = current_schema() AND c.relname = @t
      """;
    measure.Parameters.AddWithValue("t", tableName);
    var scalar = await measure.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return scalar is decimal d ? (double)d : null;
  }

  /// <inheritdoc />
  /// <summary>
  /// Whether a name is one of the framework's own tables, checked character by character rather
  /// than by regex. VACUUM FULL cannot be parameterised, so this is the only thing standing
  /// between a caller-supplied name and interpolated DDL — a plain scan has no backtracking
  /// behaviour to reason about and no timeout to forget.
  /// </summary>
  private static bool _isFrameworkTableName(string name) {
    if (name.Length <= 3 || name.Length > 63 || !name.StartsWith("wh_", StringComparison.Ordinal)) {
      return false;
    }
    foreach (var c in name) {
      var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
      if (!ok) {
        return false;
      }
    }
    return true;
  }

  /// <inheritdoc />
  public async Task RequestTableRewriteAsync(string tableName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "wh_request_table_rewrite");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {fn}(@t)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("t", tableName);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<bool> RecordInstanceStateAsync(
      Guid instanceId, string lifecyclePhase, string? libraryVersion = null,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(lifecyclePhase);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "record_instance_state");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {fn}(@id, @phase, @version)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("phase", lifecyclePhase);
    cmd.Parameters.AddWithValue("version", (object?)libraryVersion ?? DBNull.Value);
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is true;
  }

  /// <inheritdoc />
  public async Task<bool> RequestStandbyAsync(Guid instanceId, string version, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(version);
    return await _scalarFunctionAsync<bool>("request_standby", cancellationToken,
      ("id", instanceId), ("version", version)).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<bool> ClearStandbyRequestAsync(Guid instanceId, CancellationToken cancellationToken = default) {
    return await _scalarFunctionAsync<bool>("clear_standby", cancellationToken,
      ("id", instanceId)).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task EvictInstanceAsync(Guid instanceId, Guid evictedBy, string reason, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    _ = await _scalarFunctionAsync<object>("evict_instance", cancellationToken,
      ("id", instanceId), ("by", evictedBy), ("reason", reason)).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<StandbyRequest?> GetStandbyRequestAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var requests = BuildSchemaQualifiedName(schema, "wh_standby_requests");
    var instances = BuildSchemaQualifiedName(schema, "wh_service_instances");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified names built from validated schema constant
    cmd.CommandText = $@"
      SELECT r.requested_by, r.requested_version, r.requested_at, i.last_heartbeat_at
      FROM {requests} r
      LEFT JOIN {instances} i ON i.instance_id = r.requested_by";
#pragma warning restore S2077
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return null;
    }
    return new StandbyRequest(
      reader.GetGuid(0),
      reader.GetString(1),
      reader.GetFieldValue<DateTime>(2) is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : DateTimeOffset.MinValue,
      reader.IsDBNull(3)
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(3), DateTimeKind.Utc)));
  }

  /// <summary>Executes a schema-qualified scalar function with named parameters — the shared
  /// shape of the standby/eviction calls.</summary>
  private async Task<T?> _scalarFunctionAsync<T>(
      string functionName, CancellationToken cancellationToken, params (string Name, object Value)[] args) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, functionName);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {fn}({string.Join(", ", args.Select(a => "@" + a.Name))})";
#pragma warning restore S2077
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is T t ? t : default;
  }

  public async Task ClearTableRewriteRequestAsync(string tableName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "wh_clear_table_rewrite");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {fn}(@t)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("t", tableName);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<EphemeralPointerPruneResult> PruneAncientEphemeralPointersAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "prune_ancient_ephemeral_pointers");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT rows_pruned, status FROM {fn}()";
#pragma warning restore S2077
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new EphemeralPointerPruneResult(reader.GetInt64(0), reader.GetString(1));
  }

  /// <inheritdoc />
  public async Task<int> CloseDigestEpochsAsync(
    int settleSeconds, int maxEpochs, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "close_digest_epochs");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {fn}(NOW(), @settle, @max)";
#pragma warning restore S2077
    var pSettle = cmd.CreateParameter();
    pSettle.ParameterName = "settle";
    pSettle.Value = settleSeconds;
    cmd.Parameters.Add(pSettle);
    var pMax = cmd.CreateParameter();
    pMax.ParameterName = "max";
    pMax.Value = maxEpochs;
    cmd.Parameters.Add(pMax);
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is int closed ? closed : 0;
  }

  /// <inheritdoc />
  public Task<long?> GetIntegritySettledMaxAsync(
    Guid? originServiceId, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText = $"SELECT {BuildSchemaQualifiedName(schema, "integrity_settled_max")}(@p_origin, NOW(), @p_settle)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)originServiceId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", (int)settleWindow.TotalSeconds));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is long max ? (long?)max : null;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<long> GetIntegrityOriginGenerationAsync(CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var settings = BuildSchemaQualifiedName(schema, "wh_settings");
      cmd.CommandText =
        $"SELECT COALESCE((SELECT setting_value::bigint FROM {settings} WHERE setting_key = 'integrity_origin_generation'), 0)";
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is long generation ? generation : 0L;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<bool> EnsureIntegritySealGenerationAsync(
    Guid originServiceId, long generation, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText = $"SELECT {BuildSchemaQualifiedName(schema, "integrity_seal_generation_guard")}(@p_origin, @p_generation)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_generation", generation));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is bool coherent && coherent;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<EpochVerificationResult> VerifyDigestEpochsAsync(
    TimeSpan settleWindow, int maxEpochs, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        "SELECT epochs_checked, epochs_drifted " +
        $"FROM {BuildSchemaQualifiedName(schema, "verify_digest_epochs")}(NOW(), @p_settle, @p_max)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", (int)settleWindow.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_max", Math.Max(1, maxEpochs)));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      return new EpochVerificationResult(reader.GetInt32(0), reader.GetInt32(1));
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<StreamDigest>?> ComputeStreamDigestsForChunkAsync(
    Guid originServiceId, IReadOnlyList<Guid> streamIds,
    long? sinceSequence, long? untilSequence, TimeSpan settleWindow,
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // Bounded by the chunk: stream_id = ANY(named set). A NULL origin sequence never satisfies
      // a window comparison, so windowed folds naturally exclude unsequenced rows — matching the
      // answer side; a null window includes them (full history).
      cmd.CommandText = $"""
        SELECT COALESCE(es.scope->>'t', '') AS tenant, es.event_type, es.stream_id,
               bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
               bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
               COUNT(*)::int
        FROM {schema}.wh_event_store es
        LEFT JOIN {schema}.wh_event_body eb ON eb.event_id = es.event_id
        WHERE es.origin_service_id = @p_origin
          AND es.stream_id = ANY(@p_streams::uuid[])
          AND ((@p_since::bigint) IS NULL OR es.origin_commit_sequence >= @p_since)
          AND ((@p_until::bigint) IS NULL OR es.origin_commit_sequence < @p_until)
          AND COALESCE(es.flags, 0) & 8 = 0
          AND COALESCE((eb.metadata->>'deliveryGuarantee')::integer, 0) <> 1
          AND es.created_at < NOW() - @p_settle::interval
        GROUP BY 1, 2, 3
        ORDER BY 1, 2, 3
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_streams", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = streamIds.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_since", NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = (object?)sinceSequence ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_until", NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = (object?)untilSequence ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", $"{(int)settleWindow.TotalSeconds} seconds"));

      return (IReadOnlyList<StreamDigest>?)await _readStreamDigestsAsync(
        cmd, hasUpdatedAt: false, typeLevel: false, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<long> GetIntegritySealAsync(Guid originServiceId, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      cmd.CommandText = $"SELECT sealed_through FROM {schema}.wh_integrity_seals WHERE origin_service_id = @p_origin";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", originServiceId));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is long sealedThrough ? sealedThrough : 0L;
    }, cancellationToken);

  /// <inheritdoc />
  public Task AdvanceIntegritySealAsync(Guid originServiceId, long through, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
      // GREATEST: monotonic by construction — a late or replayed advance can only move forward.
      cmd.CommandText = $"""
        INSERT INTO {schema}.wh_integrity_seals (origin_service_id, sealed_through, updated_at)
        VALUES (@p_origin, @p_through, NOW())
        ON CONFLICT (origin_service_id) DO UPDATE
          SET sealed_through = GREATEST(wh_integrity_seals.sealed_through, EXCLUDED.sealed_through),
              updated_at = NOW()
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_through", through));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

  /// <summary>Clamps a requested window against the lane's settled max. Null = cannot window
  /// (nothing settled / no lane); otherwise the exclusive end the answer will actually cover.</summary>
  private async Task<long?> _clampWindowEndAsync(
      Guid? originServiceId, long? untilSequence, TimeSpan settleWindow, CancellationToken ct) {
    var settledMax = await GetIntegritySettledMaxAsync(originServiceId, settleWindow, ct).ConfigureAwait(false);
    if (settledMax is null) {
      return null;
    }
    return Math.Min(untilSequence ?? long.MaxValue, settledMax.Value + 1);
  }

  /// <inheritdoc />
  public async Task<WindowedDigestResult?> ComputeTypeDigestsWindowedAsync(
    Guid? originServiceId, IReadOnlyList<string>? eventTypes,
    long sinceSequence, long? untilSequence, TimeSpan settleWindow,
    CancellationToken cancellationToken = default) {
    var through = await _clampWindowEndAsync(originServiceId, untilSequence, settleWindow, cancellationToken)
      .ConfigureAwait(false);
    if (through is null || through <= sinceSequence) {
      // Nothing settled beyond the asker's watermark: an empty-but-honest answer. The watermark
      // stays put — claiming progress without coverage is how seals drift past reality.
      return new WindowedDigestResult { Digests = [], ComputedThrough = sinceSequence };
    }
    return await _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        "SELECT tenant, event_type, digest_lo, digest_hi, event_count " +
        $"FROM {BuildSchemaQualifiedName(schema, "compute_type_digests_epoch_window")}(@p_origin, @p_types, @p_since, @p_until, NOW(), @p_settle)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)originServiceId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes is null ? DBNull.Value : eventTypes.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_since", sinceSequence));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_until", through.Value));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", (int)settleWindow.TotalSeconds));

      var results = new List<StreamDigest>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var tenant = reader.GetString(0);
        results.Add(new StreamDigest {
          TenantScope = tenant.Length == 0 ? null : tenant,
          EventType = reader.GetString(1),
          StreamId = Guid.Empty,
          DigestLo = reader.GetInt64(2),
          DigestHi = reader.GetInt64(3),
          EventCount = reader.GetInt32(4),
          UpdatedAt = null,
        });
      }
      return (WindowedDigestResult?)new WindowedDigestResult {
        Digests = results,
        ComputedThrough = through.Value,
      };
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<WindowedDigestResult?> ComputeStreamDigestsWindowedAsync(
    Guid? originServiceId, IReadOnlyList<string>? eventTypes,
    long sinceSequence, long? untilSequence, Guid? resumeAfterStreamId, int maxDigests,
    TimeSpan settleWindow, CancellationToken cancellationToken = default) {
    var through = await _clampWindowEndAsync(originServiceId, untilSequence, settleWindow, cancellationToken)
      .ConfigureAwait(false);
    if (through is null || through <= sinceSequence) {
      return new WindowedDigestResult { Digests = [], ComputedThrough = sinceSequence };
    }
    var pageBound = Math.Max(1, maxDigests);
    return await _withCoordinatorCommandAsync(async (cmd, schema) => {
      // Pages walk WHOLE streams in stream-id order (streams are homogeneous — one type, one
      // tenant — so rows ≈ streams). The pick fetches one sentinel stream past the bound: its
      // presence is how we know the window is not complete without a second count query.
      cmd.CommandText = $"""
        WITH lane AS (
          SELECT es.*,
                 CASE WHEN (@p_origin::uuid) IS NULL THEN es.commit_sequence
                      ELSE es.origin_commit_sequence END AS lane_seq
          FROM {schema}.wh_event_store es
          WHERE ((@p_origin::uuid) IS NULL AND es.origin_service_id IS NULL
                 OR es.origin_service_id = @p_origin)
        ),
        win AS (
          SELECT l.* FROM lane l
          LEFT JOIN {schema}.wh_event_body eb ON eb.event_id = l.event_id
          WHERE l.lane_seq IS NOT NULL AND l.lane_seq >= @p_since AND l.lane_seq < @p_until
            AND ((@p_types::text[]) IS NULL OR l.event_type IN (SELECT {BuildSchemaQualifiedName(schema, "normalize_event_type")}(t) FROM unnest(@p_types::text[]) AS t))
            AND COALESCE(l.flags, 0) & 8 = 0
            AND COALESCE((eb.metadata->>'deliveryGuarantee')::integer, 0) <> 1
            AND l.created_at < NOW() - @p_settle::interval
        ),
        pick AS (
          SELECT DISTINCT w.stream_id FROM win w
          WHERE (@p_resume::uuid IS NULL OR w.stream_id > @p_resume)
          ORDER BY w.stream_id
          LIMIT @p_limit
        )
        SELECT COALESCE(w.scope->>'t', '') AS tenant, w.event_type, w.stream_id,
               bit_xor(hashtextextended(w.event_id::text, 0)) AS digest_lo,
               bit_xor(hashtextextended(w.event_id::text, 1)) AS digest_hi,
               COUNT(*)::int
        FROM win w JOIN pick p ON p.stream_id = w.stream_id
        GROUP BY 1, 2, 3
        ORDER BY 3, 1, 2
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)originServiceId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes is null ? DBNull.Value : eventTypes.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_since", sinceSequence));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_until", through.Value));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_resume", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)resumeAfterStreamId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_limit", pageBound + 1));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", $"{(int)settleWindow.TotalSeconds} seconds"));

      var rows = new List<StreamDigest>();
      await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          var tenant = reader.GetString(0);
          rows.Add(new StreamDigest {
            TenantScope = tenant.Length == 0 ? null : tenant,
            EventType = reader.GetString(1),
            StreamId = reader.GetGuid(2),
            DigestLo = reader.GetInt64(3),
            DigestHi = reader.GetInt64(4),
            EventCount = reader.GetInt32(5),
            UpdatedAt = null,
          });
        }
      }

      // The sentinel stream (if present) is dropped WHOLE — a split stream would let the asker
      // treat half its buckets as the complete story for that stream.
      var streamsInOrder = rows.Select(r => r.StreamId).Distinct().ToList();
      Guid? resume = null;
      if (streamsInOrder.Count > pageBound) {
        var sentinel = streamsInOrder[^1];
        rows.RemoveAll(r => r.StreamId == sentinel);
        resume = streamsInOrder[^2];
      }
      return (WindowedDigestResult?)new WindowedDigestResult {
        Digests = rows,
        ComputedThrough = through.Value,
        ResumeAfterStreamId = resume,
      };
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<StreamCloseResult> CloseStreamAsync(
    Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var fn = BuildSchemaQualifiedName(schema, "close_stream");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT close_status, events_truncated FROM {fn}(@sid, @through, @archive)";
#pragma warning restore S2077
    var pStream = cmd.CreateParameter();
    pStream.ParameterName = "sid";
    pStream.Value = streamId;
    cmd.Parameters.Add(pStream);
    var pThrough = cmd.CreateParameter();
    pThrough.ParameterName = "through";
    pThrough.Value = throughVersion;
    cmd.Parameters.Add(pThrough);
    var pArchive = cmd.CreateParameter();
    pArchive.ParameterName = "archive";
    pArchive.Value = archive;
    cmd.Parameters.Add(pArchive);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new StreamCloseResult(reader.GetString(0), reader.GetInt64(1));
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<ArchivedEvent>> GetArchivedEventsAsync(
    Guid streamId, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var archive = BuildSchemaQualifiedName(schema, "wh_event_archive");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant
    cmd.CommandText =
      $"SELECT event_id, stream_id, version, event_type, event_data::text, metadata::text " +
      $"FROM {archive} WHERE stream_id = @sid ORDER BY version";
#pragma warning restore S2077
    var pStream = cmd.CreateParameter();
    pStream.ParameterName = "sid";
    pStream.Value = streamId;
    cmd.Parameters.Add(pStream);
    var list = new List<ArchivedEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new ArchivedEvent(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5)));
    }
    return list;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<string>> GetConsumingPerspectiveNamesAsync(
    Guid streamId, long throughVersion, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var store = BuildSchemaQualifiedName(schema, "wh_event_store");
    var assoc = BuildSchemaQualifiedName(schema, "wh_message_associations");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table names built from validated schema constant
    cmd.CommandText =
      $"SELECT DISTINCT ma.target_name FROM {store} es " +
      $"JOIN {assoc} ma ON ma.normalized_message_type = es.event_type AND ma.association_type = 'perspective' " +
      $"WHERE es.stream_id = @sid AND es.version <= @through";
#pragma warning restore S2077
    var pStream = cmd.CreateParameter();
    pStream.ParameterName = "sid";
    pStream.Value = streamId;
    cmd.Parameters.Add(pStream);
    var pThrough = cmd.CreateParameter();
    pThrough.ParameterName = "through";
    pThrough.Value = throughVersion;
    cmd.Parameters.Add(pThrough);
    var list = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(reader.GetString(0));
    }
    return list;
  }

  /// <inheritdoc />
  public async Task<long?> GetEventVersionAsync(Guid eventId, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var store = BuildSchemaQualifiedName(schema, "wh_event_store");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant
    cmd.CommandText = $"SELECT version FROM {store} WHERE event_id = @id";
#pragma warning restore S2077
    var pId = cmd.CreateParameter();
    pId.ParameterName = "id";
    pId.Value = eventId;
    cmd.Parameters.Add(pId);
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is null || result is DBNull ? null : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var body = BuildSchemaQualifiedName(schema, "wh_event_body");
    var store = BuildSchemaQualifiedName(schema, "wh_event_store");
    var grace = BuildSchemaQualifiedName(schema, "wh_ephemeral_type_grace");
    var assoc = BuildSchemaQualifiedName(schema, "wh_message_associations");
    var snaps = BuildSchemaQualifiedName(schema, "wh_perspective_snapshots");
    var perspEvents = BuildSchemaQualifiedName(schema, "wh_perspective_events");
    var settings = BuildSchemaQualifiedName(schema, "wh_settings");
    var hold = BuildSchemaQualifiedName(schema, "wh_event_destruction_hold");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    // The exact predicate of migration 073 Task 8's DELETE, as a SELECT: ephemeral (flags&8), consumed
    // (no unprocessed work item), aged past its grace window, and snapshot-covered (no consuming
    // perspective lacks a covering snapshot). These are the bodies THIS maintenance cycle will reap.
#pragma warning disable S2077
    cmd.CommandText =
      $"SELECT es.event_id, es.stream_id, es.event_type " +
      $"FROM {body} eb " +
      $"JOIN {store} es ON es.event_id = eb.event_id " +
      $"LEFT JOIN {grace} g ON g.event_type = es.event_type " +
      "WHERE (es.flags & 8) = 8 " +
      // E2-3: skip bodies a hook already held (Cancel/Defer) — don't re-offer them until the hold lapses.
      $"AND NOT EXISTS (SELECT 1 FROM {hold} h WHERE h.event_id = eb.event_id AND h.hold_until > NOW()) " +
      $"AND es.created_at < NOW() - (COALESCE(g.grace_seconds, " +
      $"    (SELECT setting_value::int FROM {settings} WHERE setting_key = 'ephemeral_rewind_grace_seconds'), 300) " +
      $"  * INTERVAL '1 second') " +
      $"AND NOT EXISTS (SELECT 1 FROM {perspEvents} pe WHERE pe.event_id = eb.event_id AND pe.processed_at IS NULL) " +
      $"AND NOT EXISTS (SELECT 1 FROM {assoc} ma WHERE ma.normalized_message_type = es.event_type " +
      "  AND ma.association_type = 'perspective' " +
      $"  AND NOT EXISTS (SELECT 1 FROM {snaps} s WHERE s.stream_id = es.stream_id " +
      "    AND s.perspective_name = ma.target_name AND s.snapshot_commit_sequence >= es.commit_sequence))";
#pragma warning restore S2077
    var list = new List<EphemeralDestructionTarget>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new EphemeralDestructionTarget(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
    }
    return list;
  }

  /// <inheritdoc />
  public async Task HoldEphemeralDestructionAsync(
    IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var holdTable = BuildSchemaQualifiedName(schema, "wh_event_destruction_hold");
    var ids = eventIds as Guid[] ?? [.. eventIds];
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    // Upsert a hold for every event id to the same instant; a later decision (re-defer) overwrites it.
#pragma warning disable S2077 // Schema-qualified table name from validated schema constant; values are parameters
    cmd.CommandText =
      $"INSERT INTO {holdTable} (event_id, hold_until) " +
      "SELECT unnest(@ids), @until ON CONFLICT (event_id) DO UPDATE SET hold_until = EXCLUDED.hold_until";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ids });
    cmd.Parameters.Add(new NpgsqlParameter("until", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = holdUntil.UtcDateTime });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> RecordDestructionFailureAsync(
    IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
    Whizbang.Core.Lifecycle.OnDestroyFailure onFailure = Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenForcedDelete,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
    var holdTable = BuildSchemaQualifiedName(schema, "wh_event_destruction_hold");
    var bodyTable = BuildSchemaQualifiedName(schema, "wh_event_body");
    var ids = eventIds as Guid[] ?? [.. eventIds];
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    // Upsert +1 attempt per event. TTL-HALVING backoff (E2-5 inc 2): for an event that carries a TTL expiry
    // (ephemeral_expires_at), the retry is scheduled at the MIDPOINT to that expiry — NOW() + (expiry-NOW())/2 —
    // so retries decay across the remaining TTL window (60d → +30d → +15d → …), giving a failing compaction/
    // archive hook the whole window to recover. An event with no TTL (WhenConsumed) falls back to the fixed
    // @until backoff. Past the cap → hold_until '-infinity' so Task 8's `hold_until > NOW()` gate FORCE-deletes.
    // Return the batch's highest attempt count so the worker can log retry-vs-forced.
#pragma warning disable S2077 // Schema-qualified table names from validated schema constant; values are parameters
    // @pol (OnDestroyFailure): 2=ForceDeleteImmediately ('-infinity' now), 1=RetryThenKeep (past cap => keep,
    // 'infinity'), 0=RetryThenForcedDelete (past cap => force, '-infinity'). Under the cap all policies use the
    // TTL-halving/fallback retry_until.
    cmd.CommandText =
      $"WITH src AS ( " +
      $"  SELECT id AS event_id, " +
      $"    CASE WHEN @pol = 2 THEN '-infinity'::timestamptz " +
      $"         WHEN eb.metadata ->> 'ephemeral_expires_at' IS NOT NULL " +
      $"         THEN NOW() + ((eb.metadata ->> 'ephemeral_expires_at')::timestamptz - NOW()) / 2 " +
      $"         ELSE @until END AS retry_until " +
      $"  FROM unnest(@ids) AS id " +
      $"  LEFT JOIN {bodyTable} eb ON eb.event_id = id), " +
      $"upserted AS ( " +
      $"  INSERT INTO {holdTable} (event_id, hold_until, failure_count) " +
      $"  SELECT event_id, retry_until, 1 FROM src " +
      $"  ON CONFLICT (event_id) DO UPDATE SET " +
      $"    failure_count = {holdTable}.failure_count + 1, " +
      $"    hold_until = CASE " +
      $"      WHEN @pol = 2 THEN '-infinity'::timestamptz " +
      $"      WHEN {holdTable}.failure_count + 1 > @max " +
      $"        THEN (CASE WHEN @pol = 1 THEN 'infinity'::timestamptz ELSE '-infinity'::timestamptz END) " +
      $"      ELSE EXCLUDED.hold_until END " +
      $"  RETURNING failure_count) " +
      "SELECT COALESCE(MAX(failure_count), 0) FROM upserted";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ids });
    cmd.Parameters.Add(new NpgsqlParameter("until", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = retryHoldUntil.UtcDateTime });
    cmd.Parameters.Add(new NpgsqlParameter("max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = maxRetries });
    cmd.Parameters.Add(new NpgsqlParameter("pol", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)onFailure });
    return (int)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  // Issues a guarded UPDATE on wh_service_instances. No-op when the
  // instance provider isn't wired (the EFCoreWorkCoordinator can be
  // constructed without one — historical contract) or when the heartbeat
  // row was UPDATEd within the freshness window.
  private async Task _opportunisticHeartbeatAsync(
      System.Data.Common.DbConnection conn, CancellationToken cancellationToken) {
    if (_instanceProvider is null) {
      return;
    }
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, "wh_service_instances");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"UPDATE {tableName} SET last_heartbeat_at = NOW() WHERE instance_id = @p_id AND last_heartbeat_at < NOW() - make_interval(secs => @p_freshness)";
    cmd.Parameters.Add(new NpgsqlParameter("p_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = _instanceProvider.InstanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_freshness", NpgsqlTypes.NpgsqlDbType.Integer) { Value = OPPORTUNISTIC_HEARTBEAT_FRESHNESS_SECONDS });
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task CompletePerspectiveAsync(
    IReadOnlyList<PerspectiveCursorCompletion> cursors,
    IReadOnlyList<Guid> eventWorkIds,
    bool debugMode,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(cursors);
    ArgumentNullException.ThrowIfNull(eventWorkIds);
    if (cursors.Count == 0 && eventWorkIds.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "complete_perspective");

    var cursorsJson = _serializePerspectiveCompletions([.. cursors]);
    var idArray = eventWorkIds is Guid[] arr ? arr : [.. eventWorkIds];

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_cursors::jsonb, @p_ids, @p_debug_mode)";
    cmd.Parameters.Add(new NpgsqlParameter("p_cursors", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = cursorsJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_debug_mode", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = debugMode });
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }

  /// <inheritdoc />
  public async Task FlushCompletionsAsync(
    FlushCompletionsRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "flush_completions");

    var outboxIds = request.OutboxIds is null || request.OutboxIds.Count == 0
      ? []
      : (request.OutboxIds is Guid[] arr ? arr : [.. request.OutboxIds]);
    var perspIds = request.PerspectiveEventWorkIds is null || request.PerspectiveEventWorkIds.Count == 0
      ? []
      : (request.PerspectiveEventWorkIds is Guid[] parr ? parr : [.. request.PerspectiveEventWorkIds]);

    var cursorsJson = request.PerspectiveCursors is null || request.PerspectiveCursors.Count == 0
      ? "[]"
      : _serializePerspectiveCompletions([.. request.PerspectiveCursors]);

    var failuresJson = _buildFailuresByCategoryJson(request.FailuresByCategory);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_outbox, @p_cursors::jsonb, @p_persp, @p_fail::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_outbox", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = outboxIds });
    cmd.Parameters.Add(new NpgsqlParameter("p_cursors", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = cursorsJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_persp", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = perspIds });
    cmd.Parameters.Add(new NpgsqlParameter("p_fail", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = failuresJson });
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }

  private string _buildFailuresByCategoryJson(IReadOnlyList<CategoryFailures>? failures) {
    if (failures is null || failures.Count == 0) {
      return "[]";
    }
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < failures.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append("{\"Category\":\"").Append(failures[i].Category.ToSqlCategory()).Append("\",")
        .Append("\"Items\":").Append(_serializeFailures([.. failures[i].Items])).Append('}');
    }
    sb.Append(']');
    return sb.ToString();
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<SyncInquiryResult>> ResolveSyncInquiriesAsync(
    IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiry> inquiries, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(inquiries);
    if (inquiries.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "resolve_sync_inquiries");

    var inquiriesJson = _buildInquiriesJson(inquiries);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT inquiry_id, stream_id, pending_count, processed_count FROM {functionName}(@p_inq::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_inq", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = inquiriesJson });

    var results = new List<SyncInquiryResult>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new SyncInquiryResult {
        InquiryId = reader.GetGuid(0),
        StreamId = reader.GetGuid(1),
        PendingCount = reader.GetInt32(2),
        ProcessedCount = reader.GetInt32(3)
      });
    }
    return results;
  }

  private static string _buildInquiriesJson(IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiry> inquiries) {
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < inquiries.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      var inq = inquiries[i];
      sb.Append("{\"InquiryId\":\"").Append(inq.InquiryId).Append("\",")
        .Append("\"StreamId\":\"").Append(inq.StreamId).Append("\",")
        .Append("\"PerspectiveName\":\"").Append(_jsonEscape(inq.PerspectiveName)).Append("\",")
        .Append("\"DiscoverPendingFromOutbox\":").Append(inq.DiscoverPendingFromOutbox ? "true" : "false").Append(',')
        .Append("\"IncludePendingEventIds\":").Append(inq.IncludePendingEventIds ? "true" : "false").Append(',')
        .Append("\"IncludeProcessedEventIds\":").Append(inq.IncludeProcessedEventIds ? "true" : "false");
      if (inq.EventIds is { Length: > 0 } eids) {
        sb.Append(",\"EventIds\":[");
        for (var j = 0; j < eids.Length; j++) {
          if (j > 0) {
            sb.Append(',');
          }
          sb.Append('"').Append(eids[j]).Append('"');
        }
        sb.Append(']');
      }
      if (inq.EventTypeFilter is { Length: > 0 } types) {
        sb.Append(",\"EventTypeFilter\":[");
        for (var j = 0; j < types.Length; j++) {
          if (j > 0) {
            sb.Append(',');
          }
          sb.Append('"').Append(_jsonEscape(types[j])).Append('"');
        }
        sb.Append(']');
      }
      sb.Append('}');
    }
    sb.Append(']');
    return sb.ToString();
  }

  /// <inheritdoc />
  public async Task<WorkBatch> ClaimWorkAsync(
    ClaimWorkRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "claim_work");
    var outstandingFn = BuildSchemaQualifiedName(schema, "count_outstanding_work");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      $"SELECT source, work_id, work_stream_id, partition_number, destination, message_type, " +
      $"envelope_type, message_data, metadata, status, attempts, is_newly_stored, is_orphaned, " +
      $"perspective_name FROM {functionName}(@p_id, @p_svc, @p_host, @p_pid, @p_max, @p_part, @p_lease, @p_fresh)";
    if (request.IncludeOutstanding) {
      // #635: the outstanding-budget counts ride the claim's round trip as a second result set,
      // from the same snapshot, instead of a separate per-cycle call. Untruncated by design: they
      // come from count_outstanding_work, never from the claim's LIMITed CTEs.
      cmd.CommandText += $"; SELECT inbox_rows, outbox_rows, perspective_rows FROM {outstandingFn}(@p_id)";
    }
    cmd.Parameters.Add(new NpgsqlParameter("p_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = request.InstanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_svc", NpgsqlTypes.NpgsqlDbType.Text) { Value = request.ServiceName });
    cmd.Parameters.Add(new NpgsqlParameter("p_host", NpgsqlTypes.NpgsqlDbType.Text) { Value = request.HostName });
    cmd.Parameters.Add(new NpgsqlParameter("p_pid", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.ProcessId });
    cmd.Parameters.Add(new NpgsqlParameter("p_max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.MaxStreams });
    cmd.Parameters.Add(new NpgsqlParameter("p_part", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.PartitionCount });
    cmd.Parameters.Add(new NpgsqlParameter("p_lease", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.LeaseSeconds });
    cmd.Parameters.Add(new NpgsqlParameter("p_fresh", NpgsqlTypes.NpgsqlDbType.Double) { Value = request.FreshWorkShare });

    var rows = new List<WorkBatchRow>();
    OutstandingWork? outstanding = null;
    await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken)) {
      while (await reader.ReadAsync(cancellationToken)) {
        rows.Add(new WorkBatchRow {
          Source = reader.GetString(0),
          WorkId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
          StreamId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
          PartitionNumber = reader.IsDBNull(3) ? null : reader.GetInt32(3),
          Destination = reader.IsDBNull(4) ? null : reader.GetString(4),
          MessageType = reader.IsDBNull(5) ? null : reader.GetString(5),
          EnvelopeType = reader.IsDBNull(6) ? null : reader.GetString(6),
          MessageData = reader.IsDBNull(7) ? null : reader.GetString(7),
          Metadata = reader.IsDBNull(8) ? null : reader.GetValue(8)?.ToString(),
          Status = reader.IsDBNull(9) ? null : reader.GetInt32(9),
          Attempts = reader.IsDBNull(10) ? null : reader.GetInt32(10),
          IsNewlyStored = reader.IsDBNull(11) ? null : reader.GetBoolean(11),
          IsOrphaned = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
          PerspectiveName = reader.IsDBNull(13) ? null : reader.GetString(13)
        });
      }
      if (request.IncludeOutstanding && await reader.NextResultAsync(cancellationToken)
          && await reader.ReadAsync(cancellationToken)) {
        outstanding = new OutstandingWork {
          InboxRows = reader.GetInt64(0),
          OutboxRows = reader.GetInt64(1),
          PerspectiveRows = reader.GetInt64(2),
        };
      }
    }

    // Phase H step 5d: claim_work no longer projects outbox or inbox bodies — only
    // (work_id, stream_id). Both OutboxWork and InboxWork stay empty. The legacy
    // OutboxPublishWorker / InboxDispatchWorker channels are populated by the new
    // OutboxDrainWorker / InboxDrainWorker after they fetch payloads on demand.
    //
    // v0.657 slice 3: stream_id coalesce moved into StreamIdCoalescer.Coalesce so
    // Guid.Empty rows are recovered via WorkId fallback (with a Warning naming
    // the offending row) instead of being silently dropped. See
    // operations/configuration/empty-stream-id-policy for the production forensic investigation.
    var (perspectiveStreamIds, outboxStreamIds, inboxStreamIds) =
      StreamIdCoalescer.Coalesce(rows, _logger);

    return new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      PerspectiveStreamIds = perspectiveStreamIds,
      OutboxStreamIds = outboxStreamIds,
      InboxStreamIds = inboxStreamIds,
      Outstanding = outstanding
    };
  }

  /// <inheritdoc />
  public async Task CommitHandlerResultAsync(
    HandlerCommitRequest request,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "commit_handler_result");

    var payload = _buildHandlerCommitPayload(request);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_request::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_request", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = payload });
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(
    IReadOnlyList<HandlerCommitRequest> requests,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(requests);
    if (requests.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "commit_handler_batch");

    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < requests.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      sb.Append(_buildHandlerCommitPayload(requests[i]));
    }
    sb.Append(']');
    var batchJson = sb.ToString();

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT handler_id, success, error_message, tier, bulk_error FROM {functionName}(@p_results::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_results", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = batchJson });

    var results = new List<HandlerBatchResult>(requests.Count);
    var fellBack = false;
    string? bulkError = null;
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new HandlerBatchResult(
        HandlerId: reader.GetGuid(0),
        Success: reader.GetBoolean(1),
        ErrorMessage: reader.IsDBNull(2) ? null : reader.GetString(2)));
      if (!fellBack && reader.GetInt32(3) == 2) {
        fellBack = true;
        bulkError = reader.IsDBNull(4) ? null : reader.GetString(4);
      }
    }
    if (fellBack) {
      // #573: the fallback is legitimate; the silence was not. One warning per batch with
      // the Tier-1 SQLSTATE (the diagnosis), and a counter the operator can alert on when
      // a fleet quietly lives on the slow per-handler path.
      if (_logger is not null) {
        EFCoreWorkCoordinatorLog.CommitBulkTierFellBack(_logger, results.Count, bulkError ?? "unknown");
      }
      _metrics?.CommitHandlerFallbacks.Add(1);
    }
    return results;
  }

  private string _buildHandlerCommitPayload(HandlerCommitRequest request) {
    // Build JSONB that commit_handler_result expects:
    //   { instance_id, service_name, host_name, process_id, partition_count, debug_mode,
    //     handler_id, inbox_completion: {MessageId, Status},
    //     new_outbox_messages: [...], new_inbox_messages: [...] }
    var sb = new System.Text.StringBuilder("{");
    sb.Append("\"handler_id\":\"").Append(request.HandlerId).Append('"');
    sb.Append(",\"instance_id\":\"").Append(request.InstanceId).Append('"');
    sb.Append(",\"service_name\":\"").Append(_jsonEscape(request.ServiceName)).Append('"');
    sb.Append(",\"host_name\":\"").Append(_jsonEscape(request.HostName)).Append('"');
    sb.Append(",\"process_id\":").Append(request.ProcessId);
    sb.Append(",\"partition_count\":").Append(request.PartitionCount);
    sb.Append(",\"debug_mode\":").Append(request.DebugMode ? "true" : "false");
    sb.Append(",\"inbox_completion\":{")
      .Append("\"MessageId\":\"").Append(request.InboxCompletion.MessageId).Append("\",")
      .Append("\"Status\":").Append(request.InboxCompletion.Status)
      .Append('}');
    var newOutboxArr = request.NewOutboxMessages?.ToArray() ?? [];
    sb.Append(",\"new_outbox_messages\":").Append(_serializeNewOutboxMessages(newOutboxArr));
    var newInboxArr = request.NewInboxMessages?.ToArray() ?? [];
    sb.Append(",\"new_inbox_messages\":").Append(_serializeNewInboxMessages(newInboxArr));
    sb.Append('}');
    return sb.ToString();
  }

  private static string _jsonEscape(string s) =>
    s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

  /// <inheritdoc />
  public async Task ReportFailuresAsync(
    WorkCategory category,
    IReadOnlyList<MessageFailure> failures,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(failures);
    if (failures.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "report_failures");

    var failuresJson = _serializeFailures([.. failures]);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_category, @p_failures::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_category", NpgsqlTypes.NpgsqlDbType.Text) { Value = category.ToSqlCategory() });
    cmd.Parameters.Add(new NpgsqlParameter("p_failures", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = failuresJson });
    _ = await cmd.ExecuteScalarAsync(cancellationToken);
  }

  /// <inheritdoc />
  public async Task<int> RenewLeasesAsync(
    WorkCategory category,
    IReadOnlyList<Guid> ids,
    int leaseSeconds = 300,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ids);
    if (ids.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "renew_leases");

    var idArray = ids is Guid[] arr ? arr : [.. ids];

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_category, @p_ids, @p_lease)";
    cmd.Parameters.Add(new NpgsqlParameter("p_category", NpgsqlTypes.NpgsqlDbType.Text) { Value = category.ToSqlCategory() });
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_lease", NpgsqlTypes.NpgsqlDbType.Integer) { Value = leaseSeconds });
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Hands back inbox rows claimed but never dispatched, refunding the optimistic claim attempt
  /// (see <see cref="IWorkCoordinator.ReleaseUnprocessedInboxAsync"/>).
  /// </summary>
  /// <docs>fundamentals/work-coordinator/batched-flushers</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxGracefulReleaseSqlTests.cs</tests>
  public async Task<int> ReleaseUnprocessedInboxAsync(
    Guid instanceId,
    IReadOnlyList<Guid> messageIds,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(messageIds);
    if (messageIds.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "release_unprocessed_inbox");

    var idArray = messageIds is Guid[] arr ? arr : [.. messageIds];

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_instance, @p_ids)";
    cmd.Parameters.Add(new NpgsqlParameter("p_instance", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Stream-integrity R1a selection (see <see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/>).
  /// Joins the event body so reaped ephemeral events are excluded structurally, filters
  /// at-most-once occurrences by their envelope-metadata delivery guarantee, and returns rows
  /// ordered (stream, version) under a hard LIMIT.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SelectRedeliveryEventsTests.cs</tests>
  public async Task<IReadOnlyList<RedeliveryEvent>> SelectRedeliveryEventsAsync(
    RedeliveryRequest request,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);

    var sql = $"""
      SELECT es.event_id, es.stream_id, es.version::bigint, es.commit_sequence,
             es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text,
             COALESCE(es.flags, 0)
      FROM {schema}.wh_event_store es
      JOIN {schema}.wh_event_body eb ON eb.event_id = es.event_id
      WHERE (@p_tenant IS NULL OR es.scope->>'t' = @p_tenant)
        AND ((@p_types::text[]) IS NULL OR es.event_type IN (SELECT {BuildSchemaQualifiedName(schema, "normalize_event_type")}(t) FROM unnest(@p_types::text[]) AS t))
        AND ((@p_streams::uuid[]) IS NULL OR es.stream_id = ANY(@p_streams::uuid[]))
        AND (@p_from_seq::bigint IS NULL OR es.commit_sequence > @p_from_seq)
        AND (@p_to_seq::bigint IS NULL OR es.commit_sequence <= @p_to_seq)
        AND ((@p_after_stream::uuid) IS NULL OR (es.stream_id, es.version) > (@p_after_stream::uuid, @p_after_version::bigint))
        AND COALESCE((eb.metadata->>'deliveryGuarantee')::integer, 0) <> 1
      ORDER BY es.stream_id, es.version
      LIMIT @p_max
      """;

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_tenant", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)request.TenantScope ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = request.EventTypes is null ? DBNull.Value : request.EventTypes.ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_streams", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = request.StreamIds is null ? DBNull.Value : request.StreamIds.ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_from_seq", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)request.FromCommitSequence ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_to_seq", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)request.ToCommitSequence ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_after_stream", NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = (object?)request.AfterStreamId ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_after_version", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)request.AfterVersion ?? 0L
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_max", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = request.MaxEvents
    });

    var results = new List<RedeliveryEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new RedeliveryEvent {
        EventId = reader.GetGuid(0),
        StreamId = reader.GetGuid(1),
        Version = reader.GetInt64(2),
        CommitSequence = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        EventType = reader.GetString(4),
        EventData = reader.GetString(5),
        Metadata = reader.IsDBNull(6) ? null : reader.GetString(6),
        Scope = reader.IsDBNull(7) ? null : reader.GetString(7),
        Flags = reader.GetInt32(8)
      });
    }
    return results;
  }

  /// <summary>
  /// First-instance-wins claim for one integrity-audit cycle: an atomic settings CAS (the deep
  /// prune's watermark pattern) on <c>integrity_audit_last_run</c>. One statement — INSERT the
  /// watermark or UPDATE it only when older than the window — so racing replicas resolve at the
  /// row lock: exactly one sees a row affected and runs the cycle.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityAuditClaimTests.cs</tests>
  public Task<bool> TryClaimIntegrityAuditCycleAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      "integrity_audit_last_run",
      "Last claimed integrity-audit cycle — first instance to CAS this watermark runs the cycle; siblings skip.",
      claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimTypeDefinitionReconcileAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      "type_definition_reconcile_last_run",
      "Last claimed type-definition reconcile — first instance to CAS this watermark walks the catalog; siblings skip.",
      claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task RecordOffloadClaimAsync(
      string storageKey, string providerName, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var ledger = BuildSchemaQualifiedName(schema, "wh_offload_claims");
#pragma warning disable S2077 // identifier from BuildSchemaQualifiedName; all values are @parameters
      cmd.CommandText =
        $"INSERT INTO {ledger} (storage_key, provider_name) VALUES (@k, @p) " +
        "ON CONFLICT (storage_key) DO NOTHING";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("k", storageKey));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p", providerName));
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<OffloadClaimRecord>> GetExpiredOffloadClaimsAsync(
      TimeSpan olderThan, int batchSize, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<IReadOnlyList<OffloadClaimRecord>>(async (cmd, schema) => {
      var ledger = BuildSchemaQualifiedName(schema, "wh_offload_claims");
#pragma warning disable S2077
      // Age evaluated against the DB clock at query time — a changed window is retroactive over
      // every existing blob; nothing is stamped per blob.
      cmd.CommandText =
        $"SELECT storage_key, provider_name FROM {ledger} " +
        "WHERE uploaded_at < NOW() - make_interval(secs => @s) " +
        "ORDER BY uploaded_at LIMIT @n";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("s", olderThan.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("n", batchSize));
      var results = new List<OffloadClaimRecord>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        results.Add(new OffloadClaimRecord(reader.GetString(0), reader.GetString(1)));
      }
      return results;
    }, cancellationToken);

  /// <inheritdoc />
  public Task RemoveOffloadClaimsAsync(
      IReadOnlyCollection<string> storageKeys, CancellationToken cancellationToken = default) {
    if (storageKeys.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var ledger = BuildSchemaQualifiedName(schema, "wh_offload_claims");
#pragma warning disable S2077
      cmd.CommandText = $"DELETE FROM {ledger} WHERE storage_key = ANY(@keys)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("keys",
        NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = storageKeys.ToArray()
      });
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<bool> TryClaimOffloadSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      "offload_claim_sweep_last_run",
      "Last claimed offload-claim sweep — first instance to CAS this watermark drains the expired ledger; siblings skip.",
      claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
      IReadOnlyCollection<string> clrTypeNames, int perTableLimit = 500, CancellationToken cancellationToken = default) {
    if (clrTypeNames.Count == 0) {
      return Task.FromResult<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>([]);
    }
    return _withCoordinatorCommandAsync<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>(
      async (cmd, schema) => {
        var fn = BuildSchemaQualifiedName(schema, "collect_perspective_row_reap_targets");
#pragma warning disable S2077 // identifier from BuildSchemaQualifiedName; all values are @parameters
        cmd.CommandText =
          $"SELECT o_clr_type_name, o_table_name, o_row_id, o_scope, o_data, o_reason FROM {fn}(@names, @lim)";
#pragma warning restore S2077
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("names",
          NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = clrTypeNames.ToArray() });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("lim", perTableLimit));
        var targets = new List<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          System.Text.Json.JsonElement? scope = null;
          if (!await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)) {
            using var scopeDoc = System.Text.Json.JsonDocument.Parse(reader.GetString(3));
            scope = scopeDoc.RootElement.Clone();
          }
          using var dataDoc = System.Text.Json.JsonDocument.Parse(reader.GetString(4));
          targets.Add(new Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget(
            reader.GetString(0), reader.GetString(1), reader.GetGuid(2),
            scope, dataDoc.RootElement.Clone(), reader.GetString(5)));
        }
        return targets;
      }, cancellationToken);
  }

  /// <inheritdoc />
  public Task HoldPerspectiveRowDestructionAsync(
      IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
      DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var hold = BuildSchemaQualifiedName(schema, "wh_perspective_row_hold");
#pragma warning disable S2077
      cmd.CommandText =
        $"INSERT INTO {hold} (table_name, row_id, hold_until) " +
        "SELECT u.t, u.r, @until FROM unnest(@tables, @ids) AS u(t, r) " +
        "ON CONFLICT (table_name, row_id) DO UPDATE SET hold_until = EXCLUDED.hold_until";
#pragma warning restore S2077
      _addRowRefParameters(cmd, rows);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("until", holdUntil));
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task ReleasePerspectiveRowHoldsAsync(
      IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
      CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var hold = BuildSchemaQualifiedName(schema, "wh_perspective_row_hold");
#pragma warning disable S2077
      cmd.CommandText =
        $"DELETE FROM {hold} h USING unnest(@tables, @ids) AS u(t, r) " +
        "WHERE h.table_name = u.t AND h.row_id = u.r";
#pragma warning restore S2077
      _addRowRefParameters(cmd, rows);
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> RecordPerspectiveRowDestructionFailureAsync(
      IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
      TimeSpan retryBackoff, int maxRetries, Whizbang.Core.Lifecycle.OnDestroyFailure onDestroyFailure,
      CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.FromResult(0);
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var hold = BuildSchemaQualifiedName(schema, "wh_perspective_row_hold");
#pragma warning disable S2077
      // The row-shaped destruction retry ladder (the E2-5 semantics): under the cap, hold for the
      // backoff and re-offer; past it, the policy decides — '-infinity' = no active hold = the next
      // sweep takes the row (forced delete); 'infinity' = keep forever (the explicit leak-risk
      // choice). ForceDeleteImmediately short-circuits on the first failure.
      cmd.CommandText =
        $"INSERT INTO {hold} AS h (table_name, row_id, hold_until, failure_count) " +
        "SELECT u.t, u.r, " +
        "  CASE WHEN @policy = 2 THEN '-infinity'::timestamptz " +
        "       ELSE NOW() + make_interval(secs => @backoff) END, 1 " +
        "FROM unnest(@tables, @ids) AS u(t, r) " +
        "ON CONFLICT (table_name, row_id) DO UPDATE SET " +
        "  failure_count = h.failure_count + 1, " +
        "  hold_until = CASE " +
        "    WHEN @policy = 2 THEN '-infinity'::timestamptz " +
        "    WHEN h.failure_count + 1 > @max THEN " +
        "      CASE WHEN @policy = 1 THEN 'infinity'::timestamptz ELSE '-infinity'::timestamptz END " +
        "    ELSE NOW() + make_interval(secs => @backoff) END " +
        "RETURNING failure_count";
#pragma warning restore S2077
      _addRowRefParameters(cmd, rows);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("backoff", retryBackoff.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("max", maxRetries));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("policy", (int)onDestroyFailure));
      var highest = 0;
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        highest = Math.Max(highest, reader.GetInt32(0));
      }
      return highest;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(
      int batchSize = 5000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var fn = BuildSchemaQualifiedName(schema, "reap_enrolled_perspective_rows");
#pragma warning disable S2077
      cmd.CommandText = $"SELECT rows_affected, status FROM {fn}(@batch)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("batch", batchSize));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
        ? new PerspectiveRowReapResult(reader.GetInt32(0), reader.GetString(1))
        : new PerspectiveRowReapResult(0, "no result");
    }, cancellationToken);

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapPerspectiveRowCapsAsync(
      int batchSize = 5000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var fn = BuildSchemaQualifiedName(schema, "reap_perspective_row_caps");
#pragma warning disable S2077
      cmd.CommandText = $"SELECT rows_affected, status FROM {fn}(@batch)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("batch", batchSize));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
        ? new PerspectiveRowReapResult(reader.GetInt32(0), reader.GetString(1))
        : new PerspectiveRowReapResult(0, "no result");
    }, cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimRowCapSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      "row_cap_sweep_last_run",
      "Last claimed cap sweep — first instance to CAS this watermark runs the ranking eviction; siblings skip.",
      claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task AcknowledgeRetentionEnforcementAsync(
      string clrTypeName, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var registry = BuildSchemaQualifiedName(schema, "wh_perspective_registry");
#pragma warning disable S2077
      cmd.CommandText =
        $"UPDATE {registry} SET retention_enforcement_acknowledged = TRUE WHERE clr_type_name = @c";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("c", clrTypeName));
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<long> CountPerspectiveRetentionBacklogAsync(
      string clrTypeName, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var fn = BuildSchemaQualifiedName(schema, "count_perspective_retention_backlog");
#pragma warning disable S2077
      cmd.CommandText = $"SELECT {fn}(@c)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("c", clrTypeName));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is long count ? count : 0L;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowRef>> DrainRowEvictionJournalAsync(
      int limit = 1000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowRef>>(async (cmd, schema) => {
      var journal = BuildSchemaQualifiedName(schema, "wh_row_eviction_journal");
#pragma warning disable S2077
      // DELETE ... RETURNING is the atomic claim: whichever instance drains an entry owns its cascade.
      cmd.CommandText =
        $"DELETE FROM {journal} WHERE ctid IN (SELECT ctid FROM {journal} ORDER BY evicted_at LIMIT @n) " +
        "RETURNING table_name, row_id";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("n", limit));
      var drained = new List<Whizbang.Core.Lifecycle.PerspectiveRowRef>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        drained.Add(new Whizbang.Core.Lifecycle.PerspectiveRowRef(reader.GetString(0), reader.GetGuid(1)));
      }
      return drained;
    }, cancellationToken);

  /// <inheritdoc />
  public Task RequeueRowEvictionsAsync(
      IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
      CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var journal = BuildSchemaQualifiedName(schema, "wh_row_eviction_journal");
#pragma warning disable S2077
      cmd.CommandText =
        $"INSERT INTO {journal} (table_name, row_id) SELECT u.t, u.r FROM unnest(@tables, @ids) AS u(t, r) " +
        "ON CONFLICT (table_name, row_id) DO NOTHING";
#pragma warning restore S2077
      _addRowRefParameters(cmd, rows);
      await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
      IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken = default) {
    if (clrTypeNames.Count == 0) {
      return Task.FromResult<IReadOnlyList<PerspectiveTableName>>([]);
    }
    return _withCoordinatorCommandAsync<IReadOnlyList<PerspectiveTableName>>(async (cmd, schema) => {
      var registry = BuildSchemaQualifiedName(schema, "wh_perspective_registry");
#pragma warning disable S2077
      cmd.CommandText =
        $"SELECT clr_type_name, table_name FROM {registry} WHERE clr_type_name = ANY(@names)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("names",
        NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = clrTypeNames.ToArray() });
      var names = new List<PerspectiveTableName>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        names.Add(new PerspectiveTableName(reader.GetString(0), reader.GetString(1)));
      }
      return names;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>> GetPerspectiveRowsByIdsAsync(
      string clrTypeName, string tableName, IReadOnlyCollection<Guid> rowIds,
      CancellationToken cancellationToken = default) {
    if (rowIds.Count == 0) {
      return Task.FromResult<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>([]);
    }
    return _withCoordinatorCommandAsync<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>(
      async (cmd, schema) => {
#pragma warning disable S2077 // table identifier originates from wh_perspective_registry, not user input
        cmd.CommandText =
          $"SELECT id, scope, data FROM {BuildSchemaQualifiedName(schema, tableName)} WHERE id = ANY(@ids)";
#pragma warning restore S2077
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("ids",
          NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = rowIds.ToArray() });
        var targets = new List<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          System.Text.Json.JsonElement? scope = null;
          if (!await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)) {
            using var scopeDoc = System.Text.Json.JsonDocument.Parse(reader.GetString(1));
            scope = scopeDoc.RootElement.Clone();
          }
          using var dataDoc = System.Text.Json.JsonDocument.Parse(reader.GetString(2));
          targets.Add(new Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget(
            clrTypeName, tableName, reader.GetGuid(0), scope, dataDoc.RootElement.Clone(), "cascade"));
        }
        return targets;
      }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> CascadeDeletePerspectiveRowsAsync(
      string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default) {
    if (rowIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var fn = BuildSchemaQualifiedName(schema, "cascade_delete_perspective_rows");
#pragma warning disable S2077
      cmd.CommandText = $"SELECT {fn}(@t, @ids)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("t", tableName));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("ids",
        NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = rowIds.ToArray() });
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is int deleted ? deleted : 0;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> FoldSettledApplyPathsAsync(
      TimeSpan idleWindow, int limit = 1000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var fn = BuildSchemaQualifiedName(schema, "fold_settled_apply_paths");
#pragma warning disable S2077
      cmd.CommandText = $"SELECT {fn}(@idle, @lim)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("idle", (long)idleWindow.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("lim", limit));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is int folded ? folded : 0;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimSettledFoldSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      "settled_fold_last_run",
      "Last claimed settled apply-path fold — first instance to CAS this watermark folds idle streams; siblings skip.",
      claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task<int> FoldStreamApplyPathsAsync(
      IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) {
    if (streamIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var fn = BuildSchemaQualifiedName(schema, "fold_stream_apply_paths");
#pragma warning disable S2077
      cmd.CommandText = $"SELECT {fn}(@ids)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("ids",
        NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamIds.ToArray() });
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is int folded ? folded : 0;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> ReconcileFollowerPresenceAsync(
      string followerTable, IReadOnlyCollection<string> announcerTables,
      CancellationToken cancellationToken = default) {
    if (announcerTables.Count == 0) {
      return Task.FromResult(0);
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      var follower = BuildSchemaQualifiedName(schema, followerTable);
      var hold = BuildSchemaQualifiedName(schema, "wh_perspective_row_hold");
      var absentFromEveryAnnouncer = string.Join(" AND ", announcerTables.Select(a =>
        $"NOT EXISTS (SELECT 1 FROM {BuildSchemaQualifiedName(schema, a)} a WHERE a.id = f.id)"));
#pragma warning disable S2077 // identifiers originate from wh_perspective_registry, not user input
      cmd.CommandText =
        $"DELETE FROM {follower} f WHERE {absentFromEveryAnnouncer} " +
        $"AND NOT EXISTS (SELECT 1 FROM {hold} h WHERE h.table_name = @t AND h.row_id = f.id AND h.hold_until > NOW())";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("t", followerTable));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  private static void _addRowRefParameters(
      System.Data.Common.DbCommand cmd, IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows) {
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("tables",
      NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = rows.Select(r => r.TableName).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("ids",
      NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = rows.Select(r => r.RowId).ToArray()
    });
  }

  /// <summary>
  /// The one-per-service claim: compare-and-swap a timestamp watermark in wh_settings. The UPDATE
  /// only fires when the stored instant is older than the window, so exactly one instance wins and
  /// the losers see zero rows affected. Shared by every piece of work that must happen once per
  /// service per window rather than once per replica.
  /// </summary>
  private async Task<bool> _tryClaimWatermarkAsync(
      string settingKey,
      string description,
      TimeSpan claimWindow,
      CancellationToken cancellationToken) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var settings = BuildSchemaQualifiedName(schema, "wh_settings");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      INSERT INTO {settings} (setting_key, setting_value, value_type, description)
      VALUES (@p_key, NOW()::text, 'timestamptz', @p_description)
      ON CONFLICT (setting_key) DO UPDATE
        SET setting_value = NOW()::text, updated_at = NOW()
        WHERE ({settings}.setting_value)::timestamptz <= NOW() - @p_window
      """;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_key", settingKey));
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_description", description));
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_window", NpgsqlTypes.NpgsqlDbType.Interval) {
      Value = claimWindow
    });
    var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    return affected > 0;
  }


  /// <inheritdoc />
  public async Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    const string WATERMARK_KEY = "integrity_checkpoint_watermark";
    var settings = BuildSchemaQualifiedName(schema, "wh_settings");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;

    // Head watermark = the highest STAMPED sequence (the async stamper's barrier guarantees
    // everything at/below it is committed and stable), plus the previously advanced watermark.
    long current;
    long? prior;
    await using (var read = conn.CreateCommand()) {
      read.CommandText =
        $"SELECT COALESCE((SELECT MAX(commit_sequence) FROM {schema}.wh_event_store), 0), " +
        $"       (SELECT setting_value FROM {settings} WHERE setting_key = @p_key)";
      read.Parameters.Add(new Npgsql.NpgsqlParameter("p_key", WATERMARK_KEY));
      await using var reader = await read.ExecuteReaderAsync(cancellationToken);
      await reader.ReadAsync(cancellationToken);
      current = reader.GetInt64(0);
      prior = reader.IsDBNull(1)
        ? null
        : long.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
    }

    if (prior is null) {
      // First run: BASELINE at the current head without counting history — a fresh consumer set
      // has nothing to compare retroactive counts against, and a startup count storm helps no one.
      await using var init = conn.CreateCommand();
      init.CommandText =
        $"INSERT INTO {settings} (setting_key, setting_value, value_type, description) " +
        "VALUES (@p_key, @p_value, 'integer', 'Stream-integrity checkpoint watermark (highest commit_sequence already checkpointed)') " +
        "ON CONFLICT DO NOTHING";
      init.Parameters.Add(new Npgsql.NpgsqlParameter("p_key", WATERMARK_KEY));
      init.Parameters.Add(new Npgsql.NpgsqlParameter("p_value", current.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      var inserted = await init.ExecuteNonQueryAsync(cancellationToken);
      return inserted == 1
        ? new IntegrityCheckpointWindow { FromCommitSequence = current, ToCommitSequence = current }
        : null;   // another instance baselined first — it owns this window
    }

    if (current < prior.Value) {
      current = prior.Value;   // the watermark never regresses
    }

    // Optimistic advance: exactly one instance wins each window; losers skip the cycle. An
    // unchanged watermark (quiet window) still "wins" — the empty checkpoint is the liveness beat.
    await using (var cas = conn.CreateCommand()) {
      cas.CommandText =
        $"UPDATE {settings} SET setting_value = @p_new WHERE setting_key = @p_key AND setting_value = @p_old";
      cas.Parameters.Add(new Npgsql.NpgsqlParameter("p_new", current.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      cas.Parameters.Add(new Npgsql.NpgsqlParameter("p_key", WATERMARK_KEY));
      cas.Parameters.Add(new Npgsql.NpgsqlParameter("p_old", prior.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      if (await cas.ExecuteNonQueryAsync(cancellationToken) != 1) {
        return null;   // lost the advance race
      }
    }

    var buckets = new List<CheckpointBucket>();
    if (current > prior.Value) {
      // At-most-once occurrences are excluded (non-delivery is their declared behavior, not a
      // gap); checkpoints never count themselves. Reaped ephemeral bodies LEFT-JOIN to null
      // metadata and stay INCLUDED — the consumer received them live and counts them too.
      await using var count = conn.CreateCommand();
      count.CommandText = $"""
        SELECT COALESCE(es.scope->>'t', '') AS tenant, es.event_type, COUNT(*)::int
        FROM {schema}.wh_event_store es
        LEFT JOIN {schema}.wh_event_body eb ON eb.event_id = es.event_id
        WHERE es.commit_sequence > @p_from AND es.commit_sequence <= @p_to
          AND COALESCE((eb.metadata->>'deliveryGuarantee')::integer, 0) <> 1
          AND es.event_type <> @p_checkpoint_type
        GROUP BY 1, 2
        ORDER BY 1, 2
        """;
      count.Parameters.Add(new Npgsql.NpgsqlParameter("p_from", prior.Value));
      count.Parameters.Add(new Npgsql.NpgsqlParameter("p_to", current));
      count.Parameters.Add(new Npgsql.NpgsqlParameter("p_checkpoint_type",
        TypeNameFormatter.Format(typeof(IntegrityCheckpoint))));
      await using var reader = await count.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        var tenant = reader.GetString(0);
        buckets.Add(new CheckpointBucket {
          TenantScope = tenant.Length == 0 ? null : tenant,
          EventType = reader.GetString(1),
          Count = reader.GetInt32(2)
        });
      }
    }

    return new IntegrityCheckpointWindow {
      FromCommitSequence = prior.Value,
      ToCommitSequence = current,
      Buckets = buckets
    };
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
    Guid originServiceId,
    long fromCommitSequence,
    long toCommitSequence,
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    // Received events persist the ORIGIN identity (1:1 forward stamping) — the consumer's half of
    // a checkpoint comparison counts by it, windowed on the ORIGIN's commit sequence.
    cmd.CommandText = $"""
      SELECT COALESCE(es.scope->>'t', '') AS tenant, es.event_type, COUNT(*)::int
      FROM {schema}.wh_event_store es
      WHERE es.origin_service_id = @p_origin
        AND es.origin_commit_sequence > @p_from AND es.origin_commit_sequence <= @p_to
      GROUP BY 1, 2
      ORDER BY 1, 2
      """;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_from", fromCommitSequence));
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_to", toCommitSequence));

    var results = new List<CheckpointBucket>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      var tenant = reader.GetString(0);
      results.Add(new CheckpointBucket {
        TenantScope = tenant.Length == 0 ? null : tenant,
        EventType = reader.GetString(1),
        Count = reader.GetInt32(2)
      });
    }
    return results;
  }

  /// <summary>
  /// Shared acquire for the raw-SQL coordinator paths: throughput gate → resolved schema →
  /// dedicated coordinator connection scope → command. The operation receives the command and
  /// the schema; every resource disposes when it completes.
  /// </summary>
  private async Task<T> _withCoordinatorCommandAsync<T>(
      Func<System.Data.Common.DbCommand, string, Task<T>> operation,
      CancellationToken cancellationToken) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCommand();
    // Every query on this path is integrity/audit machinery (digests, coverage gaps, registered
    // types) — best-effort maintenance that retries next cycle. Bound it so a degraded or very
    // large store times a cycle out instead of holding the host's resources until the liveness
    // probe kills the process.
    cmd.CommandTimeout = 120;
    return await operation(cmd, schema).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<ConsumedTypeRegistration>> GetConsumedTypeRegistrationsAsync(
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<IReadOnlyList<ConsumedTypeRegistration>>(async (cmd, schema) => {
      cmd.CommandText = $"SELECT event_type, backfill_status FROM {schema}.wh_consumed_types ORDER BY event_type";

      var results = new List<ConsumedTypeRegistration>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        results.Add(new ConsumedTypeRegistration {
          EventType = reader.GetString(0),
          Status = (ConsumedTypeBackfillStatus)reader.GetInt16(1)
        });
      }
      return results;
    }, cancellationToken);

  /// <inheritdoc />
  public async Task RegisterConsumedTypesAsync(
    IReadOnlyList<string> eventTypes,
    bool asBaseline,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    if (eventTypes.Count == 0) {
      return;
    }
    await _withCoordinatorCommandAsync(async (cmd, schema) => {
      // ON CONFLICT DO NOTHING: idempotent + multi-instance safe — the first booting instance wins
      // each row; a row already registered (any status) is never demoted or re-pended.
      cmd.CommandText =
        $"INSERT INTO {schema}.wh_consumed_types (event_type, backfill_status) " +
        "SELECT t, @p_status FROM unnest(@p_types::text[]) AS t " +
        "ON CONFLICT (event_type) DO NOTHING";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_status", NpgsqlTypes.NpgsqlDbType.Smallint) {
        Value = (short)(asBaseline ? ConsumedTypeBackfillStatus.Baseline : ConsumedTypeBackfillStatus.Pending)
      });
      return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task MarkConsumedTypeBackfillRequestedAsync(
    IReadOnlyList<string> eventTypes,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    if (eventTypes.Count == 0) {
      return;
    }
    await _withCoordinatorCommandAsync(async (cmd, schema) => {
      // Only Pending rows transition — Baseline never backfills, Requested never re-stamps.
      cmd.CommandText =
        $"UPDATE {schema}.wh_consumed_types SET backfill_status = @p_requested, backfill_requested_at = NOW() " +
        "WHERE event_type = ANY(@p_types::text[]) AND backfill_status = @p_pending";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_requested", NpgsqlTypes.NpgsqlDbType.Smallint) {
        Value = (short)ConsumedTypeBackfillStatus.Requested
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_pending", NpgsqlTypes.NpgsqlDbType.Smallint) {
        Value = (short)ConsumedTypeBackfillStatus.Pending
      });
      return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<StreamDigest>> ComputeTypeDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    TimeSpan settleWindow,
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // The type-level roll-up happens AT THE STORE, and (since #80-E) it is served FROM THE
      // EPOCHS: sealed history composes by XOR of immutable wh_digest_epochs rows and only the
      // open window above the closure frontier folds live, so the answer costs O(open window)
      // instead of O(everything ever stored). Sealed rows are authoritative here — a per-answer
      // re-verification would re-buy the full-scan cost the epochs exist to end; the scheduled
      // self-sweep owns detecting a bad seal. A lane with no closed epochs degrades to the plain
      // full fold, bit-identical to rolling the per-stream compute up in C#.
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        "SELECT tenant, event_type, digest_lo, digest_hi, event_count " +
        $"FROM {BuildSchemaQualifiedName(schema, "compute_type_digests_epoch")}(@p_origin, @p_types, NOW(), @p_settle)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)originServiceId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes is null ? DBNull.Value : eventTypes.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", (int)settleWindow.TotalSeconds));

      var results = new List<StreamDigest>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var tenant = reader.GetString(0);
        results.Add(new StreamDigest {
          TenantScope = tenant.Length == 0 ? null : tenant,
          EventType = reader.GetString(1),
          StreamId = Guid.Empty,
          DigestLo = reader.GetInt64(2),
          DigestHi = reader.GetInt64(3),
          EventCount = reader.GetInt32(4),
          UpdatedAt = null,
        });
      }
      return (IReadOnlyList<StreamDigest>)results;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<StreamDigest>> ComputeStreamDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    TimeSpan settleWindow,
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // Two-lane 64-bit XOR of hashtextextended(event_id, seed) — order-independent, self-inverse
      // (deleted rows simply stop contributing; no subtraction bookkeeping). Origin flavor
      // (@p_origin NULL) folds LOCALLY-ORIGINATED rows — what this service publishes; consumer
      // flavor folds rows RECEIVED from that origin. Ephemeral (mode-excluded) and at-most-once
      // occurrences are excluded, matching Phase B. The settle window keeps in-flight deliveries
      // out of both sides' digests.
      cmd.CommandText = $"""
        SELECT COALESCE(es.scope->>'t', '') AS tenant, es.event_type, es.stream_id,
               bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
               bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
               COUNT(*)::int
        FROM {schema}.wh_event_store es
        LEFT JOIN {schema}.wh_event_body eb ON eb.event_id = es.event_id
        WHERE ((@p_origin::uuid) IS NULL AND es.origin_service_id IS NULL
               OR es.origin_service_id = @p_origin)
          AND ((@p_types::text[]) IS NULL OR es.event_type IN (SELECT {BuildSchemaQualifiedName(schema, "normalize_event_type")}(t) FROM unnest(@p_types::text[]) AS t))
          AND COALESCE(es.flags, 0) & 8 = 0
          AND COALESCE((eb.metadata->>'deliveryGuarantee')::integer, 0) <> 1
          AND es.created_at < NOW() - @p_settle::interval
        GROUP BY 1, 2, 3
        ORDER BY 1, 2, 3
        """;
      _addDigestFilterParams(cmd, originServiceId, eventTypes);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", $"{(int)settleWindow.TotalSeconds} seconds"));

      return await _readStreamDigestsAsync(cmd, hasUpdatedAt: false, typeLevel: false, cancellationToken)
        .ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveCoverageGap>> GetPerspectiveCoverageGapsAsync(
    TimeSpan settleWindow,
    int maxGaps,
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<IReadOnlyList<PerspectiveCoverageGap>>(async (cmd, schema) => {
      // A gap = settled non-ephemeral events + a registered perspective association + NO cursor for
      // that (stream, perspective) + no pending work item — the pipeline is not on it, and never was.
      cmd.CommandText = $"""
        SELECT es.stream_id, ma.target_name, COUNT(*)::int
        FROM {schema}.wh_event_store es
        JOIN {schema}.wh_message_associations ma
          ON ma.normalized_message_type = es.event_type AND ma.association_type = 'perspective'
        WHERE es.created_at < NOW() - @p_settle::interval
          AND COALESCE(es.flags, 0) & 8 = 0
          AND NOT EXISTS (
            SELECT 1 FROM {schema}.wh_perspective_cursors c
            WHERE c.stream_id = es.stream_id AND c.perspective_name = ma.target_name)
          AND NOT EXISTS (
            SELECT 1 FROM {schema}.wh_perspective_events pe
            WHERE pe.event_id = es.event_id AND pe.processed_at IS NULL)
        GROUP BY 1, 2
        ORDER BY 1, 2
        LIMIT @p_max
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", $"{(int)settleWindow.TotalSeconds} seconds"));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_max", Math.Max(1, maxGaps)));

      var results = new List<PerspectiveCoverageGap>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        results.Add(new PerspectiveCoverageGap {
          StreamId = reader.GetGuid(0),
          PerspectiveName = reader.GetString(1),
          EventCount = reader.GetInt32(2)
        });
      }
      return results;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<IReadOnlyList<string>>(async (cmd, schema) => {
      // The zero-guid lane holds this service's OWN emissions — its distinct types are the topics
      // the checkpoint heartbeat must cover even when the current window is quiet.
      cmd.CommandText = $"""
        SELECT DISTINCT event_type
        FROM {schema}.wh_stream_digests
        WHERE origin_service_id = '{ZERO_ORIGIN_UUID}'::uuid
        ORDER BY 1
        """;

      var results = new List<string>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        results.Add(reader.GetString(0));
      }
      return results;
    }, cancellationToken);

  private const string ZERO_ORIGIN_UUID = "00000000-0000-0000-0000-000000000000";

  /// <inheritdoc />
  public Task<IReadOnlyList<StreamDigest>> GetStreamDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // A1c: the incrementally-maintained buckets — a plain indexed read (PK prefix), no recompute.
      // Requested names normalize to the stored wire form so a long-AQN caller still matches.
      var normalizeFn = BuildSchemaQualifiedName(schema, "normalize_event_type");
      cmd.CommandText = $"""
        SELECT scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at
        FROM {schema}.wh_stream_digests
        WHERE origin_service_id = COALESCE(@p_origin::uuid, '{ZERO_ORIGIN_UUID}'::uuid)
          AND ((@p_types::text[]) IS NULL OR event_type IN (SELECT {normalizeFn}(t) FROM unnest(@p_types::text[]) AS t))
        ORDER BY 1, 2, 3
        """;
      _addDigestFilterParams(cmd, originServiceId, eventTypes);

      return await _readStreamDigestsAsync(cmd, hasUpdatedAt: true, typeLevel: false, cancellationToken)
        .ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<StreamDigest>> GetTypeDigestsAsync(
    Guid? originServiceId,
    IReadOnlyList<string>? eventTypes,
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // A1c: the per-(tenant, type) roll-up — XOR of the type's stream buckets equals folding every
      // event of the type, because the buckets partition them. MAX(updated_at) drives settle-skip.
      // Requested names normalize to the stored wire form so a long-AQN caller still matches.
      var normalizeFn = BuildSchemaQualifiedName(schema, "normalize_event_type");
      cmd.CommandText = $"""
        SELECT scope_tenant, event_type, bit_xor(digest_lo), bit_xor(digest_hi),
               SUM(event_count)::int, MAX(updated_at)
        FROM {schema}.wh_stream_digests
        WHERE origin_service_id = COALESCE(@p_origin::uuid, '{ZERO_ORIGIN_UUID}'::uuid)
          AND ((@p_types::text[]) IS NULL OR event_type IN (SELECT {normalizeFn}(t) FROM unnest(@p_types::text[]) AS t))
        GROUP BY 1, 2
        ORDER BY 1, 2
        """;
      _addDigestFilterParams(cmd, originServiceId, eventTypes);

      return await _readStreamDigestsAsync(cmd, hasUpdatedAt: true, typeLevel: true, cancellationToken)
        .ConfigureAwait(false);
    }, cancellationToken);

  /// <summary>Materializes digest rows. Stream-level column order is (tenant, type, stream, lo,
  /// hi, count[, updated]); type-level omits the stream column and carries <see cref="Guid.Empty"/>.
  /// Recomputed reads have no update time.</summary>
  private static async Task<IReadOnlyList<StreamDigest>> _readStreamDigestsAsync(
      System.Data.Common.DbCommand cmd, bool hasUpdatedAt, bool typeLevel, CancellationToken cancellationToken) {
    var results = new List<StreamDigest>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    var idx = typeLevel ? 2 : 3;
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var tenant = reader.GetString(0);
      results.Add(new StreamDigest {
        TenantScope = tenant.Length == 0 ? null : tenant,
        EventType = reader.GetString(1),
        StreamId = typeLevel ? Guid.Empty : reader.GetGuid(2),
        DigestLo = reader.GetInt64(idx),
        DigestHi = reader.GetInt64(idx + 1),
        EventCount = reader.GetInt32(idx + 2),
        UpdatedAt = hasUpdatedAt ? reader.GetFieldValue<DateTimeOffset>(idx + 3) : null,
      });
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<DigestVerificationResult> VerifyDigestTableAsync(
    TimeSpan settleWindow,
    CancellationToken cancellationToken = default) {
    return await _withCoordinatorCommandAsync(async (cmd, schema) => {
      _prepareVerifyDigestCommand(cmd, schema, settleWindow);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      await reader.ReadAsync(cancellationToken);
      return new DigestVerificationResult {
        BucketsChecked = reader.GetInt32(0),
        DriftUpdated = reader.GetInt32(1),
        DriftRemoved = reader.GetInt32(2),
        DriftAdded = reader.GetInt32(3),
      };
    }, cancellationToken).ConfigureAwait(false);
  }

  private static void _prepareVerifyDigestCommand(
      System.Data.Common.DbCommand cmd, string schema, TimeSpan settleWindow) {
    // A1c trust-but-verify: one statement, one snapshot — recompute settled buckets from the event
    // store and heal the table three ways (update drifted / delete phantom / insert missing). The
    // settle gates (bucket updated_at, event created_at) keep in-flight folds out of both sides:
    // a bucket touched inside the window is skipped this pass, and a fresh event with no bucket is
    // not "missing" — it simply hasn't settled. Data-modifying CTEs share the statement snapshot;
    // the three heal sets are disjoint by construction, so ordering between them is immaterial.
    cmd.CommandText = $"""
      WITH recomputed AS (
        SELECT COALESCE(es.origin_service_id, '{ZERO_ORIGIN_UUID}'::uuid) AS origin_service_id,
               COALESCE(es.scope->>'t', '') AS scope_tenant, es.event_type, es.stream_id,
               bit_xor(hashtextextended(es.event_id::text, 0)) AS digest_lo,
               bit_xor(hashtextextended(es.event_id::text, 1)) AS digest_hi,
               COUNT(*)::int AS event_count
        FROM {schema}.wh_event_store es
        LEFT JOIN {schema}.wh_event_body eb ON eb.event_id = es.event_id
        WHERE COALESCE(es.flags, 0) & 8 = 0
          AND COALESCE((eb.metadata->>'deliveryGuarantee')::integer, 0) <> 1
          AND es.created_at < NOW() - @p_settle::interval
        GROUP BY 1, 2, 3, 4
      ),
      drift_updated AS (
        UPDATE {schema}.wh_stream_digests d
        SET digest_lo = r.digest_lo, digest_hi = r.digest_hi, event_count = r.event_count, updated_at = NOW()
        FROM recomputed r
        WHERE d.origin_service_id = r.origin_service_id AND d.scope_tenant = r.scope_tenant
          AND d.event_type = r.event_type AND d.stream_id = r.stream_id
          AND d.updated_at < NOW() - @p_settle::interval
          AND (d.digest_lo <> r.digest_lo OR d.digest_hi <> r.digest_hi OR d.event_count <> r.event_count)
        RETURNING 1
      ),
      drift_removed AS (
        DELETE FROM {schema}.wh_stream_digests d
        WHERE d.updated_at < NOW() - @p_settle::interval
          AND NOT EXISTS (
            SELECT 1 FROM recomputed r
            WHERE r.origin_service_id = d.origin_service_id AND r.scope_tenant = d.scope_tenant
              AND r.event_type = d.event_type AND r.stream_id = d.stream_id)
        RETURNING 1
      ),
      drift_added AS (
        INSERT INTO {schema}.wh_stream_digests
          (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count)
        SELECT r.origin_service_id, r.scope_tenant, r.event_type, r.stream_id,
               r.digest_lo, r.digest_hi, r.event_count
        FROM recomputed r
        WHERE NOT EXISTS (
          SELECT 1 FROM {schema}.wh_stream_digests d
          WHERE d.origin_service_id = r.origin_service_id AND d.scope_tenant = r.scope_tenant
            AND d.event_type = r.event_type AND d.stream_id = r.stream_id)
        ON CONFLICT (origin_service_id, scope_tenant, event_type, stream_id) DO NOTHING
        RETURNING 1
      )
      SELECT (SELECT COUNT(*)::int FROM {schema}.wh_stream_digests
              WHERE updated_at < NOW() - @p_settle::interval),
             (SELECT COUNT(*)::int FROM drift_updated),
             (SELECT COUNT(*)::int FROM drift_removed),
             (SELECT COUNT(*)::int FROM drift_added)
      """;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_settle", $"{(int)settleWindow.TotalSeconds} seconds"));
  }

  private static void _addDigestFilterParams(
      System.Data.Common.DbCommand cmd, Guid? originServiceId, IReadOnlyList<string>? eventTypes) {
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = (object?)originServiceId ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = eventTypes is null ? DBNull.Value : eventTypes.ToArray()
    });
  }

  /// <inheritdoc />
  public async Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);

    var sql = $"""
      SELECT
        (SELECT COUNT(*) FROM {schema}.wh_perspective_events WHERE processed_at IS NULL)::bigint as "PendingPerspectiveEvents",
        (SELECT COUNT(*) FROM {schema}.wh_outbox WHERE processed_at IS NULL)::bigint as "PendingOutbox",
        (SELECT COUNT(*) FROM {schema}.wh_inbox WHERE processed_at IS NULL)::bigint as "PendingInbox",
        (SELECT COUNT(*) FROM {schema}.wh_active_streams)::bigint as "ActiveStreams"
      """;

    var result = await _dbContext.Database
      .SqlQueryRaw<WorkCoordinatorStatistics>(sql)
      .ToListAsync(cancellationToken);

    return result.FirstOrDefault() ?? new WorkCoordinatorStatistics();
  }

  /// <summary>
  /// Stores inbox messages directly via store_inbox_messages SQL function.
  /// Bypasses the full process_work_batch pipeline for maximum inbox throughput.
  /// Event storage and perspective creation happen on the next tick when
  /// WorkCoordinatorPublisherWorker claims the messages (self-healing via Phase 5 → 4.5B).
  /// </summary>
  /// <inheritdoc />
  public async Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(
    int partitionCount,
    CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "recompute_partition_numbers");

    long inbox = 0;
    long outbox = 0;
    long active = 0;

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var connection = __scope.Connection;
    await using var cmd = connection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT table_name, rows_recomputed FROM {functionName}(@p_partition_count)";
#pragma warning restore S2077
    var p = cmd.CreateParameter();
    p.ParameterName = "p_partition_count";
    p.Value = partitionCount;
    cmd.Parameters.Add(p);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      var name = reader.GetString(0);
      var count = reader.GetInt64(1);
      switch (name) {
        case "wh_inbox": inbox = count; break;
        case "wh_outbox": outbox = count; break;
        case "wh_active_streams": active = count; break;
      }
    }

    return new PartitionRecomputeResult {
      InboxRowsRecomputed = inbox,
      OutboxRowsRecomputed = outbox,
      ActiveStreamsRowsRecomputed = active
    };
  }

  public async Task StoreInboxMessagesAsync(
    InboxMessage[] messages,
    int partitionCount,
    CancellationToken cancellationToken = default) {
    if (messages.Length == 0) {
      return;
    }

    // v0.657 slice 2: storage-time Reject guard. See StoreOutboxMessagesAsync.
    Whizbang.Core.Messaging.EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, _emptyStreamIdPolicy);

    var json = _serializeNewInboxMessages(messages);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "store_inbox_messages");

#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    var sql = $"SELECT * FROM {functionName}({{0}}::jsonb, NULL::uuid, NULL::timestamptz, {{1}}, {{2}})";
#pragma warning restore S2077

    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      var now = DateTime.UtcNow;
      await _dbContext.Database.ExecuteSqlRawAsync(
        sql,
        [json, now, partitionCount],
        cancellationToken);
    }, logger: _logger, cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Topology arc phase 8.5 — same store, plus the durable redelivery observations
  /// <c>store_inbox_messages</c> now returns for already-seen message ids. The result set is
  /// aggregated to ONE jsonb scalar in SQL so the read needs no keyless entity type in the model
  /// for a diagnostic projection.
  /// </summary>
  public async Task<IReadOnlyList<Whizbang.Core.Messaging.InboxRedeliveryObservation>>
      StoreInboxMessagesWithObservationsAsync(
      InboxMessage[] messages,
      int partitionCount,
      CancellationToken cancellationToken = default) {
    if (messages.Length == 0) {
      return [];
    }

    Whizbang.Core.Messaging.EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, _emptyStreamIdPolicy);

    var json = _serializeNewInboxMessages(messages);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "store_inbox_messages");

    // The store runs exactly as StoreInboxMessagesAsync runs it — same function, same parameters,
    // result discarded. The observation read is a SECOND statement in the SAME command, so the
    // pair is one round trip and Postgres orders them for us; store_inbox_messages' own signature
    // and rowset stay untouched.
#pragma warning disable S2077 // Schema-qualified names built from a validated schema constant
    var sql = $"SELECT * FROM {functionName}(@messages::jsonb, NULL::uuid, NULL::timestamptz, @now, @partitionCount); "
      + Whizbang.Data.Postgres.InboxRedeliveryObservationSql.ObservationQuery(
          BuildSchemaQualifiedName(schema, string.Empty));
#pragma warning restore S2077

    var observedIds = Array.ConvertAll(messages, static m => m.MessageId);
    string? projection = null;
    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = scope.Connection.CreateCommand();
      cmd.CommandText = sql;
      cmd.Parameters.AddWithValue("messages", json);
      cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
      cmd.Parameters.AddWithValue("partitionCount", partitionCount);
      cmd.Parameters.AddWithValue("observedIds", observedIds);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      // Skip the store's own rowset, then read the observation projection.
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { /* discard */ }
      if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
          && await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        projection = reader.IsDBNull(0) ? null : reader.GetString(0);
      }
    }, logger: _logger, cancellationToken: cancellationToken);

    return Whizbang.Core.Messaging.InboxRedeliveryObservation.ParseProjection(projection);
  }

  public async Task StoreOutboxMessagesAsync(
    OutboxMessage[] messages,
    int partitionCount,
    CancellationToken cancellationToken = default) {
    if (messages.Length == 0) {
      return;
    }

    // v0.657 slice 2: storage-time Reject guard. With the default
    // EmptyStreamIdPolicy.Reject, throws EmptyStreamIdException naming the first
    // offending row so producers see the bug at INSERT time — not hundreds of silent
    // claim cycles later (the production forensic pattern).
    Whizbang.Core.Messaging.EmptyStreamIdGuard.ThrowIfAnyHasEmptyStreamId(messages, _emptyStreamIdPolicy);

    var json = _serializeNewOutboxMessages(messages);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "store_outbox_messages");

#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    var sql = $"SELECT * FROM {functionName}({{0}}::jsonb, NULL::uuid, NULL::timestamptz, {{1}}, {{2}})";
#pragma warning restore S2077

    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      var now = DateTime.UtcNow;
      await _dbContext.Database.ExecuteSqlRawAsync(
        sql,
        [json, now, partitionCount],
        cancellationToken);
    }, logger: _logger, cancellationToken: cancellationToken);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<Whizbang.Core.Messaging.CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(
    CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, "wh_outbox");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = (NpgsqlCommand)__scope.Connection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant
    // Served by idx_outbox_coalesce_pending (coalesce_group, created_at) — only pending
    // singles ever live in that partial index.
    cmd.CommandText = $@"
      SELECT coalesce_group, COUNT(*)::bigint, MIN(created_at), MAX(created_at)
      FROM {tableName}
      WHERE coalesce_group IS NOT NULL AND processed_at IS NULL
      GROUP BY coalesce_group";
#pragma warning restore S2077

    var results = new List<Whizbang.Core.Messaging.CoalesceGroupStats>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new Whizbang.Core.Messaging.CoalesceGroupStats {
        Group = reader.GetString(0),
        PendingCount = reader.GetInt64(1),
        OldestCreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
        NewestCreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
      });
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(
    string group,
    int limit,
    CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(group);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, "wh_outbox");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = (NpgsqlCommand)__scope.Connection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant
    // FOR UPDATE SKIP LOCKED: two shippers folding the same group at the same instant
    // partition the rows instead of colliding (the residual fetch→complete race dedups at the
    // consumer's inbox via identity-preserving composites).
    cmd.CommandText = $@"
      SELECT message_id, stream_id, destination, message_type, envelope_type,
             event_data::text, metadata::text, is_event, scheduled_for
      FROM {tableName}
      WHERE coalesce_group = @p_group AND processed_at IS NULL
      ORDER BY created_at
      LIMIT @p_limit
      FOR UPDATE SKIP LOCKED";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("p_group", group));
    cmd.Parameters.Add(new NpgsqlParameter("p_limit", limit));

    var envelopeTypeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo for MessageEnvelope<JsonElement>.");
    var metadataTypeInfo = _jsonOptions.GetTypeInfo(typeof(EnvelopeMetadata))
      ?? throw new InvalidOperationException("No JsonTypeInfo for EnvelopeMetadata.");

    var results = new List<OutboxMessage>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      var messageId = reader.GetGuid(0);
      var envelope = JsonSerializer.Deserialize(reader.GetString(5), envelopeTypeInfo) as IMessageEnvelope<JsonElement>
        ?? throw new InvalidOperationException($"Failed to deserialize envelope for coalesce-pending message {messageId}.");
      EnvelopeMetadata metadata;
      try {
        metadata = JsonSerializer.Deserialize(reader.GetString(6), metadataTypeInfo) as EnvelopeMetadata
          ?? new EnvelopeMetadata { MessageId = new Whizbang.Core.ValueObjects.MessageId(messageId), Hops = [] };
      } catch (JsonException) {
        // Legacy/minimal metadata (e.g. '{}' without the required keys) — the fold only needs
        // the envelope payload; synthesize the minimal metadata rather than stranding the row.
        metadata = new EnvelopeMetadata { MessageId = new Whizbang.Core.ValueObjects.MessageId(messageId), Hops = [] };
      }

      results.Add(new OutboxMessage {
        MessageId = messageId,
        StreamId = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(1),
        Destination = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
        MessageType = reader.GetString(3),
        EnvelopeType = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(4),
        Envelope = envelope,
        Metadata = metadata,
        IsEvent = reader.GetBoolean(7),
        ScheduledFor = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
          ? null
          : reader.GetFieldValue<DateTimeOffset>(8),
        CoalesceGroup = group,
      });
    }
    return results;
  }

  /// <inheritdoc />
  public async Task CompleteCoalesceFoldAsync(
    IReadOnlyList<Guid> foldedIds,
    OutboxMessage[] compositeMessages,
    int partitionCount,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(foldedIds);
    ArgumentNullException.ThrowIfNull(compositeMessages);
    if (foldedIds.Count == 0) {
      return;
    }

    var json = _serializeNewOutboxMessages(compositeMessages);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "store_outbox_messages");
    var tableName = BuildSchemaQualifiedName(schema, "wh_outbox");

#pragma warning disable S2077 // Schema-qualified names built from validated schema constant
    var storeSql = $"SELECT * FROM {functionName}({{0}}::jsonb, NULL::uuid, NULL::timestamptz, {{1}}, {{2}})";
    var completeSql = $"UPDATE {tableName} SET processed_at = NOW() WHERE message_id = ANY({{0}}) AND processed_at IS NULL";
#pragma warning restore S2077

    var foldedIdArray = foldedIds is Guid[] arr ? arr : [.. foldedIds];

    // ONE transaction: the composite row(s) appear and the folded singles complete together —
    // a single is either still pending (floor intact) or folded (composite exists), never both,
    // never neither. BeginTransactionAsync MUST run inside the connection's execution strategy:
    // an unwrapped user transaction throws under the retrying strategy.
    var strategy = _dbContext.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () => {
      await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
      var now = DateTime.UtcNow;
      await _dbContext.Database.ExecuteSqlRawAsync(storeSql, [json, now, partitionCount], cancellationToken);
      await _dbContext.Database.ExecuteSqlRawAsync(
        completeSql,
        [new NpgsqlParameter { Value = foldedIdArray, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid }],
        cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    });
  }

  /// <inheritdoc />
  public async Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(group);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, "wh_outbox");

#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant
    // The deadline degrade: clearing group + floor moves the row into the eligible-scan index,
    // so the normal pump ships it individually. Explicit transition, never a query union.
    var sql = $@"
      UPDATE {tableName}
      SET coalesce_group = NULL, scheduled_for = NULL
      WHERE coalesce_group = {{0}} AND scheduled_for <= NOW() AND processed_at IS NULL";
#pragma warning restore S2077

    var released = 0;
    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      released = await _dbContext.Database.ExecuteSqlRawAsync(sql, [group], cancellationToken);
    }, logger: _logger, cancellationToken: cancellationToken);
    return released;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<Whizbang.Core.Messaging.StuckRow>> FindStuckOutboxRowsAsync(
    int maxAttempts,
    int limit,
    CancellationToken cancellationToken = default)
    => await _findStuckRowsAsync("find_stuck_outbox_rows", maxAttempts, limit, cancellationToken);

  /// <inheritdoc />
  public async Task<IReadOnlyList<Whizbang.Core.Messaging.StuckRow>> FindStuckInboxRowsAsync(
    int maxAttempts,
    int limit,
    CancellationToken cancellationToken = default)
    => await _findStuckRowsAsync("find_stuck_inbox_rows", maxAttempts, limit, cancellationToken);

  // v0.657 slice 5c: shared shape for both sentinel methods — they only differ
  // in the SQL function name. Both use the partial indexes from migration 054
  // so the cost is O(log N) on a near-empty partial in steady state.
  private async Task<IReadOnlyList<Whizbang.Core.Messaging.StuckRow>> _findStuckRowsAsync(
      string functionShortName, int maxAttempts, int limit, CancellationToken ct) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, functionShortName);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), ct);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT message_id, message_type, stream_id, attempts, claimed_since FROM {functionName}(@p_max, @p_limit)";
    cmd.Parameters.Add(new NpgsqlParameter("p_max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = maxAttempts });
    cmd.Parameters.Add(new NpgsqlParameter("p_limit", NpgsqlTypes.NpgsqlDbType.Integer) { Value = limit });

    var rows = new List<Whizbang.Core.Messaging.StuckRow>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      rows.Add(new Whizbang.Core.Messaging.StuckRow {
        MessageId = reader.GetGuid(0),
        MessageType = reader.GetString(1),
        StreamId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
        Attempts = reader.GetInt32(3),
        ClaimedSince = reader.GetDateTime(4),
      });
    }
    return rows;
  }

  /// <inheritdoc />
  public async Task<int> CleanupCompletedStreamsAsync(
    IReadOnlyList<Guid> streamIds,
    CancellationToken cancellationToken = default) {
    if (streamIds is null || streamIds.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "cleanup_completed_streams");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_stream_ids)";
    var p = cmd.CreateParameter();
    p.ParameterName = "p_stream_ids";
    p.Value = streamIds.ToArray();
    if (p is Npgsql.NpgsqlParameter np) {
      np.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid;
    }
    cmd.Parameters.Add(p);
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is int evicted ? evicted : 0;
  }

  public async Task ReportPerspectiveCompletionAsync(
    PerspectiveCursorCompletion completion,
    CancellationToken cancellationToken = default) {
    if (_logger?.IsEnabled(LogLevel.Information) == true) {
      var streamId = completion.StreamId;
      var perspectiveName = completion.PerspectiveName;
      var lastEventId = completion.LastEventId;
      var status = completion.Status;
      _logger.LogInformation(
        "[DIAGNOSTIC] ReportPerspectiveCompletionAsync called: stream={StreamId}, perspective={PerspectiveName}, lastEvent={LastEventId}, status={Status}",
        streamId, perspectiveName, lastEventId, status);
    }

    // CRITICAL: Skip if no events were processed (LastEventId = Guid.Empty)
    // This prevents FK constraint violation when event doesn't exist in wh_event_store
    if (completion.LastEventId == Guid.Empty) {
      _logSkippingEmptyCheckpoint(completion.StreamId, completion.PerspectiveName);
      return;
    }

    await _executeCursorCompletionAsync(
      completion.StreamId, completion.PerspectiveName,
      completion.LastEventId, completion.ProcessedEventIds,
      (short)completion.Status, null,
      cancellationToken);

    await _logCheckpointDiagnosticAsync(completion.StreamId, completion.PerspectiveName, cancellationToken);
  }

  /// <summary>
  /// Executes the complete_perspective_cursor_work SQL function within a managed transaction.
  /// Creates a new transaction if one does not already exist on the DbContext.
  /// </summary>
  private async Task _executeCursorCompletionAsync(
    Guid streamId, string perspectiveName, Guid lastEventId,
    Guid[] processedEventIds,
    short status, string? error,
    CancellationToken cancellationToken) {
    var transaction = _dbContext.Database.CurrentTransaction;
    var needsCommit = transaction == null;

    if (needsCommit) {
      transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    try {
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
        DEFAULT_SCHEMA,
        _logger);
      var functionName = BuildSchemaQualifiedName(schema, "complete_perspective_cursor_work");
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant; parameters use EF Core positional placeholders ({0}..{5})
      var sql = $"SELECT {functionName}({{0}}, {{1}}, {{2}}, {{3}}::jsonb, {{4}}, {{5}}::text)";
#pragma warning restore S2077

      // Serialize ProcessedEventIds as JSON string for the JSONB parameter (AOT-safe)
      var processedEventIdsJson = System.Text.Json.JsonSerializer.Serialize(
        processedEventIds,
        _jsonOptions.GetTypeInfo(typeof(Guid[])) ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]"));

      await _dbContext.Database.ExecuteSqlRawAsync(
        sql,
        [streamId, perspectiveName, lastEventId, processedEventIdsJson, status, error!],
        cancellationToken);

      if (needsCommit && transaction != null) {
        await transaction.CommitAsync(cancellationToken);
        if (_logger?.IsEnabled(LogLevel.Information) == true) {
          _logger.LogInformation(
            "[DIAGNOSTIC] Transaction committed for stream={StreamId}, perspective={PerspectiveName}",
            streamId, perspectiveName);
        }
      }
    } catch {
      if (needsCommit && transaction != null) {
        await transaction.RollbackAsync(cancellationToken);
      }
      throw;
    } finally {
      if (needsCommit && transaction != null) {
        await transaction.DisposeAsync();
      }
    }

    if (_logger?.IsEnabled(LogLevel.Information) == true) {
      _logger.LogInformation(
        "[DIAGNOSTIC] complete_perspective_cursor_work completed for stream={StreamId}, perspective={PerspectiveName}",
        streamId, perspectiveName);
    }
  }

  /// <summary>
  /// Logs a debug message when skipping checkpoint update for empty LastEventId.
  /// </summary>
  private void _logSkippingEmptyCheckpoint(Guid streamId, string perspectiveName) {
    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      _logger.LogDebug(
        "[DIAGNOSTIC] Skipping checkpoint update for stream={StreamId}, perspective={PerspectiveName} - no events processed (LastEventId is Empty)",
        streamId, perspectiveName);
    }
  }

  /// <summary>
  /// Queries and logs the checkpoint state after a cursor completion update for diagnostics.
  /// </summary>
  private async Task _logCheckpointDiagnosticAsync(
    Guid streamId, string perspectiveName,
    CancellationToken cancellationToken) {
    var diagnosticSchema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var diagnosticTable = BuildSchemaQualifiedName(diagnosticSchema, PERSPECTIVE_CURSORS_TABLE);
#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant; parameters use EF Core positional placeholders ({0}, {1})
    var diagnosticSql = $"SELECT stream_id, perspective_name, status, last_event_id, error FROM {diagnosticTable} WHERE stream_id = {{0}} AND perspective_name = {{1}}";
#pragma warning restore S2077

    var checkpointState = await _dbContext.Database
      .SqlQueryRaw<CheckpointDiagnostic>(diagnosticSql, streamId, perspectiveName)
      .OrderBy(c => c.StreamId)
      .FirstOrDefaultAsync(cancellationToken);

    if (checkpointState != null) {
      if (_logger?.IsEnabled(LogLevel.Information) == true) {
        _logger.LogInformation(
          "[DIAGNOSTIC] After update - checkpoint state: stream={StreamId}, perspective={PerspectiveName}, status={Status}, lastEvent={LastEventId}, error={Error}",
          checkpointState.StreamId, checkpointState.PerspectiveName, checkpointState.Status, checkpointState.LastEventId, checkpointState.Error);
      }
    } else {
      if (_logger?.IsEnabled(LogLevel.Warning) == true) {
        _logger.LogWarning(
          "[DIAGNOSTIC] Checkpoint not found after update: stream={StreamId}, perspective={PerspectiveName}",
          streamId, perspectiveName);
      }
    }
  }

  /// <summary>
  /// Reports perspective cursor failure directly (out-of-band).
  /// Calls complete_perspective_cursor_work SQL function directly without full work batch processing.
  /// Creates its own database connection to allow calling after the scoped DbContext is disposed.
  /// </summary>
  public async Task ReportPerspectiveFailureAsync(
    PerspectiveCursorFailure failure,
    CancellationToken cancellationToken = default) {
    // Use DbContext's ExecuteSqlRawAsync which properly manages the connection
    // This works with both traditional connection strings and NpgsqlDataSource

    // CRITICAL: Skip if no events were processed (LastEventId = Guid.Empty)
    // This prevents FK constraint violation when event doesn't exist in wh_event_store
    if (failure.LastEventId == Guid.Empty) {
      if (_logger?.IsEnabled(LogLevel.Debug) == true) {
        var streamId = failure.StreamId;
        var perspectiveName = failure.PerspectiveName;
        _logger.LogDebug(
          "[DIAGNOSTIC] Skipping checkpoint update for failure on stream={StreamId}, perspective={PerspectiveName} - no events processed (LastEventId is Empty)",
          streamId, perspectiveName);
      }
      return;
    }

    await _executeCursorCompletionAsync(
      failure.StreamId, failure.PerspectiveName,
      failure.LastEventId, failure.ProcessedEventIds,
      (short)failure.Status, failure.Error,
      cancellationToken);
  }

  /// <summary>
  /// Gets the current checkpoint for a perspective stream.
  /// Returns null if no checkpoint exists yet.
  /// </summary>
  public async Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default) {

    // Get schema from OutboxRecord entity (all Whizbang tables share the same schema)
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, PERSPECTIVE_CURSORS_TABLE);
    var eventStoreTable = BuildSchemaQualifiedName(schema, "wh_event_store");
#pragma warning disable S2077 // Schema-qualified table names built from validated schema constant; parameters use EF Core positional placeholders ({0}, {1})
    // Slice 26.13: LEFT JOIN wh_event_store so cold-cache cursor hydration warms the
    // commit_sequence half of PerspectiveCursorCache (see GetPerspectiveCursorsBatchAsync).
    var sql = "SELECT c.stream_id, c.perspective_name, c.last_event_id, c.status, "
            + "c.rewind_trigger_event_id, e.commit_sequence AS last_commit_sequence "
            + $"FROM {tableName} c "
            + $"LEFT JOIN {eventStoreTable} e ON e.event_id = c.last_event_id "
            + "WHERE c.stream_id = {0} AND c.perspective_name = {1}";
#pragma warning restore S2077

    var result = await _dbContext.Database
      .SqlQueryRaw<CursorQueryResult>(sql, streamId, perspectiveName)
      .OrderBy(c => c.StreamId)
      .FirstOrDefaultAsync(cancellationToken);

    if (result == null) {
      return null;
    }

    return new PerspectiveCursorInfo {
      StreamId = result.StreamId,
      PerspectiveName = result.PerspectiveName,
      LastEventId = result.LastEventId,
      Status = (PerspectiveProcessingStatus)result.Status,
      RewindTriggerEventId = result.RewindTriggerEventId,
      LastCommitSequence = result.LastCommitSequence
    };
  }

  /// <inheritdoc />
  public async Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(
    Guid[] streamIds,
    CancellationToken cancellationToken = default) {

    if (streamIds.Length == 0) {
      return [];
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, PERSPECTIVE_CURSORS_TABLE);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    var eventStoreTable = BuildSchemaQualifiedName(schema, "wh_event_store");

    await using var cmd = (Npgsql.NpgsqlCommand)dbConnection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table names built from validated schema constant
    // Slice 26.13: LEFT JOIN wh_event_store so cold-cache cursor prefetch can warm the
    // commit_sequence half of PerspectiveCursorCache. Without it, the inversion detector
    // falls back to event_id (UUIDv7 lex) comparison and re-introduces same-millisecond
    // generation-vs-commit-order false positives (a production run surfaced thousands of such logs).
    cmd.CommandText =
        "SELECT c.stream_id, c.perspective_name, c.last_event_id, c.status, "
      + "c.rewind_trigger_event_id, e.commit_sequence "
      + $"FROM {tableName} c "
      + $"LEFT JOIN {eventStoreTable} e ON e.event_id = c.last_event_id "
      + "WHERE c.stream_id = ANY(@p_stream_ids)";
#pragma warning restore S2077
#pragma warning disable RCS1130 // NpgsqlDbType third-party enum; bitwise composition is its documented API.
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streamIds
    });
#pragma warning restore RCS1130

    var results = new List<PerspectiveCursorInfo>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new PerspectiveCursorInfo {
        StreamId = reader.GetGuid(0),
        PerspectiveName = reader.GetString(1),
        LastEventId = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
        Status = (PerspectiveProcessingStatus)reader.GetInt32(3),
        RewindTriggerEventId = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(4),
        LastCommitSequence = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(5)
      });
    }

    return results;
  }

  /// <inheritdoc/>
  public async Task RecordLifecycleCompletionAsync(
    Guid eventId,
    CancellationToken cancellationToken = default) {

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, "wh_lifecycle_completions");

    // Idempotent: ON CONFLICT DO NOTHING handles duplicate event IDs
#pragma warning disable S2077
    var sql = $"INSERT INTO {tableName} (event_id, instance_id) VALUES ({{0}}, {{1}}) ON CONFLICT DO NOTHING";
#pragma warning restore S2077

    await _dbContext.Database.ExecuteSqlRawAsync(
      sql,
      [eventId, _instanceId()],
      cancellationToken);
  }

  /// <inheritdoc/>
  public async Task<IReadOnlyList<OrphanedLifecycleEvent>> GetOrphanedLifecycleEventsAsync(
    Dictionary<string, IReadOnlyList<string>> perspectivesPerEventType,
    TimeSpan lookbackWindow,
    int maxOrphans = 100,
    CancellationToken cancellationToken = default) {

    if (perspectivesPerEventType.Count == 0) {
      return [];
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var eventStoreTable = BuildSchemaQualifiedName(schema, "wh_event_store");
    var bodyTable = BuildSchemaQualifiedName(schema, "wh_event_body");
    var cursorsTable = BuildSchemaQualifiedName(schema, PERSPECTIVE_CURSORS_TABLE);
    var completionsTable = BuildSchemaQualifiedName(schema, "wh_lifecycle_completions");

    var cutoff = DateTimeOffset.UtcNow - lookbackWindow;
    var orphaned = new List<OrphanedLifecycleEvent>();

    // ONE set-based pass over ALL registered types (the per-type loop ran a query per catalog
    // entry — over a thousand sequential round-trips on a large consumer, enough sustained DB
    // work to stall the host past its liveness budget at startup). The expectation pairs ride
    // as parallel unnest arrays; an event qualifies when every expected perspective for its
    // type has a cursor at/past it, no completion marker exists, and it is inside the lookback
    // window. The batch is GLOBALLY capped (oldest first) — the caller loops bounded passes.
    var pairTypes = new List<string>();
    var pairNames = new List<string>();
    foreach (var (eventTypeKey, expectedPerspectives) in perspectivesPerEventType) {
      foreach (var perspectiveName in expectedPerspectives) {
        pairTypes.Add(eventTypeKey);
        pairNames.Add(perspectiveName);
      }
    }
    if (pairTypes.Count == 0) {
      return [];
    }

#pragma warning disable S2077
    var sql = $@"
      WITH expected AS (
        SELECT * FROM unnest({{0}}::text[], {{1}}::text[]) AS x(event_type, perspective_name)
      ),
      expected_counts AS (
        SELECT event_type, COUNT(*) AS cnt FROM expected GROUP BY event_type
      )
      SELECT e.event_id, e.stream_id, eb.event_data AS event_data, eb.metadata AS metadata, e.event_type, e.scope
      FROM {eventStoreTable} e
      JOIN expected_counts ec ON ec.event_type = e.event_type
      LEFT JOIN {bodyTable} eb ON eb.event_id = e.event_id
      WHERE e.created_at >= {{2}}
        AND NOT EXISTS (
          SELECT 1 FROM {completionsTable} lc WHERE lc.event_id = e.event_id
        )
        AND (
          SELECT COUNT(DISTINCT pc.perspective_name)
          FROM {cursorsTable} pc
          JOIN expected x ON x.event_type = e.event_type AND x.perspective_name = pc.perspective_name
          WHERE pc.stream_id = e.stream_id
            AND pc.last_event_id >= e.event_id
        ) = ec.cnt
      ORDER BY e.created_at
      LIMIT {{3}}";
#pragma warning restore S2077

    try {
      // Bounded even when the store is degraded: the reconcile may time out a pass and retry
      // later — it must never hold the host's resources indefinitely.
      var previousTimeout = _dbContext.Database.GetCommandTimeout();
      _dbContext.Database.SetCommandTimeout(120);
      List<OrphanedEventRow> rows;
      try {
        rows = await _dbContext.Database
          .SqlQueryRaw<OrphanedEventRow>(sql, pairTypes.ToArray(), pairNames.ToArray(), cutoff, Math.Max(1, maxOrphans))
          .ToListAsync(cancellationToken);
      } finally {
        _dbContext.Database.SetCommandTimeout(previousTimeout);
      }

      foreach (var row in rows) {
        try {
          var envelope = _deserializeEventEnvelope(row);
          orphaned.Add(new OrphanedLifecycleEvent(row.EventId, row.StreamId, envelope));
        } catch (Exception ex) {
          if (_logger?.IsEnabled(LogLevel.Warning) == true) {
            _logger.LogWarning(ex, "Failed to deserialize orphaned event {EventId} (type: {EventType}) for reconciliation", row.EventId, row.EventType);
          }
        }
      }
    } catch (Exception ex) {
      if (_logger?.IsEnabled(LogLevel.Warning) == true) {
        _logger.LogWarning(ex, "Failed to query orphaned lifecycle events ({TypeCount} type(s))", perspectivesPerEventType.Count);
      }
    }

    return orphaned;
  }

  /// <inheritdoc/>
  public async Task<int> CleanupLifecycleCompletionsAsync(
    TimeSpan retentionPeriod,
    CancellationToken cancellationToken = default) {

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var tableName = BuildSchemaQualifiedName(schema, "wh_lifecycle_completions");
    var cutoff = DateTimeOffset.UtcNow - retentionPeriod;

#pragma warning disable S2077
    var sql = $"DELETE FROM {tableName} WHERE completed_at < {{0}}";
#pragma warning restore S2077

    return await _dbContext.Database.ExecuteSqlRawAsync(
      sql,
      [cutoff],
      cancellationToken);
  }

  /// <summary>
  /// Deserializes an orphaned event row from the event store into a MessageEnvelope with JsonElement payload.
  /// Falls back to JsonDocument.Parse when the type resolver returns incompatible type info.
  /// </summary>
  /// <docs>fundamentals/events/event-store-serialization</docs>
  private MessageEnvelope<JsonElement> _deserializeEventEnvelope(OrphanedEventRow row) {
    // Deserialize event_data as JsonElement for AOT compatibility.
    // The concrete event type is resolved downstream by the lifecycle coordinator/receptors.
    JsonElement payload;
    try {
      var typeInfo = _jsonOptions.GetTypeInfo(typeof(JsonElement))
        ?? throw new InvalidOperationException("No JsonTypeInfo found for JsonElement.");
      payload = (JsonElement)(System.Text.Json.JsonSerializer.Deserialize(row.EventData, typeInfo)
        ?? throw new InvalidOperationException($"Failed to deserialize event {row.EventId} as JsonElement."));
    } catch (NotSupportedException) {
      // Fallback: deserializing an interface/abstract type is not supported.
      // The chained type resolver may return polymorphic IEvent type info.
      payload = JsonDocument.Parse(row.EventData).RootElement.Clone();
    } catch (InvalidOperationException ex) when (ex.Message.Contains("incompatible JsonTypeInfo")) {
      // Fallback: the type resolver returned a JsonTypeInfo for a different type
      // (e.g., IEvent instead of JsonElement). Bypass with direct parse (AOT-safe).
      payload = JsonDocument.Parse(row.EventData).RootElement.Clone();
    }

    // Restore security context from scope column so _establishSecurityContextAsync can extract tenant/user.
    // Scope uses PerspectiveScope short keys: "t" = tenant, "u" = user.
    var hops = _buildHopsFromScope(row.Scope);

    return new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(row.EventId),
      Payload = payload,
      Hops = hops,
      DispatchContext = new MessageDispatchContext {
        Mode = DispatchModes.Local,
        Source = MessageSource.Local
      }
    };
  }

  private List<MessageHop> _buildHopsFromScope(string? scopeJson) {
    if (string.IsNullOrEmpty(scopeJson)) {
      return [];
    }

    try {
      var typeInfo = _jsonOptions.GetTypeInfo(typeof(JsonElement))
        ?? throw new InvalidOperationException("No JsonTypeInfo found for JsonElement.");
      var scopeElement = (JsonElement)(System.Text.Json.JsonSerializer.Deserialize(scopeJson, typeInfo)!);

      string? tenantId = null;
      string? userId = null;

      if (scopeElement.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String) {
        tenantId = t.GetString();
      }
      if (scopeElement.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.String) {
        userId = u.GetString();
      }

      if (!string.IsNullOrEmpty(tenantId) || !string.IsNullOrEmpty(userId)) {
        return [new MessageHop {
          Type = HopType.Current,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenantId, UserId = userId })
        }];
      }
    } catch (Exception ex) {
      // Scope parsing is best-effort for reconciliation, but a parse failure silently drops the
      // tenant/user security context so the replayed lifecycle event runs unscoped — always log it.
      EFCoreWorkCoordinatorLog.ReconcileScopeParseFailed(_logger ?? NullLogger<EFCoreWorkCoordinator<TDbContext>>.Instance, ex);
    }

    return [];
  }

  /// <summary>
  /// Broker DLQ import (migration 118): gives a broker-dead-lettered message durable custody as a
  /// wh_dead_letters row via <c>wh_import_dead_letter</c>. The RAW wire body travels as TEXT —
  /// the SQL side parses defensively (non-JSON bodies become JSON strings), so this path never
  /// deserializes and never loses a message to a parse failure. Idempotent on the wire message id.
  /// </summary>
  /// <docs>operations/dead-letter-queue/transport-recovery</docs>
  public async Task<bool> ImportBrokerDeadLetterAsync(
      Whizbang.Core.Transports.BrokerDeadLetterImport import,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(import);
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = GetSchemaWithFallback(
        _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(), DEFAULT_SCHEMA, _logger);
      var qualified = BuildSchemaQualifiedName(schema, "wh_import_dead_letter");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCommand();
      cmd.Parameters.AddWithValue("p_dead_letter_id", (Guid)Whizbang.Core.ValueObjects.TrackedGuid.NewMedo());
      cmd.Parameters.AddWithValue("p_message_id", import.MessageId);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_stream_id", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)import.StreamId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_message_type", NpgsqlTypes.NpgsqlDbType.Text) {
        Value = (object?)import.MessageType ?? DBNull.Value
      });
      cmd.Parameters.AddWithValue("p_destination", import.Destination);
      cmd.Parameters.AddWithValue("p_envelope_json", import.EnvelopeJson);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_broker_reason", NpgsqlTypes.NpgsqlDbType.Text) {
        Value = (object?)import.BrokerReason ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_broker_description", NpgsqlTypes.NpgsqlDbType.Text) {
        Value = (object?)import.BrokerDescription ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_enqueued_at", NpgsqlTypes.NpgsqlDbType.TimestampTz) {
        Value = (object?)import.EnqueuedAt ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_delivery_count", NpgsqlTypes.NpgsqlDbType.Integer) {
        Value = (object?)import.DeliveryCount ?? DBNull.Value
      });
      cmd.Parameters.AddWithValue("p_instance_id", _instanceId());
      cmd.Parameters.AddWithValue("p_generation", new Whizbang.Core.Messaging.DefaultGenerationProvider().GetGeneration());
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT {qualified}(@p_dead_letter_id,@p_message_id,@p_stream_id,@p_message_type,@p_destination,@p_envelope_json,@p_broker_reason,@p_broker_description,@p_enqueued_at,@p_delivery_count,@p_instance_id,@p_generation)";
#pragma warning restore S2077
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is bool imported && imported;
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      _logger?.LogError(ex,
        "ImportBrokerDeadLetterAsync failed for message {MessageId} from {Destination} — message stays on the broker DLQ for the next drain pass",
        import.MessageId, import.Destination);
      // Rethrow: FALSE means "duplicate — custody already exists, safe to settle at the broker".
      // A failed import must NOT look like a duplicate, or the drainer would complete the broker
      // message and lose it. Throwing makes the drainer abandon, so the broker re-offers it.
      throw;
    }
  }

  private static Guid _instanceId() {
    // Fallback to a new GUID — the actual instance ID is set by the PerspectiveWorker
    // which resolves IServiceInstanceProvider from DI
    return Guid.NewGuid();
  }

  /// <summary>
  /// Queries wh_perspective_cursors for cursors with the RewindRequired flag (bit 5 = 32).
  /// Used by PerspectiveWorker startup scan to identify streams needing rewind repair.
  /// </summary>
  /// <docs>fundamentals/perspectives/rewind#startup-scan</docs>
  public async Task<IReadOnlyList<RewindCursorInfo>> GetCursorsRequiringRewindAsync(
      CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var cursorsTable = BuildSchemaQualifiedName(schema, PERSPECTIVE_CURSORS_TABLE);

    var sql = $@"
      SELECT stream_id, perspective_name, last_event_id, rewind_trigger_event_id
      FROM {cursorsTable}
      WHERE (status & 32) = 32
      ORDER BY stream_id, perspective_name";

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    var results = new List<RewindCursorInfo>();
    await using var cmd = dbConnection.CreateCommand();
    cmd.CommandText = sql;

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new RewindCursorInfo(
        reader.GetGuid(0),
        reader.GetString(1),
        await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
        await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(3)));
    }

    return results;
  }

  /// <summary>
  /// Deletes processed perspective event rows via complete_perspective_events SQL function.
  /// Called after drain mode processing completes for a batch of events.
  /// </summary>
  /// <docs>fundamentals/perspectives/drain-mode</docs>
  public async Task<int> CompletePerspectiveEventsAsync(
    Guid[] workItemIds,
    bool debugMode,
    CancellationToken cancellationToken = default) {
    if (workItemIds.Length == 0) {
      return 0;
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "complete_perspective_events");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {functionName}(@p_event_work_ids, @p_debug_mode)";
#pragma warning restore S2077
#pragma warning disable RCS1130 // NpgsqlDbType third-party enum; bitwise composition is its documented API.
    cmd.Parameters.Add(new NpgsqlParameter("p_event_work_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = workItemIds
    });
#pragma warning restore RCS1130
    cmd.Parameters.Add(new NpgsqlParameter("p_debug_mode", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = debugMode });

    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is int count ? count : 0;
  }

  /// <summary>
  /// Batch-fetches events for multiple streams in a single call via get_stream_events SQL function.
  /// Returns denormalized rows: one per (stream, event). C# groups by StreamId for processing.
  /// </summary>
  /// <docs>fundamentals/perspectives/drain-mode</docs>
  /// <inheritdoc />
  public async Task<int> ReapExhaustedOrphanedPerspectiveRowsAsync(
    Guid instanceId,
    IReadOnlyList<Guid> streamIds,
    int maxAttempts,
    CancellationToken cancellationToken = default) {
    if (streamIds.Count == 0) {
      return 0;
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "reap_exhausted_orphaned_perspective_rows");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {functionName}(@p_instance_id, @p_stream_ids, @p_max_attempts)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
#pragma warning disable RCS1130 // NpgsqlDbType third-party enum; bitwise composition is its documented API.
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streamIds is Guid[] arr ? arr : System.Linq.Enumerable.ToArray(streamIds)
    });
#pragma warning restore RCS1130
    cmd.Parameters.Add(new NpgsqlParameter("p_max_attempts", maxAttempts));
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is int i ? i : 0;
  }

  public async Task<List<StreamEventData>> GetStreamEventsAsync(
    Guid instanceId,
    Guid[] streamIds,
    CancellationToken cancellationToken = default) {
    if (streamIds.Length == 0) {
      return [];
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "get_stream_events");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_instance_id, @p_stream_ids)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
#pragma warning disable RCS1130 // NpgsqlDbType third-party enum; bitwise composition is its documented API.
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streamIds
    });
#pragma warning restore RCS1130

    var results = new List<StreamEventData>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    var hasPerspectiveColumn = false;
    var perspectiveOrdinal = -1;
    try {
      perspectiveOrdinal = reader.GetOrdinal("out_perspective_name");
      hasPerspectiveColumn = true;
    } catch (IndexOutOfRangeException) {
      // Older SQL function without out_perspective_name column. Field stays null; cooldown
      // gate falls back to legacy "all-rows-under-eventid" semantics.
    }
    var hasCommitSequenceColumn = false;
    var commitSequenceOrdinal = -1;
    try {
      commitSequenceOrdinal = reader.GetOrdinal("out_commit_sequence");
      hasCommitSequenceColumn = true;
    } catch (IndexOutOfRangeException) {
      // Older SQL function without out_commit_sequence column (pre-slice-26.7).
      // Field stays null; consumers fall back to event_id-based cursor ordering.
    }
    var hasAttemptsColumn = false;
    var attemptsOrdinal = -1;
    try {
      attemptsOrdinal = reader.GetOrdinal("out_attempts");
      hasAttemptsColumn = true;
    } catch (IndexOutOfRangeException) {
      // v0.502 slice C.4c forward-compatibility — older SQL function (pre-C.4c) doesn't
      // surface attempts. Field stays 0; PerspectiveWorker's DLQ check is a no-op for
      // these rows (their attempts count is still tracked in wh_perspective_events; the
      // claim_orphaned_perspective_events path will eventually dead-letter them via
      // FailureFlushWorker once that path also lands).
    }
    while (await reader.ReadAsync(cancellationToken)) {
      // AOT-safe: read columns by ordinal, parse event_data as string
      var metadataOrdinal = reader.GetOrdinal("out_metadata");
      var scopeOrdinal = reader.GetOrdinal("out_scope");
      results.Add(new StreamEventData {
        StreamId = reader.GetGuid(reader.GetOrdinal("out_stream_id")),
        EventId = reader.GetGuid(reader.GetOrdinal("out_event_id")),
        EventType = reader.GetString(reader.GetOrdinal("out_event_type")),
        EventData = reader.GetString(reader.GetOrdinal("out_event_data")),
        Metadata = await reader.IsDBNullAsync(metadataOrdinal, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(metadataOrdinal),
        Scope = await reader.IsDBNullAsync(scopeOrdinal, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(scopeOrdinal),
        EventWorkId = reader.GetGuid(reader.GetOrdinal("out_event_work_id")),
        PerspectiveName = hasPerspectiveColumn && !await reader.IsDBNullAsync(perspectiveOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetString(perspectiveOrdinal)
          : null,
        CommitSequence = hasCommitSequenceColumn && !await reader.IsDBNullAsync(commitSequenceOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetInt64(commitSequenceOrdinal)
          : null,
        Attempts = hasAttemptsColumn && !await reader.IsDBNullAsync(attemptsOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetInt32(attemptsOrdinal)
          : 0,
      });
    }

    return results;
  }

  /// <inheritdoc />
  /// <docs>messaging/work-coordinator#local-service-identity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorServiceIdTests.cs</tests>
  public async Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) {
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    // Schema-qualified like every other query here (issue #630): wh_service_config is created in
    // the DbContext's schema, and a bare name resolves through search_path — 42P01 on a non-public
    // schema, or another schema's row when public happens to have one.
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var table = BuildSchemaQualifiedName(schema, "wh_service_config");
    await using var cmd = dbConnection.CreateCommand();
#pragma warning disable S2077 // schema comes from the EF model, not user input — same pattern as every neighbor
    cmd.CommandText = $"SELECT service_id FROM {table} LIMIT 1";
#pragma warning restore S2077
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result switch {
      null or DBNull => Guid.Empty,
      Guid g => g,
      _ => Guid.Empty
    };
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
    CancellationToken cancellationToken = default)
    => FetchOutboxBatchAsync(streamIds, instanceId, maxPerStream, null, cancellationToken);

  /// <inheritdoc />
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Reader hydration covers the schema-shape drift between deployed migrations (older column set vs newer columns: commit_sequence + scope + envelope_type all have try-GetOrdinal fallbacks). The branches mirror migration adoption sequencing.")]
  public async Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream,
    long? maxBytes,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return [];
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "fetch_outbox_batch");

    var streamArr = streamIds is Guid[] arr ? arr : [.. streamIds];
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_ids, @p_instance_id, @p_max_per_stream, @p_max_bytes)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamArr });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_max_per_stream", maxPerStream));
    // NULL = count bound only, which is exactly what this function did before the byte budget.
    cmd.Parameters.Add(new NpgsqlParameter("p_max_bytes", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)maxBytes ?? DBNull.Value,
    });

    var results = new List<OutboxBatchRow>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    // Slice 26.6b: graceful fallback for the new commit_sequence / origin_* columns —
    // matches the established pattern for older SQL functions that may not have them.
    var hasCommitSequenceCol = false;
    var commitSeqOrdinal = -1;
    var originServiceOrdinal = -1;
    var originSeqOrdinal = -1;
    try {
      commitSeqOrdinal = reader.GetOrdinal("commit_sequence");
      originServiceOrdinal = reader.GetOrdinal("origin_service_id");
      originSeqOrdinal = reader.GetOrdinal("origin_commit_sequence");
      hasCommitSequenceCol = true;
    } catch (IndexOutOfRangeException) {
      // Older fetch_outbox_batch without slice 26.6b columns — leave as null.
    }
    // Slice 1 of release/v0.648.0-alpha.1 — same defensive ordinal-lookup pattern
    // for the new error column. Older fetch_outbox_batch revisions (pre-Slice 1)
    // don't return it; leave Error as null in that case.
    var hasErrorCol = false;
    var errorOrdinal = -1;
    try {
      errorOrdinal = reader.GetOrdinal("error");
      hasErrorCol = true;
    } catch (IndexOutOfRangeException) {
      // Older fetch_outbox_batch without Slice 1's error column — leave Error null.
    }
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new OutboxBatchRow {
        MessageId = reader.GetGuid(0),
        StreamId = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(1),
        Destination = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
        MessageType = reader.GetString(3),
        EnvelopeType = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
        EventData = reader.GetString(5),
        Metadata = reader.GetString(6),
        Scope = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7),
        Status = reader.GetInt32(8),
        Attempts = reader.GetInt32(9),
        PartitionNumber = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(10),
        IsEvent = reader.GetBoolean(11),
        CommitSequence = hasCommitSequenceCol && !await reader.IsDBNullAsync(commitSeqOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetInt64(commitSeqOrdinal)
          : null,
        OriginServiceId = hasCommitSequenceCol && !await reader.IsDBNullAsync(originServiceOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetGuid(originServiceOrdinal)
          : null,
        OriginCommitSequence = hasCommitSequenceCol && !await reader.IsDBNullAsync(originSeqOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetInt64(originSeqOrdinal)
          : null,
        Error = hasErrorCol && !await reader.IsDBNullAsync(errorOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetString(errorOrdinal)
          : null,
      });
    }
    return results;
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
    CancellationToken cancellationToken = default)
    => FetchInboxBatchAsync(streamIds, instanceId, maxPerStream, null, cancellationToken);

  /// <inheritdoc />
  public async Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream,
    long? maxBytes,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return [];
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "fetch_inbox_batch");

    var streamArr = streamIds is Guid[] arr ? arr : [.. streamIds];
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_ids, @p_instance_id, @p_max_per_stream, @p_max_bytes)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamArr });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_max_per_stream", maxPerStream));
    // NULL = count bound only, which is exactly what this function did before the byte budget.
    cmd.Parameters.Add(new NpgsqlParameter("p_max_bytes", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)maxBytes ?? DBNull.Value,
    });

    var results = new List<InboxBatchRow>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    // v0.651 inbox forensic-preservation slice — defensive ordinal lookup for the new
    // error column. Mirrors the outbox-side pattern: older fetch_inbox_batch revisions
    // (pre-v0.651) don't return it; leave Error null in that case so a mid-rollout
    // package mix doesn't blow up.
    var hasErrorCol = false;
    var errorOrdinal = -1;
    try {
      errorOrdinal = reader.GetOrdinal("error");
      hasErrorCol = true;
    } catch (IndexOutOfRangeException) {
      // Pre-v0.651 fetch_inbox_batch without the error column — leave Error null.
    }
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new InboxBatchRow {
        MessageId = reader.GetGuid(0),
        StreamId = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(1),
        HandlerName = reader.GetString(2),
        MessageType = reader.GetString(3),
        EventData = reader.GetString(4),
        Metadata = reader.GetString(5),
        Scope = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
        Status = reader.GetInt32(7),
        Attempts = reader.GetInt32(8),
        PartitionNumber = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(9),
        IsEvent = reader.GetBoolean(10),
        Error = hasErrorCol && !await reader.IsDBNullAsync(errorOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetString(errorOrdinal)
          : null,
      });
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PendingPerspectiveEvent>> FetchPendingPerspectiveEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid instanceId,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "fetch_pending_perspective_events");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_id, @p_perspective_name, @p_instance_id)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_id", streamId));
    cmd.Parameters.Add(new NpgsqlParameter("p_perspective_name", perspectiveName));
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));

    var results = new List<PendingPerspectiveEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new PendingPerspectiveEvent(
        EventWorkId: reader.GetGuid(0),
        EventId: reader.GetGuid(1),
        CommitSequence: await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetInt64(2)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PendingPerspectiveEvent>> ClaimAndFetchPendingPerspectiveEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid instanceId,
    TimeSpan leaseDuration,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "claim_and_fetch_pending_perspective_events");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    var now = DateTime.UtcNow;
    var leaseExpiry = now + leaseDuration;

    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_id, @p_perspective_name, @p_instance_id, @p_lease_expiry, @p_now)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_id", streamId));
    cmd.Parameters.Add(new NpgsqlParameter("p_perspective_name", perspectiveName));
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_lease_expiry", leaseExpiry));
    cmd.Parameters.Add(new NpgsqlParameter("p_now", now));

    var results = new List<PendingPerspectiveEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new PendingPerspectiveEvent(
        EventWorkId: reader.GetGuid(0),
        EventId: reader.GetGuid(1),
        CommitSequence: await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetInt64(2)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<StreamEventData>> FetchEventsByIdsAsync(
    IReadOnlyList<Guid> eventIds,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return [];
    }

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "fetch_events_by_ids");

    var idArr = eventIds is Guid[] arr ? arr : [.. eventIds];
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_event_ids)";
    cmd.Parameters.Add(new NpgsqlParameter("p_event_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArr });

    var results = new List<StreamEventData>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new StreamEventData {
        StreamId = reader.GetGuid(0),
        EventId = reader.GetGuid(1),
        EventType = reader.GetString(2),
        EventData = reader.GetString(3),
        Metadata = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
        Scope = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
        EventWorkId = Guid.Empty
      });
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA, _logger);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var connection = __scope.Connection;

    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT * FROM \"{schema}\".perform_maintenance()";
    command.CommandTimeout = 30;

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var results = new List<MaintenanceResult>();
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new MaintenanceResult(
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetDouble(2),
        reader.GetString(3)
      ));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PurgedOrphanInboxRow>> PurgeOrphanInboxAsync(
      IReadOnlyList<string> handledTypeNames,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(handledTypeNames);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(InboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA, _logger);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var connection = __scope.Connection;

    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT * FROM \"{schema}\".purge_orphan_inbox(@handled_types)";
    command.CommandTimeout = 30;
    var param = (Npgsql.NpgsqlParameter)command.CreateParameter();
    param.ParameterName = "handled_types";
    param.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text;
    param.Value = handledTypeNames is string[] arr ? arr : System.Linq.Enumerable.ToArray(handledTypeNames);
    command.Parameters.Add(param);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var rows = new List<PurgedOrphanInboxRow>();
    while (await reader.ReadAsync(cancellationToken)) {
      rows.Add(new PurgedOrphanInboxRow(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2)
      ));
    }
    return rows;
  }
}

/// <summary>
/// Internal DTO for mapping process_work_batch function results.
/// Matches the function's return type structure.
/// </summary>
internal class WorkBatchRow {
  [Column("instance_rank")]
  public int? InstanceRank { get; set; }

  [Column("active_instance_count")]
  public int? ActiveInstanceCount { get; set; }

  [Column("source")]
  public required string Source { get; set; }  // 'outbox', 'inbox', 'receptor', 'perspective'

  [Column("work_id")]
  public Guid? WorkId { get; set; }  // message_id or event_work_id or processing_id (NULL for stream-only perspective rows)

  [Column("work_stream_id")]
  public Guid? StreamId { get; set; }

  [Column("partition_number")]
  public int? PartitionNumber { get; set; }  // Partition assignment for load balancing

  [Column("destination")]
  public string? Destination { get; set; }  // Topic name (outbox) or handler name (inbox)

  [Column("message_type")]
  public string? MessageType { get; set; }  // For outbox/inbox

  [Column("envelope_type")]
  public string? EnvelopeType { get; set; }  // For outbox work only

  [Column("message_data")]
  public string? MessageData { get; set; }

  [Column("metadata")]
  public string? Metadata { get; set; }  // JSONB as string

  [Column("status")]
  public int? Status { get; set; }  // MessageProcessingStatus flags (NULL for stream-only perspective rows)

  [Column("attempts")]
  public int? Attempts { get; set; }

  [Column("is_newly_stored")]
  public bool? IsNewlyStored { get; set; }

  [Column("is_orphaned")]
  public bool? IsOrphaned { get; set; }

  [Column("error")]
  public string? Error { get; set; }  // Error message (NULL if no error)

  [Column("failure_reason")]
  public int? FailureReason { get; set; }  // MessageFailureReason enum value (NULL if no failure)

  [Column("perspective_name")]
  public string? PerspectiveName { get; set; }  // NULL for non-perspective work
}

/// <summary>
/// Diagnostic DTO for querying perspective cursor state.
/// Used in ReportPerspectiveCompletionAsync to verify updates are persisting.
/// </summary>
internal class CheckpointDiagnostic {
  [Column("stream_id")]
  public Guid StreamId { get; set; }

  [Column("perspective_name")]
  public string PerspectiveName { get; set; } = string.Empty;

  [Column("status")]
  public short Status { get; set; }

  [Column("last_event_id")]
  public Guid? LastEventId { get; set; }

  [Column("error")]
  public string? Error { get; set; }
}

/// <summary>
/// DTO for querying perspective cursor info.
/// Used by GetPerspectiveCursorAsync.
/// </summary>
internal class CursorQueryResult {
  [Column("stream_id")]
  public Guid StreamId { get; set; }

  [Column("perspective_name")]
  public string PerspectiveName { get; set; } = string.Empty;

  [Column("status")]
  public short Status { get; set; }

  [Column("last_event_id")]
  public Guid? LastEventId { get; set; }

  [Column("rewind_trigger_event_id")]
  public Guid? RewindTriggerEventId { get; set; }

  [Column("last_commit_sequence")]
  public long? LastCommitSequence { get; set; }
}

/// <summary>
/// DTO for querying orphaned lifecycle events.
/// Used by GetOrphanedLifecycleEventsAsync.
/// </summary>
internal class OrphanedEventRow {
  [Column("event_id")]
  public Guid EventId { get; set; }

  [Column("stream_id")]
  public Guid StreamId { get; set; }

  [Column("event_data")]
  public string EventData { get; set; } = string.Empty;

  [Column("metadata")]
  public string? Metadata { get; set; }

  [Column("event_type")]
  public string EventType { get; set; } = string.Empty;

  [Column("scope")]
  public string? Scope { get; set; }
}

/// <summary>
/// Source-generated log messages for <see cref="EFCoreWorkCoordinator{TDbContext}"/>. Kept in a
/// non-generic class because the <c>[LoggerMessage]</c> source generator does not emit into generic
/// containing types.
/// </summary>
internal static partial class EFCoreWorkCoordinatorLog {
  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Failed to parse reconciliation scope JSON; the replayed lifecycle event will run without tenant/user scope.")]
  public static partial void ReconcileScopeParseFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 74, Level = LogLevel.Warning,
    Message = "Handler-commit batch of {HandlerCount} fell back from the bulk tier to per-handler savepoints: {BulkError} — "
            + "sustained fallbacks mean every commit pays the slow path; the SQLSTATE names why")]
  public static partial void CommitBulkTierFellBack(ILogger logger, int handlerCount, string bulkError);
}
