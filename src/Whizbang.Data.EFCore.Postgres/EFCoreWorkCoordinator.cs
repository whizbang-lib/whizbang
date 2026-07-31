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
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithMetadata_StoresMetadataCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_TwoInstances_DistributesPartitionsViaModuloAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ThreeInstances_DistributesPartitionsViaModuloAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CrossInstanceStreamOrdering_PreventsClaimingWhenEarlierMessagesHeldAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletionWithStatusZero_DoesNotChangeStatusFlagsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StreamBasedFailureCascade_ReleasesLaterMessagesInSameStreamAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ClearedLeaseMessages_BecomeAvailableForOtherInstancesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_UnitOfWorkPattern_ProcessesCompletionsAndFailuresInSameCallAsync</tests>
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
  public async Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "record_heartbeat");

#pragma warning disable S2077
    var sql = $"SELECT {functionName}({{0}}, {{1}}, {{2}}, {{3}}, {{4}}::jsonb)";
#pragma warning restore S2077

    var metadataJson = request.Metadata is { } meta
      ? meta.GetRawText()
      : "{}";

    await _dbContext.Database.ExecuteSqlRawAsync(sql,
      [request.InstanceId, request.ServiceName, request.HostName, request.ProcessId, metadataJson],
      cancellationToken);
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
    var settings = "wh_settings"; // public (created bare, mig 028) — NOT the service schema; see a6ca8dd4
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
    var settings = "wh_settings"; // public (created bare, mig 028) — NOT the service schema; see a6ca8dd4
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

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      $"SELECT source, work_id, work_stream_id, partition_number, destination, message_type, " +
      $"envelope_type, message_data, metadata, status, attempts, is_newly_stored, is_orphaned, " +
      $"perspective_name FROM {functionName}(@p_id, @p_svc, @p_host, @p_pid, @p_max, @p_part, @p_lease)";
    cmd.Parameters.Add(new NpgsqlParameter("p_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = request.InstanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_svc", NpgsqlTypes.NpgsqlDbType.Text) { Value = request.ServiceName });
    cmd.Parameters.Add(new NpgsqlParameter("p_host", NpgsqlTypes.NpgsqlDbType.Text) { Value = request.HostName });
    cmd.Parameters.Add(new NpgsqlParameter("p_pid", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.ProcessId });
    cmd.Parameters.Add(new NpgsqlParameter("p_max", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.MaxStreams });
    cmd.Parameters.Add(new NpgsqlParameter("p_part", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.PartitionCount });
    cmd.Parameters.Add(new NpgsqlParameter("p_lease", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.LeaseSeconds });

    var rows = new List<WorkBatchRow>();
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
      InboxStreamIds = inboxStreamIds
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
    cmd.CommandText = $"SELECT handler_id, success, error_message FROM {functionName}(@p_results::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_results", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = batchJson });

    var results = new List<HandlerBatchResult>(requests.Count);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new HandlerBatchResult(
        HandlerId: reader.GetGuid(0),
        Success: reader.GetBoolean(1),
        ErrorMessage: reader.IsDBNull(2) ? null : reader.GetString(2)));
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
  /// Stream-integrity R1a selection (see <see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/>).
  /// Joins the event body so reaped ephemeral events are excluded structurally, filters
  /// at-most-once occurrences by their envelope-metadata delivery guarantee, and returns rows
  /// ordered (stream, version) under a hard LIMIT.
  /// </summary>
  /// <docs>proposals/stream-integrity</docs>
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
        AND ((@p_types::text[]) IS NULL OR es.event_type = ANY(@p_types::text[]))
        AND ((@p_streams::uuid[]) IS NULL OR es.stream_id = ANY(@p_streams::uuid[]))
        AND (@p_from_seq::bigint IS NULL OR es.commit_sequence > @p_from_seq)
        AND (@p_to_seq::bigint IS NULL OR es.commit_sequence <= @p_to_seq)
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

    // For each event type that has registered perspectives, find events where:
    // 1. The event was created within the lookback window
    // 2. All expected perspectives have cursor.last_event_id >= event.event_id (UUIDv7 ordering)
    // 3. No lifecycle completion marker exists
    foreach (var (eventTypeKey, expectedPerspectives) in perspectivesPerEventType) {
      if (expectedPerspectives.Count == 0) {
        continue;
      }

#pragma warning disable S2077
      var sql = $@"
        SELECT e.event_id, e.stream_id, eb.event_data AS event_data, eb.metadata AS metadata, e.event_type, e.scope
        FROM {eventStoreTable} e
        LEFT JOIN {bodyTable} eb ON eb.event_id = e.event_id
        WHERE e.event_type = {{0}}
          AND e.created_at >= {{1}}
          AND NOT EXISTS (
            SELECT 1 FROM {completionsTable} lc WHERE lc.event_id = e.event_id
          )
          AND (
            SELECT COUNT(DISTINCT pc.perspective_name)
            FROM {cursorsTable} pc
            WHERE pc.stream_id = e.stream_id
              AND pc.perspective_name = ANY({{2}})
              AND pc.last_event_id >= e.event_id
          ) = {{3}}
        ORDER BY e.created_at
        LIMIT 100";
#pragma warning restore S2077

      var perspectiveNamesArray = expectedPerspectives.ToArray();

      try {
        var rows = await _dbContext.Database
          .SqlQueryRaw<OrphanedEventRow>(sql, eventTypeKey, cutoff, perspectiveNamesArray, expectedPerspectives.Count)
          .ToListAsync(cancellationToken);

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
          _logger.LogWarning(ex, "Failed to query orphaned lifecycle events for type {EventType}", eventTypeKey);
        }
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
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetOrphanedLifecycleEventsAsync_DeserializesOrphanedEvent_AsJsonElementAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetOrphanedLifecycleEventsAsync_FallsBackToJsonDocumentParse_WhenTypeResolverFailsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:GetOrphanedLifecycleEventsAsync_DeserializesEventData_WithTypeDiscriminatorAsync</tests>
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
  /// <inheritdoc />
  public async Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) {
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCommand();
    cmd.CommandText = "SELECT service_id FROM wh_service_config LIMIT 1";
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result switch {
      null or DBNull => Guid.Empty,
      Guid g => g,
      _ => Guid.Empty
    };
  }

  /// <inheritdoc />
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Reader hydration covers the schema-shape drift between deployed migrations (older column set vs newer columns: commit_sequence + scope + envelope_type all have try-GetOrdinal fallbacks). The branches mirror migration adoption sequencing.")]
  public async Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
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
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_ids, @p_instance_id, @p_max_per_stream)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamArr });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_max_per_stream", maxPerStream));

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
  public async Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
    IReadOnlyList<Guid> streamIds,
    Guid instanceId,
    int maxPerStream = 100,
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
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_ids, @p_instance_id, @p_max_per_stream)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamArr });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_max_per_stream", maxPerStream));

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
}
