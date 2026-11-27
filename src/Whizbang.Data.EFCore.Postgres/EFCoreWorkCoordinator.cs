// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Priority;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IWorkCoordinator for lease-based work coordination.
/// Each operation calls its own focused PostgreSQL function (claim_work, store_outbox_messages,
/// store_inbox_messages, commit_handler_result, flush_completions, record_heartbeat and others).
/// </summary>
/// <typeparam name="TDbContext">DbContext type containing outbox, inbox, and service instance tables</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Work coordinator diagnostic logging - I/O bound database operations where LoggerMessage overhead isn't justified")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1845:Use span-based 'string.Concat'", Justification = "Debug logging with substring truncation - span-based operations not worth complexity for diagnostic output")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Schema-qualified identifiers cannot be parameterized, so the schema is interpolated. It is escaped through PgIdentifier, which doubles any embedded quote, so an identifier cannot be terminated early no matter where the schema came from. All values are bound parameters. Previously this justification rested on the schema being a developer constant, which does not hold under per-schema multi-tenancy.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType intentionally supports `Array | Uuid`, `Array | Integer`, etc. per the Npgsql API design — the Array bit is combined with the element type. The enum is not marked [Flags] upstream but the API expects bitwise composition.")]
#pragma warning disable S107 // Optional collaborators, each defaulted; DI supplies what is registered.
public class EFCoreWorkCoordinator<TDbContext>(
  TDbContext dbContext,
  JsonSerializerOptions jsonOptions,
  ILogger<EFCoreWorkCoordinator<TDbContext>>? logger = null,
  WorkCoordinatorMetrics? metrics = null,
  WorkCoordinatorGate? gate = null,
  IServiceInstanceProvider? instanceProvider = null,
  Microsoft.Extensions.Options.IOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>? coreOptions = null,
  Whizbang.Core.Validation.GuidOrderingValidator? guidOrdering = null,
  PartitionTenures? tenures = null
) : IWorkCoordinator
  where TDbContext : DbContext {
#pragma warning restore S107
  // #2528: every outbox and inbox write checks its message ids before any SQL runs.
  private readonly Whizbang.Core.Validation.GuidOrderingValidator _guidOrdering =
    guidOrdering ?? Whizbang.Core.Validation.GuidOrderingValidator.Default;
  private const string OUTBOX_ENTRY_POINT = "EFCoreWorkCoordinator.OutboxWrite";
  private const string INBOX_ENTRY_POINT = "EFCoreWorkCoordinator.InboxWrite";
  private const string DEFAULT_SCHEMA = "public";
  private const string PERSPECTIVE_CURSORS_TABLE = "wh_perspective_cursors";
  // #1734: each perspective's live cursor, else the one a whole-stream archive parked (migration 280).
  private const string RESUME_CURSORS_FUNCTION = "wh_perspective_resume_cursors";
  private const string OUTBOX_TABLE = "wh_outbox";
  private const string P_MAX_ATTEMPTS = "p_max_attempts";
  private const string PARAM_INSTANCE_ID = "p_instance_id";

  // Bound-parameter names shared by the hand-written commands below. They mirror the
  // argument names of the SQL functions in the migrations; the SQL text itself still
  // spells them inline as @p_..., because a parameter marker inside a query string is
  // part of the query, not a value.
  private const string P_EVENT_IDS = "p_event_ids";
  private const string P_IDS = "p_ids";
  private const string P_KEY = "p_key";
  private const string P_FLOOR_KEY = "p_floor_key";
  private const string P_FLOOR = "p_floor";
  private const string P_LIMIT = "p_limit";
  private const string P_MAX = "p_max";
  private const string P_NOW = "p_now";
  private const string P_LOOKBACK = "p_lookback";
  private const string P_ORIGIN = "p_origin";
  private const string P_ORIGIN_SERVICE_ID = "p_origin_service_id";
  private const string P_SETTLE = "p_settle";
  private const string P_STREAM_ID = "p_stream_id";
  private const string P_STREAM_IDS = "p_stream_ids";
  private const string P_TYPES = "p_types";
  private const string P_EVENT_TYPES = "p_event_types";
  private const string P_SINCE = "p_since";
  private const string P_STREAMS = "p_streams";
  private const string P_FROM = "p_from";
  private const string P_TO = "p_to";
  private const string P_TENANTS = "p_tenants";

  // Unqualified object names; every use goes through BuildSchemaQualifiedName so the
  // caller's schema is applied and escaped.
  private const string EVENT_STORE_TABLE = "wh_event_store";
  private const string EVENT_BODY_TABLE = "wh_event_body";
  private const string SETTINGS_TABLE = "wh_settings";
  private const string NORMALIZE_EVENT_TYPE_FUNCTION = "normalize_event_type";

  // The event-store flag bit re-delivery leaves out (#1484): an at-most-once event is owed to no one. Written into the
  // SQL as a literal.
  private const int AT_MOST_ONCE_FLAG = (int)EventFlags.AtMostOnce;


  // The payload builders below write JSON by hand, so they need the lower-case spellings:
  // StringBuilder.Append(bool) would emit "True"/"False", which no JSON reader accepts.
  private const string JSON_TRUE = "true";
  private const string JSON_FALSE = "false";

  // Slice 5 of zero-idle-polling — opportunistic heartbeat update inside
  // CompleteOutboxPublishedAsync skips when the freshness guard says the row
  // was UPDATEd within this many seconds. Same value as the SQL-side guard
  // in migration 010 (register_instance_heartbeat) so the two paths agree
  // on what "fresh" means.
  private const int OPPORTUNISTIC_HEARTBEAT_FRESHNESS_SECONDS = 10;

  private readonly TDbContext _dbContext = _initDbContext(dbContext);
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<EFCoreWorkCoordinator<TDbContext>>? _logger = logger;
  // The integrity paths' fail-open warnings, written to a logger that is always there: a coordinator built
  // without one discards them, exactly as the null-conditional calls it replaces did.
  private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;
  private readonly WorkCoordinatorMetrics? _metrics = metrics;
  // #2556: the process's tenure ledger. Claims and fetches record into it; every write that commits a result names the
  // epoch its work was claimed under from it, so no worker has to carry the epoch.
  private readonly PartitionTenures? _tenures = tenures;
  // #2256: the ledger functions answer how many buckets each call opened and closed; recorded here, the one place that
  // calls them, so every caller is counted. Reached through the coordinator's metrics; null when either is not registered.
  private readonly Whizbang.Core.Observability.StreamIntegrityMetrics? _integrityMetrics = metrics?.Integrity;
  private readonly WorkCoordinatorGate? _gate = gate;
  private readonly IServiceInstanceProvider? _instanceProvider = instanceProvider;
  // v0.657 slice 2: enforce EmptyStreamIdPolicy at StoreOutboxMessagesAsync /
  // StoreInboxMessagesAsync time. When the options aren't wired into DI,
  // default to the same Reject value as WhizbangCoreOptions itself.
  private readonly Whizbang.Core.Configuration.EmptyStreamIdPolicy _emptyStreamIdPolicy =
    coreOptions?.Value.EmptyStreamIdPolicy ?? Whizbang.Core.Configuration.EmptyStreamIdPolicy.Reject;

  private static TDbContext _initDbContext(TDbContext ctx) {
    ArgumentNullException.ThrowIfNull(ctx);
    ctx.Database.SetCommandTimeout(TimeSpan.FromSeconds(CoordinatorCommandExtensions.COORDINATOR_COMMAND_TIMEOUT_SECONDS));
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
  /// Resolves the schema the coordinator's SQL runs in from the schema the model maps
  /// <paramref name="entityType"/> to, falling back to the default schema when the model does not map
  /// that type or maps it without a schema.
  /// </summary>
  /// <remarks>
  /// Every coordinator call resolves its schema through here, so the "entity not in the model" outcome
  /// is decided, logged and tested once rather than at each of the call sites.
  /// </remarks>
  /// <param name="model">The model of the coordinator's <c>DbContext</c>.</param>
  /// <param name="entityType">The Whizbang entity whose table the call targets.</param>
  /// <param name="logger">Optional logger for the fallback warning.</param>
  /// <returns>The mapped schema, or the default schema.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorSchemaTests.cs</tests>
  internal static string ResolveSchema(
    Microsoft.EntityFrameworkCore.Metadata.IModel model,
    [DynamicallyAccessedMembers(EntityTypeTrimming.MEMBERS)] Type entityType,
    ILogger<EFCoreWorkCoordinator<TDbContext>>? logger) {
    ArgumentNullException.ThrowIfNull(model);
    return GetSchemaWithFallback(model.FindEntityType(entityType)?.GetSchema(), DEFAULT_SCHEMA, logger);
  }

  private string _resolveSchema([DynamicallyAccessedMembers(EntityTypeTrimming.MEMBERS)] Type entityType) => ResolveSchema(_dbContext.Model, entityType, _logger);

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
    return $"{Whizbang.Data.Postgres.PgIdentifier.Quote(schema)}.{identifier}";
  }
#pragma warning restore RCS1158

  private string _serializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) { return "[]"; }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageFailure[]));
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  // Every outbox write funnels through here, so the time-ordering check refuses a non-v7 message id before any SQL runs
  // and nothing of the batch is written (#2528).
  private string _serializeNewOutboxMessages(OutboxMessage[] messages) {
    if (messages.Length == 0) { return "[]"; }
    foreach (var message in messages) {
      _guidOrdering.ValidateMessageId(message.MessageId, message.MessageType, OUTBOX_ENTRY_POINT);
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(OutboxMessage[]));
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  // Every inbox write funnels through here; the same check, before any SQL runs (#2528).
  private string _serializeNewInboxMessages(InboxMessage[] messages) {
    if (messages.Length == 0) { return "[]"; }
    foreach (var message in messages) {
      _guidOrdering.ValidateMessageId(message.MessageId, message.MessageType, INBOX_ENTRY_POINT);
      if (message.StreamId is { } streamId) {
        // A remote producer's stream id may reach this service only here (#2549): reported, or refused at Error
        // before any SQL runs, resolved through the message type's stored name
        _guidOrdering.ReportStreamId(streamId, message.MessageType, INBOX_ENTRY_POINT);
      }
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]));
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  private string _serializePerspectiveCompletions(PerspectiveCursorCompletion[] completions) {
    if (completions.Length == 0) { return "[]"; }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCursorCompletion[]));
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <summary>
  /// Reports perspective cursor completion directly (out-of-band).
  /// Calls complete_perspective_cursor_work SQL function directly without full work batch processing.
  /// Creates its own database connection to allow calling after the scoped DbContext is disposed.
  /// </summary>
  /// <inheritdoc />
  public async Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "deregister_instance");

#pragma warning disable S2077
    var sql = $"SELECT {functionName}({{0}})";
#pragma warning restore S2077

    await _dbContext.Database.ExecuteSqlRawAsync(sql, [instanceId], cancellationToken);
  }

  /// <inheritdoc />
  public async ValueTask<OutstandingWork?> CountOutstandingWorkAsync(
      Guid instanceId, CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "count_outstanding_work");

    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
#pragma warning disable S2077
    cmd.CommandText = $"SELECT inbox_rows, outbox_rows, perspective_rows FROM {functionName}(@instanceId)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue(nameof(instanceId), instanceId);

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
  /// The sweep already guards its purge on <c>debug_mode</c>, but nothing wrote that row, so the
  /// documented option marked rows in process and the sweep deleted them anyway on its next pass.
  /// Both values are written: leaving a stale true would disable the purge permanently. Written by the
  /// same statement as <see cref="SyncRetentionSettingsAsync"/>, which is what the maintenance cycle calls.
  /// </remarks>
  /// <param name="debugMode">Whether completed rows should be retained.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the setting is stored.</returns>
  public Task SyncDebugRetentionSettingAsync(bool debugMode, CancellationToken cancellationToken = default)
    => SyncRetentionSettingsAsync([Whizbang.Core.Workers.RetentionSettingsBridge.DebugModeSetting(debugMode)], cancellationToken);

  /// <inheritdoc />
  /// <summary>
  /// Publishes debug retention and every configured retention window into <c>wh_settings</c> in one statement,
  /// where the maintenance sweep reads them.
  /// </summary>
  /// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetentionSettingsSyncSqlTests.cs</tests>
  public async Task SyncRetentionSettingsAsync(
      IReadOnlyList<Whizbang.Core.Workers.RetentionSetting> settings, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(settings);
    var schema = _resolveSchema(typeof(OutboxRecord));
    var table = BuildSchemaQualifiedName(schema, SETTINGS_TABLE);

    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await Whizbang.Data.Postgres.RetentionSettingsWriter.WriteAsync(scope.Connection, table, settings, cancellationToken)
      .ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <docs>fundamentals/events/archive-store#the-policy-per-stream-type</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveRestoreRequestSqlTests.cs:SyncPolicies_ThroughTheCoordinator_StoresTheConfiguredSetAsync</tests>
  public async Task SyncStreamTypePoliciesAsync(
      IReadOnlyList<Whizbang.Core.Archival.StreamTypePolicy> policies, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(policies);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await Whizbang.Data.Postgres.StreamTypePolicyWriter.WriteAsync(
        scope.Connection, BuildSchemaQualifiedName(schema, Whizbang.Data.Postgres.StreamTypePolicyWriter.FUNCTION_NAME), policies,
        cancellationToken).ConfigureAwait(false);
  }

  public async ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = scope.Connection.CreateCoordinatorCommand();
    // 162: every inbox column the backlog reads is on wh_inbox_state, so it never touches the message row. No row
    // means the query answered nothing, not that the service is settled: null keeps the caller's gate closed.
    return await CoordinatorFleetSql.CountServiceBacklogAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HousekeepingDeferralSqlTests.cs:TheFirstDeferral_IsReadWithTheBacklog_AndTheFirstWriterWinsAsync</tests>
  public async ValueTask RecordHousekeepingDeferralAsync(
      string budgetKey, DateTimeOffset firstDeferredAt, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(budgetKey);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = scope.Connection.CreateCoordinatorCommand();
    _ = await CoordinatorFleetSql.RecordHousekeepingDeferralAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), budgetKey, firstDeferredAt, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HousekeepingDeferralSqlTests.cs:AClear_RemovesOnlyItsOwnBudgetAsync</tests>
  public async ValueTask ClearHousekeepingDeferralAsync(string budgetKey, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(budgetKey);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = scope.Connection.CreateCoordinatorCommand();
    _ = await CoordinatorFleetSql.ClearHousekeepingDeferralAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), budgetKey, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "record_heartbeat");

    // #2323: the metadata with this build's stored form level, which the trim gate counts.
    var metadataJson = request.MetadataJson();

    // record_heartbeat returns BOOLEAN (migration 106): false means this instance_id has been
    // tombstoned in wh_instance_evictions and the caller must stop heartbeating. ExecuteSqlRawAsync
    // discards the function's own return value (it reports affected ROWS, not the result), so the
    // scalar has to be read directly — the same pattern already used for other scalar-returning
    // functions on this coordinator (see NotifyScheduledRetryDueAsync).
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
#pragma warning disable S2077
    cmd.CommandText = $"SELECT {functionName}(@instanceId, @serviceName, @hostName, @processId, @metadata::jsonb, @phase, @version, @staleSeconds, @connectionMode, @applicationVersion)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("instanceId", request.InstanceId);
    cmd.Parameters.AddWithValue("serviceName", request.ServiceName);
    cmd.Parameters.AddWithValue("hostName", request.HostName);
    cmd.Parameters.AddWithValue("processId", request.ProcessId);
    cmd.Parameters.AddWithValue("metadata", metadataJson);
    // Migration 147: the beat carries the current phase and version so a registry row created
    // before the first phase transition landed is backfilled, and the stale threshold the writer's
    // cadence implies for the opportunistic peer reap. Nulls keep the SQL defaults.
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("phase", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)request.LifecyclePhase ?? DBNull.Value });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("version", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)request.LibraryVersion ?? DBNull.Value });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("staleSeconds", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)request.StaleThresholdSeconds ?? DBNull.Value });
    // 203 (#1254): how this instance reaches the database, so the partition assigner judges it by the right signal.
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("connectionMode", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = request.ConnectionMode is { } mode ? Whizbang.Core.Workers.InstanceConnectionModes.ToDatabaseValue(mode) : DBNull.Value
    });
    // 316 (#2027): the application version, recorded at registration like the library version.
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("applicationVersion", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)request.ApplicationVersion ?? DBNull.Value });

    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return ScalarResult.IsTrue(result);
  }

  /// <inheritdoc />
  /// <remarks>
  /// #2126: through <c>wh_record_startup_heartbeat</c>, which Preflight's bootstrap closure creates, so it answers from
  /// the moment the instance registers, before the main pass has created <c>record_heartbeat</c> or replaced an earlier
  /// release's.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartingInstanceHeartbeatE2ETests.cs</tests>
  public async Task<bool> RecordStartupHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    // Not behind the work gate: a starting instance's liveness must not queue behind the startup work it reports on.
    var functionName = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), Whizbang.Data.Postgres.StartupHeartbeatSql.FUNCTION_NAME);
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    return await Whizbang.Data.Postgres.StartupHeartbeatSql.RecordAsync(scope.Connection, functionName, request, cancellationToken)
      .ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// #2126: through <c>wh_record_instance_exit</c>, which Preflight's bootstrap closure creates, so an instance evicted
  /// while it is still starting records its exit too. Outside the work gate: an exit must not queue behind the work it is
  /// leaving.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InstanceExitRecordSqlTests.cs</tests>
  public async Task<Whizbang.Core.RunControl.InstanceExitRecordResult> RecordInstanceExitAsync(
      Whizbang.Core.RunControl.InstanceExitRecord exitRecord, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(exitRecord);
    var functionName = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), Whizbang.Data.Postgres.InstanceExitSql.FUNCTION_NAME);
    await using var scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    return await Whizbang.Data.Postgres.InstanceExitSql.RecordAsync(scope.Connection, functionName, exitRecord, cancellationToken)
      .ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<DateTimeOffset?> GetOldestSequenceHeldOutboxAsync(
    Guid instanceId,
    int maxRows,
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "wh_oldest_sequence_held_outbox");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // the function name is schema-qualified from the resolved schema, never user input
    cmd.CommandText = $"SELECT {functionName}(@instance_id, @max_rows)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("instance_id", instanceId);
    cmd.Parameters.AddWithValue("max_rows", maxRows);
    return ScalarResult.InstantOrNull(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  /// <inheritdoc />
  public async Task<int> NotifyScheduledRetryDueAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "notify_scheduled_retry_due");

#pragma warning disable S2077
    var sql = $"SELECT COALESCE(SUM(stream_count), 0)::int FROM {functionName}()";
#pragma warning restore S2077

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = sql;
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    var due = ScalarResult.IntOrZero(result);
    if (due > 0) {
      // #720: the probe queued a doorbell per due stream; ring them after its commit.
      await DoorbellRinger.RingAsync(conn, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
    }
    return due;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PartitionHandover>> GetPartitionsHandingOverAsync(
    Guid instanceId, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT partition_number, epoch, next_owner_instance_id FROM {BuildSchemaQualifiedName(schema, "wh_partitions_handing_over")}(@p_instance_id)";
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = instanceId });
    var handovers = new List<PartitionHandover>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      handovers.Add(new PartitionHandover(reader.GetInt32(0), reader.GetInt64(1), reader.GetGuid(2)));
    }
    return handovers;
  }

  /// <inheritdoc />
  public async Task<PartitionLeaveStatus> GetLeaveStatusAsync(Guid instanceId, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var prefix = Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(_resolveSchema(typeof(OutboxRecord)));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.CommandText = PartitionLeaveSql.Status(prefix, "@p_instance_id");
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = instanceId });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    await reader.ReadAsync(cancellationToken);
    return new PartitionLeaveStatus(reader.GetInt32(0), reader.GetInt32(1));
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<int>> AcknowledgeHandoverAsync(
    Guid instanceId, IReadOnlyList<PartitionHandover> handovers, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(handovers);
    if (handovers.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT partition_number FROM {BuildSchemaQualifiedName(schema, "wh_acknowledge_handover")}(@p_instance_id, @p_partitions, @p_epochs)";
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_partitions", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = handovers.Select(h => h.Partition).ToArray()
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_epochs", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = handovers.Select(h => h.Epoch).ToArray()
    });
    var flipped = new List<int>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      flipped.Add(reader.GetInt32(0));
    }
    return flipped;
  }

  /// <inheritdoc />
  public Task<int> CompleteOutboxPublishedAsync(
    IReadOnlyList<Guid> ids, bool debugMode, CancellationToken cancellationToken = default) =>
    CompleteOutboxPublishedAsync(ids, epochs: null, debugMode, cancellationToken);

  /// <inheritdoc />
  public async Task<int> CompleteOutboxPublishedAsync(
    IReadOnlyList<Guid> ids, IReadOnlyList<long?>? epochs, bool debugMode, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ids);
    if (ids.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    // 570 (#2556): complete_outbox_published keeps its two parameters; the fenced mark is its own function, which fences
    // the ids and hands the rest to it.
    var functionName = BuildSchemaQualifiedName(schema, "wh_complete_outbox_published_fenced");

    var idArray = AsArray(ids);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_ids, @p_debug_mode, @p_epochs)";
    cmd.Parameters.Add(new NpgsqlParameter(P_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_debug_mode", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = debugMode });
    // 570 (#2556): the epoch each id was claimed under; a completion under one that moved on is fenced and reported.
    cmd.Parameters.Add(EpochsParameter("p_epochs", TenureFill.Epochs(_tenures, ids, epochs)));
    object? result;
    using (Whizbang.Data.Postgres.CoordinatorOwnershipNotices.WatchFencedWrites(conn, _metrics, _logger, nameof(CompleteOutboxPublishedAsync))) {
      result = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    TenureFill.Forget(_tenures, ids);
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

    var schema = _resolveSchema(typeof(OutboxRecord));

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorRetentionSql.ReclassifyEventsEphemeralAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), eventTypeNames, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<long> CountSourcedEventsForTypesAsync(
    IReadOnlyList<string> eventTypeNames, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeNames);
    if (eventTypeNames.Count == 0) {
      return 0L;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorCatalogSql.CountSourcedEventsForTypesAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), eventTypeNames, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<TypeDefinitionInfo>> GetTypeDefinitionsAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorCatalogSql.TypeDefinitionsAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
    string eventTypeName, string settingsHashHex, string schemaHashHex, int schemaVersion,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorCatalogSql.RegisterTypeDefinitionAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), eventTypeName, settingsHashHex, schemaHashHex, schemaVersion, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task RecordDefinitionLineageAsync(
    int fromDefinitionId, int toDefinitionId, DefinitionRelationship relationship, string? migrationRef,
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await CoordinatorCatalogSql.RecordDefinitionLineageAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), fromDefinitionId, toDefinitionId, relationship, migrationRef, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyCollection<Guid>> GetStateBasedStreamIdsAsync(
    IReadOnlyList<Guid> streamIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    var eventStore = BuildSchemaQualifiedName(schema, EVENT_STORE_TABLE);
    var ids = AsArray(streamIds);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
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
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:StateBased_IgnoringTheAllowedEphemeralType_LeavesTheMixedStreamRebuildableAsync</tests>
  public async Task<IReadOnlyCollection<Guid>> GetStateBasedStreamIdsAsync(
    IReadOnlyList<Guid> streamIds, IReadOnlyList<string> ignoredEphemeralTypes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    ArgumentNullException.ThrowIfNull(ignoredEphemeralTypes);
    if (streamIds.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorMixedStreamSql.StateBasedStreamIdsAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), streamIds, ignoredEphemeralTypes, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs:Sync_ReplacesThePairs_AndAnEmptyListClearsThemAsync</tests>
  public async Task SyncMixedStreamAllowancesAsync(IReadOnlyList<Whizbang.Core.Perspectives.MixedStreamPair> pairs, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(pairs);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await CoordinatorMixedStreamSql.SyncAllowancesAsync(cmd, PgIdentifier.QualifyPrefix(schema), pairs, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task SyncEphemeralTypeGraceAsync(
    IReadOnlyList<EphemeralTypeGrace> graceOverrides, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(graceOverrides);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await CoordinatorRetentionSql.SyncEphemeralTypeGraceAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), graceOverrides, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task SyncPerspectiveRetentionAsync(
    IReadOnlyList<PerspectiveRetentionDeclaration> declarations, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(declarations);
    if (declarations.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    // The function returns the rows it matched. A declaration that matches no registry row is a
    // key drift (issue #697: the registry was keyed in a display-string form the runtime never
    // looked up, and every nested model sat silently un-enrolled), so zero is a warning that
    // names the declaration and the key, never a swallowed result.
    await PerspectiveRowRetentionSql.SyncRetentionAsync(
      () => conn.CreateCoordinatorCommand(), PgIdentifier.QualifyPrefix(schema),
      declarations, _reportUnmatchedRetentionDeclaration, cancellationToken).ConfigureAwait(false);
  }

  private void _reportUnmatchedRetentionDeclaration(string clrTypeName) {
    if (_logger is not null) {
      EFCoreWorkCoordinatorLog.RetentionDeclarationMatchedNoRegistryRow(_logger, clrTypeName);
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<EphemeralSnapshotTarget>> GetEphemeralPairsNeedingSnapshotAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorCatalogSql.EphemeralPairsNeedingSnapshotAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>The function answers an outcome (migration 399): 0 not reported, 1 reported, 2 reported and the row
  /// recorded, 3 reported and the unhealable mark lifted; the last two are counted as buckets opened (#2256).</remarks>
  public async Task<bool> IntegrityTryBeginReportAsync(
      IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    _flagFromOutcome(await _integrityLedgerOutcomeAsync("wh_integrity_report_outcome", cmd => {
      _bindDivergenceKey(cmd, key);
      cmd.Parameters.AddWithValue("p_origin_lo", originLo);
      cmd.Parameters.AddWithValue("p_origin_hi", originHi);
      cmd.Parameters.AddWithValue("p_local_lo", localLo);
      cmd.Parameters.AddWithValue("p_local_hi", localHi);
      cmd.Parameters.AddWithValue(P_NOW, now);
      cmd.Parameters.AddWithValue("p_cooldown_seconds", (int)cooldown.TotalSeconds);
    }, failOpen: true, cancellationToken).ConfigureAwait(false), failOpen: true);

  /// <inheritdoc />
  /// <remarks>The function answers an outcome (migration 399): 0 refused, 1 granted, 2 granted and the row recorded,
  /// counted as a bucket opened (#2256).</remarks>
  public async Task<bool> IntegrityTryBeginRepairAsync(
      IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      CancellationToken cancellationToken = default) =>
    _flagFromOutcome(await _integrityLedgerOutcomeAsync("wh_integrity_repair_outcome", cmd => {
      _bindDivergenceKey(cmd, key);
      cmd.Parameters.AddWithValue(P_NOW, now);
      cmd.Parameters.AddWithValue("p_base_backoff_secs", (int)baseBackoff.TotalSeconds);
      cmd.Parameters.AddWithValue(P_MAX_ATTEMPTS, maxAttempts);
    }, failOpen: false, cancellationToken).ConfigureAwait(false), failOpen: false);

  /// <inheritdoc />
  /// <remarks>The function answers the rows it removed that were still open (migration 399), counted as healed.</remarks>
  public async Task IntegrityMarkHealedAsync(
      IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
    _recordClosed(await _integrityLedgerOutcomeAsync("wh_integrity_heal_counted",
      cmd => _bindDivergenceKey(cmd, key), failOpen: false, cancellationToken).ConfigureAwait(false) ?? 0,
      Whizbang.Core.Observability.StreamIntegrityMetrics.CLOSED_HEALED);

  // The single-key outcome (migration 399) as the flag callers read, counting an opened or reopened bucket on the way.
  // An unreadable outcome (the call failed) is the caller's fail-open or fail-closed answer, and counts nothing.
  private bool _flagFromOutcome(int? outcome, bool failOpen) {
    if (outcome is not { } known) {
      return failOpen;
    }
    _recordOpened(known == LEDGER_OUTCOME_OPENED ? 1 : 0, Whizbang.Core.Observability.StreamIntegrityMetrics.OPENED_NEW);
    _recordOpened(known == LEDGER_OUTCOME_REOPENED ? 1 : 0, Whizbang.Core.Observability.StreamIntegrityMetrics.OPENED_REOPENED);
    return known > 0;
  }

  // The outcomes of wh_integrity_report_outcome and wh_integrity_repair_outcome that record a bucket (399).
  private const int LEDGER_OUTCOME_OPENED = 2;
  private const int LEDGER_OUTCOME_REOPENED = 3;

  /// <summary>#2256: counts buckets the ledger opened, by how; nothing when none or when metrics are not registered.</summary>
  private void _recordOpened(long buckets, string how) {
    if (buckets > 0) {
      _integrityMetrics?.LedgerBucketsOpened.Add(buckets,
        new KeyValuePair<string, object?>(Whizbang.Core.Observability.StreamIntegrityMetrics.HOW_TAG, how));
    }
  }

  /// <summary>#2256: counts buckets the ledger closed, by how; nothing when none or when metrics are not registered.</summary>
  private void _recordClosed(long buckets, string how) {
    if (buckets > 0) {
      _integrityMetrics?.LedgerBucketsClosed.Add(buckets,
        new KeyValuePair<string, object?>(Whizbang.Core.Observability.StreamIntegrityMetrics.HOW_TAG, how));
    }
  }

  private static void _bindDivergenceKeyArrays(
      Npgsql.NpgsqlCommand cmd, Guid originServiceId,
      IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys) {
    cmd.Parameters.AddWithValue(P_ORIGIN_SERVICE_ID, originServiceId);
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_tenant_scopes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = keys.Select(k => k.TenantScope).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = keys.Select(k => k.EventType).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAM_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = keys.Select(k => k.StreamId).ToArray()
    });
  }

  /// <summary>The array sibling of <c>_integrityLedgerOutcomeAsync</c>: one batch function call answering the per-key
  /// flags (<c>granted</c>), the rows it recorded (<c>opened</c>) and, for the report batch, the unhealable marks it
  /// lifted (<c>reopened</c>), which are counted (#2256, migration 399). Null on ANY failure: the caller then loops the
  /// single-key path, whose per-operation fail-open/fail-closed semantics remain the authority.</summary>
  private async Task<IReadOnlyList<bool>?> _integrityLedgerBoolArrayAsync(
      string fn, Action<Npgsql.NpgsqlCommand> bind, CancellationToken ct) {
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
      var schema = _resolveSchema(typeof(OutboxRecord));
      var qualified = BuildSchemaQualifiedName(schema, fn);
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), ct);
      var conn = __scope.Connection;
      await using var cmd = conn.CreateCoordinatorCommand();
      bind(cmd);
      var args = string.Join(",", cmd.Parameters
        .Cast<Npgsql.NpgsqlParameter>()
        .Select(p => "@" + p.ParameterName));
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT * FROM {qualified}({args})";
#pragma warning restore S2077
      await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
      _ = await reader.ReadAsync(ct).ConfigureAwait(false);   // one row, always; a missing one throws below and falls back
      var granted = await reader.GetFieldValueAsync<bool[]>(0, ct).ConfigureAwait(false);
      _recordOpened(reader.GetInt32(1), Whizbang.Core.Observability.StreamIntegrityMetrics.OPENED_NEW);
      if (reader.FieldCount > 2) {
        _recordOpened(reader.GetInt32(2), Whizbang.Core.Observability.StreamIntegrityMetrics.OPENED_REOPENED);
      }
      return granted;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; falls back to the single-key functions.
      _log.LogWarning(ex,
        "Integrity ledger batch {Function} failed; falling back to single-key calls for this chunk.", fn);
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<bool>?> IntegrityTryBeginReportBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityReportObservation> observations,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
    // #1842: the batch that also records each key's divergence kind (251), which decides whether the
    // paced repair drain may claim the row, and (#2030, 320) its deficit size, so a grown deficit resets the ladder.
    // Arguments bind in the order they are added.
    _integrityLedgerBoolArrayAsync("wh_integrity_report_divergences_counted", cmd => {
      cmd.Parameters.AddWithValue(P_ORIGIN_SERVICE_ID, originServiceId);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_tenant_scopes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = observations.Select(o => o.Key.TenantScope).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = observations.Select(o => o.Key.EventType).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAM_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
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
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_is_deficits", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Boolean) {
        Value = observations.Select(o => o.IsDeficit).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_deficit_events", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = observations.Select(o => o.DeficitEvents).ToArray()
      });
      cmd.Parameters.AddWithValue(P_NOW, now);
      cmd.Parameters.AddWithValue("p_cooldown_seconds", (int)cooldown.TotalSeconds);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<bool>?> IntegrityTryBeginRepairBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants,
      CancellationToken cancellationToken = default) =>
    _integrityLedgerBoolArrayAsync("wh_integrity_repair_batch_counted", cmd => {
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
      cmd.Parameters.AddWithValue(P_NOW, now);
      cmd.Parameters.AddWithValue("p_base_backoff_secs", (int)baseBackoff.TotalSeconds);
      cmd.Parameters.AddWithValue(P_MAX_ATTEMPTS, maxAttempts);
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
      var schema = _resolveSchema(typeof(OutboxRecord));
      var qualified = BuildSchemaQualifiedName(schema, "wh_integrity_stamp_repair_windows");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
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
      _log.LogWarning(ex, "Integrity window stamp failed; the drain will derive a coarser range for these buckets.");
#pragma warning restore CA1848
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// The count stops at <paramref name="backlogCap"/> (a LIMITed subquery), so the read costs at most
  /// the bound however large the ledger is. Rows on the synthetic bulk lane (stream zero) are left out,
  /// exactly as the drain's claim leaves them out, and so are rows that are not deficits (#1842) and buckets
  /// marked unhealable (249), answered or exhausted (320): the drain never claims a mismatch, a surplus, a row of
  /// unknown kind, an unhealable, answered or exhausted bucket, so none of them is a queued repair. The count carries
  /// the claim's predicate, so it is answered from the claimable-rows index (320). A key is tracked when the ledger holds it as a deficit: a
  /// deficit found where the ledger holds a mismatch or a surplus is new to the drain, while an unhealable
  /// deficit stays tracked, so buckets repair cannot change never take the per-comparison allowance on every
  /// audit. Best-effort: a probe that cannot reach the ledger is logged and answers null, and the comparison
  /// falls back to its per-comparison cap.
  /// </remarks>
  public async Task<IntegrityRepairBacklogProbe?> IntegrityProbeRepairBacklogAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, int backlogCap,
      CancellationToken cancellationToken = default) {
    try {
      return await _withCoordinatorCommandAsync<IntegrityRepairBacklogProbe?>(async (cmd, schema) => {
        cmd.CommandText = $"""
          SELECT
            (SELECT COUNT(*) FROM (
               SELECT 1 FROM {schema}.wh_integrity_ledger
               WHERE is_deficit
                 AND stream_id <> '00000000-0000-0000-0000-000000000000'::uuid
                 AND unhealable_at IS NULL
                 AND answered_at IS NULL
                 AND exhausted_at IS NULL
               LIMIT @p_limit) b),
            ARRAY(
              SELECT EXISTS (
                SELECT 1 FROM {schema}.wh_integrity_ledger l
                WHERE l.origin_service_id = @p_origin_service_id
                  AND l.tenant_scope = COALESCE(k.scope, '')
                  AND l.event_type = k.event_type
                  AND l.stream_id = k.stream_id
                  AND l.is_deficit)
              FROM UNNEST(@p_tenant_scopes, @p_event_types, @p_stream_ids) WITH ORDINALITY AS k(scope, event_type, stream_id, ord)
              ORDER BY k.ord)
          """;
        _bindDivergenceKeyArrays((Npgsql.NpgsqlCommand)cmd, originServiceId, keys);
        ((Npgsql.NpgsqlCommand)cmd).Parameters.AddWithValue(P_LIMIT, backlogCap);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new IntegrityRepairBacklogProbe(reader.GetInt64(0), reader.GetFieldValue<bool[]>(1));
      }, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the comparison falls back to its per-comparison cap.
      _log.LogWarning(ex,
        "Integrity repair-backlog probe failed; this comparison admits deficits by its per-comparison cap alone.");
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  /// <remarks>False on any failure (logged), so the caller keeps its counts for the next tick; a canceled write is a
  /// stopping host and propagates.</remarks>
  public async Task<bool> IntegrityAddLedgerTallyAsync(
      DateTimeOffset at, long opened, long closed, DateTimeOffset keepSince, CancellationToken cancellationToken = default) {
    try {
      return await _withCoordinatorCommandAsync(async (cmd, schema) => {
        cmd.CommandText = $"SELECT {schema}.wh_integrity_ledger_tally_add(@p_at, @p_opened, @p_closed, @p_keep)";
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_at", at));
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_opened", opened));
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_closed", closed));
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_keep", keepSince));
        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
      }, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the counts are written on the next tick.
      _log.LogWarning(ex, "Integrity ledger tally write failed; this instance's opened and closed buckets are written on the next tick.");
#pragma warning restore CA1848
      return false;
    }
  }

  /// <inheritdoc />
  /// <remarks>Null on any failure (logged): the progress and ETA gauges report nothing rather than a zero.</remarks>
  public async Task<IntegrityLedgerTally?> GetIntegrityLedgerTallyAsync(DateTimeOffset since, CancellationToken cancellationToken = default) {
    try {
      return await _withCoordinatorCommandAsync<IntegrityLedgerTally?>(async (cmd, schema) => {
        cmd.CommandText = $"SELECT opened, closed FROM {schema}.wh_integrity_ledger_tally_since(@p_since)";
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_since", since));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);   // one row, always
        return new IntegrityLedgerTally(reader.GetInt64(0), reader.GetInt64(1));
      }, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the gauges report nothing until a read succeeds.
      _log.LogWarning(ex, "Integrity ledger tally read failed; repair progress and the backlog ETA report nothing this cycle.");
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  /// <remarks>#2377: one read per process start, of the open halts only (one per peer and side).</remarks>
  public Task<IReadOnlyList<IntegrityPairHaltRecord>?> IntegrityLoadPairHaltsAsync(CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<IReadOnlyList<IntegrityPairHaltRecord>?>(async (cmd, schema) => {
      cmd.CommandText = $"""
        SELECT peer, side, reason, differing_types, local_semantics_version, local_eligibility_sql_version,
               local_fingerprint, peer_semantics_version, peer_eligibility_sql_version, peer_fingerprint, halted_at, alerted_at
        FROM {schema}.wh_integrity_pair_halts WHERE resumed_at IS NULL ORDER BY peer, side
        """;
      var halts = new List<IntegrityPairHaltRecord>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        halts.Add(new IntegrityPairHaltRecord(
          reader.GetString(0), (IntegrityPairSide)reader.GetInt16(1), (Whizbang.Rules.RulePairHaltReason)reader.GetInt16(2),
          reader.GetFieldValue<string[]>(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6),
          reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
          reader.IsDBNull(9) ? null : reader.GetString(9),
          reader.GetFieldValue<DateTimeOffset>(10), reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11)));
      }
      return halts;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IntegrityPairHaltRecord?> IntegrityRecordPairHaltAsync(IntegrityPairHaltRecord halt, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(halt);
    return _withCoordinatorCommandAsync<IntegrityPairHaltRecord?>(async (cmd, schema) => {
      cmd.CommandText = $"""
        SELECT halted_at, alerted_at FROM {schema}.wh_integrity_pair_halt_record(
          @p_peer, @p_side, @p_reason, @p_types, @p_local_semantics, @p_local_sql, @p_local_fingerprint,
          @p_peer_semantics, @p_peer_sql, @p_peer_fingerprint, @p_at)
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_peer", halt.Peer));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_side", (short)halt.Side));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_reason", (short)halt.Reason));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_types", halt.DifferingTypes.ToArray()));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_local_semantics", halt.LocalSemanticsVersion));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_local_sql", halt.LocalEligibilitySqlVersion));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_local_fingerprint", halt.LocalFingerprint));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter<int?>("p_peer_semantics", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = halt.PeerSemanticsVersion });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter<int?>("p_peer_sql", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = halt.PeerEligibilitySqlVersion });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter<string?>("p_peer_fingerprint", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = halt.PeerFingerprint });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_at", halt.HaltedAt));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);   // one row, always
      return halt with {
        HaltedAt = reader.GetFieldValue<DateTimeOffset>(0),
        AlertedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
      };
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task IntegrityRecordPairHaltAlertAsync(string peer, IntegrityPairSide side, DateTimeOffset at, CancellationToken cancellationToken = default) =>
    _recordPairHaltStampAsync("wh_integrity_pair_halt_alerted", peer, side, at, cancellationToken);

  /// <inheritdoc />
  public Task IntegrityRecordPairResumedAsync(string peer, IntegrityPairSide side, DateTimeOffset at, CancellationToken cancellationToken = default) =>
    _recordPairHaltStampAsync("wh_integrity_pair_halt_resumed", peer, side, at, cancellationToken);

  private Task<bool> _recordPairHaltStampAsync(string function, string peer, IntegrityPairSide side, DateTimeOffset at, CancellationToken cancellationToken) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      cmd.CommandText = $"SELECT {schema}.{function}(@p_peer, @p_side, @p_at)";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_peer", peer));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_side", (short)side));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_at", at));
      _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IntegrityGapTrackerState?> IntegrityLoadGapTrackerStateAsync(CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<IntegrityGapTrackerState?>(async (cmd, schema) => {
      // #2104: one read per process start, two primary-key-ordered scans of small tables (one row per origin, one per
      // pending gap).
      cmd.CommandText = $"""
        SELECT origin_service_id, origin_service_name, request_topic, last_checkpoint_at
        FROM {schema}.wh_integrity_known_origins ORDER BY origin_service_id;
        SELECT origin_service_id, origin_service_name, tenant_scope, event_type, from_commit_sequence,
               to_commit_sequence, expected_count
        FROM {schema}.wh_integrity_pending_gaps ORDER BY 1, 3, 4, 5, 6;
        """;
      var origins = new List<IntegrityKnownOrigin>();
      var pending = new List<IntegrityGapTracker.PendingGap>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        origins.Add(new IntegrityKnownOrigin(
          reader.GetGuid(0), reader.GetString(1),
          await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
          await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false)));
      }
      await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var tenant = reader.GetString(2);
        pending.Add(new IntegrityGapTracker.PendingGap {
          OriginServiceId = reader.GetGuid(0),
          OriginServiceName = reader.GetString(1),
          TenantScope = tenant.Length == 0 ? null : tenant,
          EventType = reader.GetString(3),
          FromCommitSequence = reader.GetInt64(4),
          ToCommitSequence = reader.GetInt64(5),
          ExpectedCount = reader.GetInt32(6),
        });
      }
      return new IntegrityGapTrackerState(origins, pending);
    }, cancellationToken);

  /// <inheritdoc />
  /// <remarks>Null on any failure (logged): the drill-down then starts from the window's first stream, as it did before
  /// the cursor existed.</remarks>
  public async Task<Guid?> IntegrityTakeDrillCursorAsync(Guid originServiceId, long windowSince, CancellationToken cancellationToken = default) {
    try {
      return await _withCoordinatorCommandAsync<Guid?>(async (cmd, schema) => {
        // #2211: read and forgotten in one statement, so a cursor is used by one drill-down only.
        cmd.CommandText = $"""
          DELETE FROM {schema}.wh_integrity_drill_cursors
          WHERE origin_service_id = @p_origin AND window_since = @p_since
          RETURNING resume_after_stream_id
          """;
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SINCE, windowSince));
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid cursor ? cursor : null;
      }, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the drill-down starts from the first stream.
      _log.LogWarning(ex,
        "Integrity drill cursor for origin {OriginServiceId}, window from {WindowSince}, could not be read; the drill-down starts from the window's first stream.",
        originServiceId, windowSince);
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  /// <remarks>A failure is logged and keeps nothing: the next drill-down starts from the window's first stream.</remarks>
  public async Task IntegritySaveDrillCursorAsync(
      Guid originServiceId, long windowSince, Guid resumeAfterStreamId, DateTimeOffset now, CancellationToken cancellationToken = default) {
    try {
      _ = await _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
        // #2211: one cursor per window, the later replacing the earlier; the origin's cursors of windows below its seal
        // are never read again (the audit starts at the seal), so they go in the same statement.
        cmd.CommandText = $"""
          WITH dead AS (
            DELETE FROM {schema}.wh_integrity_drill_cursors c
            USING {schema}.wh_integrity_seals s
            WHERE c.origin_service_id = @p_origin AND s.origin_service_id = @p_origin AND c.window_since < s.sealed_through
              AND c.window_since <> @p_since
          )
          INSERT INTO {schema}.wh_integrity_drill_cursors (origin_service_id, window_since, resume_after_stream_id, saved_at)
          VALUES (@p_origin, @p_since, @p_resume, @p_now)
          ON CONFLICT (origin_service_id, window_since) DO UPDATE SET
            resume_after_stream_id = EXCLUDED.resume_after_stream_id,
            saved_at = EXCLUDED.saved_at
          """;
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SINCE, windowSince));
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_resume", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = resumeAfterStreamId });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_NOW, now));
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the next drill-down starts from the first stream.
      _log.LogWarning(ex,
        "Integrity drill cursor for origin {OriginServiceId}, window from {WindowSince}, could not be kept; the next drill-down starts from the window's first stream.",
        originServiceId, windowSince);
#pragma warning restore CA1848
    }
  }

  /// <inheritdoc />
  public Task IntegrityRecordOriginCheckpointAsync(
      Guid originServiceId, string originServiceName, string? requestTopic, DateTimeOffset checkpointAt,
      long? latestCommitSequence = null, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
      // #2104: one row per origin, rewritten in place per checkpoint; no indexed column changes, so the update is
      // heap-only. An origin that announces no address keeps the known one.
      // #2261: the highest watermark announced is kept (GREATEST ignores a NULL), so a late checkpoint never lowers it.
      cmd.CommandText = $"""
        INSERT INTO {schema}.wh_integrity_known_origins AS k
          (origin_service_id, origin_service_name, request_topic, last_checkpoint_at, last_commit_sequence)
        VALUES (@p_origin, @p_name, @p_topic, @p_at, @p_latest)
        ON CONFLICT (origin_service_id) DO UPDATE SET
          origin_service_name = EXCLUDED.origin_service_name,
          request_topic = COALESCE(EXCLUDED.request_topic, k.request_topic),
          last_checkpoint_at = GREATEST(k.last_checkpoint_at, EXCLUDED.last_checkpoint_at),
          last_commit_sequence = GREATEST(k.last_commit_sequence, EXCLUDED.last_commit_sequence)
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_name", originServiceName));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_topic", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)requestTopic ?? DBNull.Value });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_at", checkpointAt));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_latest", NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = (object?)latestCommitSequence ?? DBNull.Value
      });
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task IntegrityReplacePendingGapsAsync(
      Guid originServiceId, IReadOnlyList<IntegrityGapTracker.PendingGap> pending, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(pending);
    return _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
      // #2104: the origin's set replaced in one statement: rows no longer pending leave, new ones are added, rows still
      // pending are left alone.
      cmd.CommandText = $"""
        WITH kept AS (
          SELECT * FROM unnest(@p_names::text[], @p_tenants::text[], @p_types::text[], @p_from::bigint[], @p_to::bigint[],
                               @p_expected::int[])
            AS g(origin_service_name, tenant_scope, event_type, from_commit_sequence, to_commit_sequence, expected_count)
        ),
        gone AS (
          DELETE FROM {schema}.wh_integrity_pending_gaps p
          WHERE p.origin_service_id = @p_origin
            AND NOT EXISTS (SELECT 1 FROM kept g
                            WHERE g.tenant_scope = p.tenant_scope AND g.event_type = p.event_type
                              AND g.from_commit_sequence = p.from_commit_sequence
                              AND g.to_commit_sequence = p.to_commit_sequence)
        )
        INSERT INTO {schema}.wh_integrity_pending_gaps AS kp
          (origin_service_id, origin_service_name, tenant_scope, event_type, from_commit_sequence, to_commit_sequence,
           expected_count)
        SELECT @p_origin, g.origin_service_name, g.tenant_scope, g.event_type, g.from_commit_sequence,
               g.to_commit_sequence, g.expected_count
        FROM kept g
        ORDER BY 3, 4, 5, 6
        ON CONFLICT (origin_service_id, tenant_scope, event_type, from_commit_sequence, to_commit_sequence)
        DO UPDATE SET expected_count = EXCLUDED.expected_count
        WHERE kp.expected_count IS DISTINCT FROM EXCLUDED.expected_count
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_names", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = pending.Select(g => g.OriginServiceName).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TENANTS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = pending.Select(g => g.TenantScope ?? string.Empty).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = pending.Select(g => g.EventType).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_FROM, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = pending.Select(g => g.FromCommitSequence).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TO, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = pending.Select(g => g.ToCommitSequence).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_expected", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) {
        Value = pending.Select(g => g.ExpectedCount).ToArray()
      });
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task IntegrityRecordMissingEventsAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, IReadOnlyList<Guid?> missingEventIds,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(keys);
    ArgumentNullException.ThrowIfNull(missingEventIds);
    if (keys.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
      // One statement for the comparison's deficits, each row found by its primary key; a row whose missing event is
      // already the recorded one is not rewritten.
      cmd.CommandText = $"""
        UPDATE {schema}.wh_integrity_ledger l
        SET missing_event_id = m.missing_event_id
        FROM unnest(@p_tenants::text[], @p_types::text[], @p_streams::uuid[], @p_missing::uuid[])
          AS m(tenant_scope, event_type, stream_id, missing_event_id)
        WHERE l.origin_service_id = @p_origin AND l.tenant_scope = m.tenant_scope
          AND l.event_type = m.event_type AND l.stream_id = m.stream_id
          AND l.missing_event_id IS DISTINCT FROM m.missing_event_id
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TENANTS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = keys.Select(k => k.TenantScope ?? string.Empty).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = keys.Select(k => k.EventType).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAMS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = keys.Select(k => k.StreamId).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_missing", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = missingEventIds.Select(id => (object?)id ?? DBNull.Value).ToArray()
      });
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task IntegrityRecordTraceAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      Whizbang.Core.Observability.IntegrityTraceLink trace, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(keys);
    if (keys.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
      // One statement for the comparison's deficits, each row found by its primary key (491).
      cmd.CommandText = $"""
        UPDATE {schema}.wh_integrity_ledger l
        SET trace_parent = @p_trace_parent, trace_request_id = @p_trace_request_id
        FROM unnest(@p_tenants::text[], @p_types::text[], @p_streams::uuid[]) AS m(tenant_scope, event_type, stream_id)
        WHERE l.origin_service_id = @p_origin AND l.tenant_scope = m.tenant_scope
          AND l.event_type = m.event_type AND l.stream_id = m.stream_id
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TENANTS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = keys.Select(k => k.TenantScope ?? string.Empty).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = keys.Select(k => k.EventType).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAMS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = keys.Select(k => k.StreamId).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_trace_parent", NpgsqlTypes.NpgsqlDbType.Text) {
        Value = (object?)trace.TraceParent ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_trace_request_id", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)trace.RequestId ?? DBNull.Value
      });
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  /// <inheritdoc />
  public async Task<int> IntegrityCountShortAfterAnswerAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(keys);
    if (keys.Count == 0) {
      return 0;
    }
    return await _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
      // 493: each row by its primary key; a row the answer of answered_at filled (not unhealable) and not yet counted
      // since that answer is counted and stamped, so the comparisons that follow do not count it again.
      cmd.CommandText = $"""
        UPDATE {schema}.wh_integrity_ledger l
        SET counted_short_at = @p_now
        FROM unnest(@p_tenants::text[], @p_types::text[], @p_streams::uuid[]) AS m(tenant_scope, event_type, stream_id)
        WHERE l.origin_service_id = @p_origin AND l.tenant_scope = m.tenant_scope
          AND l.event_type = m.event_type AND l.stream_id = m.stream_id
          AND l.answered_at IS NOT NULL AND l.unhealable_at IS NULL
          AND (l.counted_short_at IS NULL OR l.counted_short_at < l.answered_at)
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TENANTS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = keys.Select(k => k.TenantScope ?? string.Empty).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = keys.Select(k => k.EventType).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAMS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = keys.Select(k => k.StreamId).ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_NOW, now));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task RecordCutAwaitingSealAsync(Guid streamId, string? task, DateTimeOffset now, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
      // 494: one row per stream the seal holds, by primary key; any other answer removes it.
      cmd.CommandText = task is null
        ? $"DELETE FROM {schema}.wh_integrity_cuts_awaiting_seal WHERE stream_id = @p_stream"
        : $"""
          INSERT INTO {schema}.wh_integrity_cuts_awaiting_seal (stream_id, task, since) VALUES (@p_stream, @p_task, @p_now)
          ON CONFLICT (stream_id) DO UPDATE SET task = EXCLUDED.task
          """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_stream", streamId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_task", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)task ?? DBNull.Value });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_NOW, now));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<Whizbang.Core.Observability.CutsAwaitingSeal> GetCutsAwaitingSealAsync(int cap, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // 494: per task, at most cap + 1 entries of idx_integrity_cuts_awaiting_seal_task.
      cmd.CommandText = $"""
        SELECT t.task, (SELECT COUNT(*) FROM (
                          SELECT 1 FROM {schema}.wh_integrity_cuts_awaiting_seal c WHERE c.task = t.task
                          LIMIT GREATEST(@p_cap, 1)::BIGINT + 1) x) AS held
        FROM (VALUES ('truncate'), ('archive')) AS t(task)
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_cap", cap));
      var tasks = new Dictionary<string, Whizbang.Core.Observability.PendingRows>(StringComparer.Ordinal);
      var limit = Math.Max(cap, 1);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var held = reader.GetInt64(1);
        if (held > 0) {
          tasks[reader.GetString(0)] = new Whizbang.Core.Observability.PendingRows(Math.Min(held, limit), held > limit);
        }
      }
      return new Whizbang.Core.Observability.CutsAwaitingSeal { Reported = true, Tasks = tasks };
    }, cancellationToken);

  /// <inheritdoc />
  public Task<long?> IntegrityNoteRulesAsync(string rulesFingerprint, long currentGeneration, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<long?>(async (cmd, schema) => {
      // #2365: the fingerprint is replaced only when it changed; a change after an earlier note records the generation
      // it was first answered at. The answer is the generation the rules last changed at, or null when never.
      cmd.CommandText = $"""
        WITH prev AS (
          SELECT p.setting_value FROM {schema}.wh_settings p WHERE p.setting_key = 'integrity_rules_fingerprint'
        ),
        changed AS (
          INSERT INTO {schema}.wh_settings AS s (setting_key, setting_value, value_type, description)
          VALUES ('integrity_rules_fingerprint', @p_fingerprint, 'string',
                  'The rule fingerprint this origin last answered with (#2365).')
          ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            WHERE s.setting_value IS DISTINCT FROM EXCLUDED.setting_value
          RETURNING 1
        ),
        noted AS (
          INSERT INTO {schema}.wh_settings AS s (setting_key, setting_value, value_type, description)
          SELECT 'integrity_rules_changed_generation', @p_generation::text, 'string',
                 'The origin generation this service''s rule fingerprint last changed at (#2365).'
          WHERE EXISTS (SELECT 1 FROM changed) AND EXISTS (SELECT 1 FROM prev)
          ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
          RETURNING s.setting_value
        )
        SELECT COALESCE(
          (SELECT n.setting_value FROM noted n),
          (SELECT g.setting_value FROM {schema}.wh_settings g WHERE g.setting_key = 'integrity_rules_changed_generation'))::BIGINT
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_fingerprint", rulesFingerprint));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_generation", currentGeneration));
      var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return value is long changedAt ? changedAt : (long?)null;
    }, cancellationToken);

  /// <inheritdoc />
  public async Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
      IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      int limit, CancellationToken cancellationToken = default) {
    if (originIds.Count == 0 || limit <= 0) {
      return [];
    }
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = _resolveSchema(typeof(OutboxRecord));
      var qualified = BuildSchemaQualifiedName(schema, "wh_integrity_claim_repair_drain");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = originIds.ToArray()
      });
      cmd.Parameters.AddWithValue(P_NOW, now);
      cmd.Parameters.AddWithValue("p_base_backoff_secs", (int)baseBackoff.TotalSeconds);
      cmd.Parameters.AddWithValue(P_MAX_ATTEMPTS, maxAttempts);
      cmd.Parameters.AddWithValue(P_LIMIT, limit);
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      // #2103: each claimed row's recorded missing event rides along, read by primary key from the row the claim stamped;
      // #2365: and the trace of the comparison that recorded it.
      cmd.CommandText = $"""
        SELECT c.origin_service_id, c.tenant_scope, c.event_type, c.stream_id, c.window_from, c.window_until, l.missing_event_id,
               l.trace_parent, l.trace_request_id
        FROM {qualified}(@p_origin_ids,@p_now,@p_base_backoff_secs,@p_max_attempts,@p_limit) c
        LEFT JOIN {schema}.wh_integrity_ledger l
          ON l.origin_service_id = c.origin_service_id AND l.tenant_scope = c.tenant_scope
         AND l.event_type = c.event_type AND l.stream_id = c.stream_id
        """;
#pragma warning restore S2077
      var items = new List<IntegrityRepairDrainItem>(limit);
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        items.Add(await _readRepairDrainItemAsync(reader, cancellationToken).ConfigureAwait(false));
      }
      return items;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the drain simply waits for the next pass.
      _log.LogWarning(ex, "Integrity repair-drain claim failed; nothing dispatched this pass.");
#pragma warning restore CA1848
      return [];
    }
  }

  // One claimed repair-drain row: the key, the window, the recorded missing event and (#2365) the trace of the
  // comparison that recorded it.
  private static async Task<IntegrityRepairDrainItem> _readRepairDrainItemAsync(
      System.Data.Common.DbDataReader reader, CancellationToken cancellationToken) =>
    new(
      reader.GetGuid(0),
      reader.GetString(1),
      reader.GetString(2),
      reader.GetGuid(3),
      await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(4),
      await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(5),
      await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(6),
      await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7),
      await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(8));

  /// <inheritdoc />
  /// <remarks>The claim (<c>wh_integrity_claim_ledger_recheck</c>, migration 375) returns a row with a stamped window
  /// with both bounds, and a row with no window (compared over the whole history) with neither (#2208). A failure
  /// propagates: the re-check worker logs it and retries next tick.</remarks>
  public async Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimLedgerRecheckAsync(
      IReadOnlyList<Guid> originIds, DateTimeOffset reportedBefore, DateTimeOffset now, int limit,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(originIds);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = originIds.ToArray()
    });
    cmd.Parameters.AddWithValue("p_reported_before", reportedBefore);
    cmd.Parameters.AddWithValue(P_NOW, now);
    cmd.Parameters.AddWithValue(P_LIMIT, limit);
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
    cmd.CommandText =
      "SELECT origin_service_id, tenant_scope, event_type, stream_id, window_from, window_until " +
      $"FROM {BuildSchemaQualifiedName(schema, "wh_integrity_claim_ledger_recheck")}(@p_origin_ids, @p_reported_before, @p_now, @p_limit)";
#pragma warning restore S2077
    var items = new List<IntegrityRepairDrainItem>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      items.Add(new IntegrityRepairDrainItem(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
        await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(4),
        await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(5)));
    }
    return items;
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
      var schema = _resolveSchema(typeof(OutboxRecord));
      var qualified = BuildSchemaQualifiedName(schema, "wh_integrity_heal_batch_counted");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      var conn = __scope.Connection;
      await using var cmd = conn.CreateCoordinatorCommand();
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT age_seconds, was_open FROM {qualified}(@p_origin_service_id,@p_tenant_scopes,@p_event_types,@p_stream_ids)";
#pragma warning restore S2077
      var ages = new List<double>(keys.Count);
      var closed = 0;
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)) {
          ages.Add(reader.GetDouble(0));
        }
        closed += reader.GetBoolean(1) ? 1 : 0;
      }
      // #2256: a row already marked unhealable was closed by the mark, and is not closed again (399).
      _recordClosed(closed, Whizbang.Core.Observability.StreamIntegrityMetrics.CLOSED_HEALED);
      return ages;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; falls back to the single-key functions.
      _log.LogWarning(ex,
        "Integrity ledger batch wh_integrity_heal_batch_counted failed; falling back to single-key calls for this chunk.");
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  /// <remarks>Null on any failure (logged): the ledger then forgets the type lanes alone, through the batch heal.</remarks>
  public async Task<IReadOnlyList<double>?> IntegrityMarkTypesHealedWithAgesAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> typeKeys,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(typeKeys);
    try {
      return await _withCoordinatorCommandAsync<IReadOnlyList<double>?>(async (cmd, schema) => {
        cmd.CommandText = $"SELECT age_seconds, was_open FROM {BuildSchemaQualifiedName(schema, "wh_integrity_heal_types_counted")}(@p_origin_service_id,@p_tenant_scopes,@p_event_types)";
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN_SERVICE_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_tenant_scopes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
          Value = typeKeys.Select(k => k.TenantScope).ToArray()
        });
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
          Value = typeKeys.Select(k => k.EventType).ToArray()
        });
        var ages = new List<double>();
        var closed = 0;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          ages.Add(reader.GetDouble(0));
          closed += reader.GetBoolean(1) ? 1 : 0;
        }
        // #2256: a row already marked unhealable was closed by the mark, and is not closed again (399).
        _recordClosed(closed, Whizbang.Core.Observability.StreamIntegrityMetrics.CLOSED_HEALED);
        return ages;
      }, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the ledger falls back to the type lanes.
      _log.LogWarning(ex,
        "Integrity ledger wh_integrity_heal_types_counted failed for origin {OriginServiceId}; only the proven types' type lanes are forgotten this time.",
        originServiceId);
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  /// <remarks>Null on any failure: a mark that cannot reach the ledger leaves repair on its terminal cadence, the
  /// harmless direction. The returned keys speak the caller's form, an absent tenant as null.</remarks>
  public Task<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>?> IntegrityMarkUnhealableBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now,
      CancellationToken cancellationToken = default) =>
    _markUnhealableBatchAsync(originServiceId, keys, null, [], now, cancellationToken);

  /// <inheritdoc />
  /// <remarks>The store marks only the rows whose deficit the answer covered (357, #2143).</remarks>
  public Task<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>?> IntegrityMarkUnhealableBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, RedeliveryAnswerWindow answeredWindow,
      IReadOnlyList<Guid> answeredEventIds, DateTimeOffset now, CancellationToken cancellationToken = default) =>
    _markUnhealableBatchAsync(originServiceId, keys, answeredWindow, answeredEventIds, now, cancellationToken);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The mark's keys, the answer's window and events, the clock and cancellation.")]
  private async Task<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>?> _markUnhealableBatchAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, RedeliveryAnswerWindow? answeredWindow,
      IReadOnlyList<Guid> answeredEventIds, DateTimeOffset now, CancellationToken cancellationToken) {
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = _resolveSchema(typeof(OutboxRecord));
      // 357 (#2143): an answer whose window is known marks through the coverage-checking function.
      var qualified = BuildSchemaQualifiedName(schema,
        answeredWindow is null ? "wh_integrity_mark_unhealable_batch" : "wh_integrity_mark_unhealable_covered_batch");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
      cmd.Parameters.AddWithValue(P_NOW, now);
      var arguments = "@p_origin_service_id,@p_tenant_scopes,@p_event_types,@p_stream_ids,@p_now";
      if (answeredWindow is not null) {
        _bindAnsweredWindow(cmd, answeredWindow);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
          Value = answeredEventIds.ToArray()
        });
        arguments += ",@p_answer_from,@p_answer_to,@p_event_ids";
      }
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT tenant_scope, event_type, stream_id FROM {qualified}({arguments})";
#pragma warning restore S2077
      var marked = new List<IntegrityRepairLedger.DivergenceKey>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        var tenant = reader.GetString(0);
        marked.Add(new IntegrityRepairLedger.DivergenceKey(
          originServiceId, tenant.Length == 0 ? null : tenant, reader.GetString(1), reader.GetGuid(2)));
      }
      return marked;
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; repair keeps its terminal cadence for these buckets.
      _log.LogWarning(ex,
        "Integrity ledger wh_integrity_mark_unhealable_batch failed for origin {OriginServiceId}; repair keeps asking on its terminal cadence.",
        originServiceId);
#pragma warning restore CA1848
      return null;
    }
  }

  /// <summary>
  /// Binds the window a redelivery answered (#2143): its exclusive floor and inclusive ceiling (NULL = unbounded, and
  /// both NULL when the window is not known).
  /// </summary>
  private static void _bindAnsweredWindow(Npgsql.NpgsqlCommand cmd, RedeliveryAnswerWindow? window) {
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_answer_from", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = window is { FromCommitSequence: { } from } ? (object)from : DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_answer_to", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = window is { ToCommitSequence: { } to } ? (object)to : DBNull.Value
    });
  }

  /// <inheritdoc />
  /// <remarks>Best-effort: a prune that cannot reach the ledger is logged and answers 0; the next audit cycle prunes.</remarks>
  public async Task<int> IntegrityForgetUnconsumedEventTypesAsync(
      IReadOnlyList<string> consumedEventTypes, IReadOnlyCollection<string> absorbedNamespaces,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(consumedEventTypes);
    ArgumentNullException.ThrowIfNull(absorbedNamespaces);
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = _resolveSchema(typeof(OutboxRecord));
      var qualified = BuildSchemaQualifiedName(schema, "wh_integrity_forget_unconsumed_counted");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      cmd.Parameters.AddWithValue("p_consumed", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, consumedEventTypes.ToArray());
      cmd.Parameters.AddWithValue("p_absorbed", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, absorbedNamespaces.ToArray());
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT forgotten, closed FROM {qualified}(@p_consumed,@p_absorbed)";
#pragma warning restore S2077
      return await _readForgottenAsync(cmd, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the next audit cycle prunes.
      _log.LogWarning(ex,
        "Integrity ledger wh_integrity_forget_unconsumed_counted failed; rows of unconsumed event types stay until the next audit cycle.");
#pragma warning restore CA1848
      return 0;
    }
  }

  // The forget functions' one row (migration 399): the rows removed, answered to the caller, and those of them still
  // open, counted as forgotten (#2256). A row already marked unhealable was closed by the mark.
  private async Task<int> _readForgottenAsync(Npgsql.NpgsqlCommand cmd, CancellationToken cancellationToken) {
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);   // one row, always
    _recordClosed(reader.GetInt32(1), Whizbang.Core.Observability.StreamIntegrityMetrics.CLOSED_FORGOTTEN);
    return reader.GetInt32(0);
  }

  /// <inheritdoc />
  /// <remarks>Two statements on one connection: the ledger's distinct (origin, type) pairs with their origin's last report
  /// (<c>wh_integrity_ledger_origin_types</c>, migrations 370 and 372), then the judged ones forgotten
  /// (<c>wh_integrity_forget_origin_types_counted</c>, migration 399); the second is skipped when nothing is judged. Best-effort: a prune that
  /// cannot reach the ledger is logged and answers 0, and the next audit cycle prunes.</remarks>
  public async Task<int> IntegrityForgetNonOwnerLedgerRowsAsync(
      Func<Guid, string, DateTimeOffset, bool> isNotAuthoritative, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(isNotAuthoritative);
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = _resolveSchema(typeof(OutboxRecord));
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      var origins = new List<Guid>();
      var types = new List<string>();
      await using (var read = __scope.Connection.CreateCoordinatorCommand()) {
#pragma warning disable S2077 // Function name is a compile-time constant; there are no arguments.
        read.CommandText =
          $"SELECT origin_service_id, event_type, origin_last_reported_at FROM {BuildSchemaQualifiedName(schema, "wh_integrity_ledger_origin_types")}()";
#pragma warning restore S2077
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          var (origin, type) = (reader.GetGuid(0), reader.GetString(1));
          var lastReported = await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false);
          if (isNotAuthoritative(origin, type, lastReported)) {
            origins.Add(origin);
            types.Add(type);
          }
        }
      }
      if (origins.Count == 0) {
        return 0;
      }
      await using var forget = __scope.Connection.CreateCoordinatorCommand();
      forget.Parameters.AddWithValue("p_origins", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, origins.ToArray());
      forget.Parameters.AddWithValue("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, types.ToArray());
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      forget.CommandText = $"SELECT forgotten, closed FROM {BuildSchemaQualifiedName(schema, "wh_integrity_forget_origin_types_counted")}(@p_origins,@p_types)";
#pragma warning restore S2077
      return await _readForgottenAsync(forget, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the next audit cycle prunes.
      _log.LogWarning(ex,
        "Integrity ledger prune of rows recorded under an origin that does not own their type failed; they stay until the next audit cycle.");
#pragma warning restore CA1848
      return 0;
    }
  }

  /// <inheritdoc />
  /// <remarks>One bounded read (<c>wh_integrity_count_answer_pending</c>, migration 371), answered from
  /// <c>idx_integrity_ledger_answer_pending</c> once the maintenance pass has built it. Not best-effort: a count that
  /// cannot be read fails the drain's tick, which claims nothing and retries, so the cap never opens on a failure.</remarks>
  public Task<int> IntegrityCountAnswerPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      cmd.CommandText = $"SELECT {schema}.wh_integrity_count_answer_pending(@p_now, @p_limit)";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_NOW, now));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_limit", limit));
      return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
        System.Globalization.CultureInfo.InvariantCulture);
    }, cancellationToken);

  /// <inheritdoc />
  /// <remarks>Best-effort: a mark that cannot reach the ledger is logged and answers 0, and the requested buckets wait
  /// out their backoff only, as before.</remarks>
  public async Task<int> IntegrityMarkAnswerPendingAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, long? windowFrom, long? windowUntil,
      DateTimeOffset now, TimeSpan timeout, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(keys);
    if (keys.Count == 0) {
      return 0;
    }
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var qualified = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_mark_answer_pending");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_window_from", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)windowFrom ?? DBNull.Value });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_window_until", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)windowUntil ?? DBNull.Value });
      cmd.Parameters.AddWithValue(P_NOW, now);
      cmd.Parameters.AddWithValue("p_timeout_secs", _wholeSeconds(timeout));
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT {qualified}(@p_origin_service_id,@p_tenant_scopes,@p_event_types,@p_stream_ids,@p_window_from,@p_window_until,@p_now,@p_timeout_secs)";
#pragma warning restore S2077
      return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the buckets wait out their backoff instead of their answer.
      _log.LogWarning(ex,
        "Integrity ledger wh_integrity_mark_answer_pending failed for origin {OriginServiceId}; the requested buckets wait out their backoff only.",
        originServiceId);
#pragma warning restore CA1848
      return 0;
    }
  }

  /// <inheritdoc />
  /// <remarks>One call to <c>wh_integrity_withdraw_answer_pending</c> (migration 395). Best-effort: a withdrawal that
  /// cannot reach the ledger is logged and answers 0, and the buckets wait out their answer timeout with the attempt
  /// spent.</remarks>
  public async Task<int> IntegrityWithdrawAnswerPendingAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now, TimeSpan timeout,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(keys);
    if (keys.Count == 0) {
      return 0;
    }
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var qualified = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_withdraw_answer_pending");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      _bindDivergenceKeyArrays(cmd, originServiceId, keys);
      cmd.Parameters.AddWithValue(P_NOW, now);
      cmd.Parameters.AddWithValue("p_timeout_secs", _wholeSeconds(timeout));
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT {qualified}(@p_origin_service_id,@p_tenant_scopes,@p_event_types,@p_stream_ids,@p_now,@p_timeout_secs)";
#pragma warning restore S2077
      return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the buckets wait out their answer timeout instead.
      _log.LogWarning(ex,
        "Integrity ledger wh_integrity_withdraw_answer_pending failed for origin {OriginServiceId}; the buckets whose send failed wait out their answer timeout, with the attempt spent.",
        originServiceId);
#pragma warning restore CA1848
      return 0;
    }
  }

  /// <inheritdoc />
  /// <remarks>Null on any failure (logged): the reconcile then judges the bundle as it did before, and its buckets wait
  /// out their answer timeout. The returned keys speak the caller's form, an absent tenant as null.</remarks>
  public Task<IntegrityBundleAnswer?> IntegrityRecordBundleAnswerAsync(
      Guid originServiceId, IReadOnlyList<Guid> eventIds, IReadOnlyList<long?> commitSequences, DateTimeOffset now,
      CancellationToken cancellationToken = default) =>
    IntegrityRecordBundleAnswerAsync(originServiceId, eventIds, commitSequences, null, now, cancellationToken);

  /// <inheritdoc />
  /// <remarks>An unchanged bucket the answered window did not cover is answered and not marked (357, #2143), and so is one
  /// of a stream the bundle carried only in part (410, #2149). A type lane is marked only by the answer's last bundle (431,
  /// #2311), and, for a bundle that names its answer, only once every bundle of the answer has committed, and only for an
  /// answer to a whole-type request (530, #2340, #2341).</remarks>
  public async Task<IntegrityBundleAnswer?> IntegrityRecordBundleAnswerAsync(
      Guid originServiceId, IReadOnlyList<Guid> eventIds, IReadOnlyList<long?> commitSequences,
      RedeliveryAnswerWindow? answeredWindow, DateTimeOffset now, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    ArgumentNullException.ThrowIfNull(commitSequences);
    if (eventIds.Count != commitSequences.Count) {
      throw new ArgumentException(
        $"{eventIds.Count} event ids but {commitSequences.Count} commit sequences; they pair by position.", nameof(commitSequences));
    }
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var qualified = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_record_bundle_answer");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      cmd.Parameters.AddWithValue(P_ORIGIN_SERVICE_ID, originServiceId);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = eventIds.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_commit_sequences", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = commitSequences.ToArray()
      });
      cmd.Parameters.AddWithValue(P_NOW, now);
      // An unknown window marks as before 357.
      cmd.Parameters.AddWithValue("p_answer_window_known", answeredWindow is not null);
      _bindAnsweredWindow(cmd, answeredWindow);
      // #2149 (410): the streams the bundle carried only in part are answered but never marked.
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_partial_streams", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = answeredWindow?.PartialStreamIds is { } partial ? partial.ToArray() : DBNull.Value
      });
      // #2311 (431): a bundle more of whose answer follows leaves the type lane waiting when it changed nothing.
      cmd.Parameters.AddWithValue("p_answer_continues", answeredWindow is { Continues: true });
      // #2340, #2341 (530): the bundle's answer and place in it, judged with the rest of the answer once all of it is in;
      // an answer to named streams marks no type lane.
      _bindAnswerPart(cmd, answeredWindow?.Part);
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
      cmd.CommandText = $"SELECT stream_id, tenant_scope, event_type, present_count, absent_count, refused_truncated_count, adoptable_count, marked FROM {qualified}(@p_origin_service_id,@p_event_ids,@p_commit_sequences,@p_now,@p_answer_window_known,@p_answer_from,@p_answer_to,@p_partial_streams,@p_answer_continues,@p_answer_id,@p_bundle_index,@p_bundle_count,@p_answers_whole_type)";
#pragma warning restore S2077
      int present = 0, absent = 0, refused = 0, adoptable = 0;
      var markedRefused = new List<IntegrityRepairLedger.DivergenceKey>();
      var markedUnchanged = new List<IntegrityRepairLedger.DivergenceKey>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        present += reader.GetInt32(3);
        absent += reader.GetInt32(4);
        refused += reader.GetInt32(5);
        adoptable += reader.GetInt32(6);
        if (await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)) {
          continue;
        }
        var tenant = reader.GetString(1);
        var key = new IntegrityRepairLedger.DivergenceKey(
          originServiceId, tenant.Length == 0 ? null : tenant, reader.GetString(2), reader.GetGuid(0));
        (reader.GetString(7) == "refused_truncated" ? markedRefused : markedUnchanged).Add(key);
      }
      return new IntegrityBundleAnswer(
        eventIds.Distinct().Count(), present, absent, refused, adoptable, markedRefused, markedUnchanged);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; the reconcile judges the bundle as before.
      _log.LogWarning(ex,
        "Integrity ledger wh_integrity_record_bundle_answer failed for origin {OriginServiceId}; the reconcile judges the bundle without the commit's view.",
        originServiceId);
#pragma warning restore CA1848
      return null;
    }
  }

  /// <summary>Binds the bundle's place in its answer (#2340, 530); all null, and not whole-type, when it names none.</summary>
  private static void _bindAnswerPart(Npgsql.NpgsqlCommand cmd, RedeliveryAnswerPart? part) {
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_answer_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)part?.AnswerId ?? DBNull.Value });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_bundle_index", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)part?.BundleIndex ?? DBNull.Value });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_bundle_count", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)part?.BundleCount ?? DBNull.Value });
    cmd.Parameters.AddWithValue("p_answers_whole_type", part is { WholeType: true });
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<RedeliveredBucketPresence>?> AdoptRedeliveredOriginAsync(
      Guid originServiceId, string? originServiceName, IReadOnlyList<Guid> eventIds, IReadOnlyList<long?> commitSequences,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    ArgumentNullException.ThrowIfNull(commitSequences);
    if (eventIds.Count != commitSequences.Count) {
      throw new ArgumentException(
        $"{eventIds.Count} event ids but {commitSequences.Count} commit sequences; they pair by position.", nameof(commitSequences));
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    // 253 (#1843, #1844): per bucket, the events present, those that adopted the origin, the children the store
    // refused to restore past deduplication, and the present events the first-hop check refused. 454 (#2350): and the
    // children settled as satisfied by a newer held copy of their key, from the counting form (a new name, so the
    // result of 253's function never changes).
    var qualified = BuildSchemaQualifiedName(schema, "wh_reconcile_redelivered_bundle_counting_satisfied");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.Parameters.AddWithValue(P_ORIGIN_SERVICE_ID, originServiceId);
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin_service_name", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)originServiceName ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = eventIds.ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_commit_sequences", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = commitSequences.ToArray()
    });
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
    cmd.CommandText = $"SELECT stream_id, tenant_scope, event_type, present_count, adopted_count, refused_truncated_count, refused_foreign_origin_count, superseded_satisfied_count FROM {qualified}(@p_origin_service_id,@p_origin_service_name,@p_event_ids,@p_commit_sequences)";
#pragma warning restore S2077
    var buckets = new List<RedeliveredBucketPresence>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var tenant = reader.GetString(1);
      buckets.Add(new RedeliveredBucketPresence(
        reader.GetGuid(0), tenant.Length == 0 ? null : tenant, reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
        reader.GetInt32(5), reader.GetInt32(6), SupersededSatisfied: reader.GetInt32(7)));
    }
    return buckets;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<IReadOnlyList<RedeliveredBucketPresence>>?> ReconcileRedeliveredBundlesAsync(
      IReadOnlyList<RedeliveredBundle> bundles, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(bundles);
    foreach (var bundle in bundles) {
      if (bundle.EventIds.Count != bundle.CommitSequences.Count) {
        throw new ArgumentException(
          $"A bundle of origin {bundle.OriginServiceId} has {bundle.EventIds.Count} event ids but "
          + $"{bundle.CommitSequences.Count} commit sequences; they pair by position.", nameof(bundles));
      }
    }
    var results = new List<RedeliveredBucketPresence>[bundles.Count];
    for (var i = 0; i < results.Length; i++) {
      results[i] = [];
    }
    if (bundles.Count == 0) {
      return results;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    // 335 (#2099): every bundle at once, each sealed epoch the batch touches refolded once. 454 (#2350): the counting
    // form, which also reports the children settled as satisfied.
    var qualified = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_reconcile_redelivered_bundles_counting_satisfied");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_bundle_origins", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = bundles.Select(b => b.OriginServiceId).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_bundle_names", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = bundles.Select(b => (object?)b.OriginServiceName ?? DBNull.Value).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_event_bundles", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = bundles.SelectMany((b, i) => b.EventIds.Select(_ => i + 1)).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = bundles.SelectMany(b => b.EventIds).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_commit_sequences", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = bundles.SelectMany(b => b.CommitSequences).ToArray()
    });
#pragma warning disable S2077 // Function name is a compile-time constant; every argument is bound.
    cmd.CommandText = $"SELECT bundle, stream_id, tenant_scope, event_type, present_count, adopted_count, origins_filled_count, refused_truncated_count, refused_foreign_origin_count, superseded_satisfied_count FROM {qualified}(@p_bundle_origins,@p_bundle_names,@p_event_bundles,@p_event_ids,@p_commit_sequences)";
#pragma warning restore S2077
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var tenant = reader.GetString(2);
      results[reader.GetInt32(0) - 1].Add(new RedeliveredBucketPresence(
        reader.GetGuid(1), tenant.Length == 0 ? null : tenant, reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
        reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(6), reader.GetInt32(9)));
    }
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<IntegrityDomainClaim>> RecordIntegrityDomainClaimsAsync(
      Guid ownerServiceId, string ownerServiceName, string? requestTopic, IReadOnlyList<string> domains,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ownerServiceName);
    ArgumentNullException.ThrowIfNull(domains);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var fn = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_record_domain_claims");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant; every argument is bound.
    cmd.CommandText = $"SELECT domain, owner_service_id, owner_service_name, request_topic FROM {fn}(@p_owner, @p_owner_name, @p_request_topic, @p_domains)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("p_owner", ownerServiceId);
    cmd.Parameters.AddWithValue("p_owner_name", ownerServiceName);
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_request_topic", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)requestTopic ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_domains", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = domains.ToArray()
    });
    return await _readDomainClaimsAsync(cmd, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<IntegrityDomainClaim>> GetIntegrityDomainClaimsAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified table name built from a validated schema constant; no argument.
    cmd.CommandText = $"SELECT domain, owner_service_id, owner_service_name, request_topic FROM {schema}.wh_integrity_domain_claims ORDER BY domain, owner_service_id";
#pragma warning restore S2077
    return await _readDomainClaimsAsync(cmd, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<OriginReattributionWindow?> GetOriginReattributionWindowAsync(
      string ownershipFingerprint, int maxEvents, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ownershipFingerprint);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var fn = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_reattribution_window");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant; every argument is bound.
    cmd.CommandText = $"SELECT after_commit_sequence, through_commit_sequence, event_types FROM {fn}(@p_fingerprint, @p_max_events)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("p_fingerprint", ownershipFingerprint);
    cmd.Parameters.AddWithValue("p_max_events", maxEvents);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    // The function always returns exactly one row (RETURN QUERY of three locals).
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new OriginReattributionWindow {
      AfterCommitSequence = reader.GetInt64(0),
      ThroughCommitSequence = reader.GetInt64(1),
      EventTypes = reader.GetFieldValue<string[]>(2),
    };
  }

  /// <inheritdoc />
  public async Task<OriginReattributionResult?> ReattributeOriginsAsync(
      OriginReattributionBatch batch, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(batch);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var fn = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_reattribute_origins");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant; every argument is bound.
    cmd.CommandText = $"SELECT reattributed_count, hop_disagreement_count, unattributed_count, advanced FROM {fn}(@p_after, @p_through, @p_event_types, @p_owner_ids, @p_owner_names)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("p_after", batch.AfterCommitSequence);
    cmd.Parameters.AddWithValue("p_through", batch.ThroughCommitSequence);
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = batch.Targets.Select(t => t.EventType).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_owner_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = batch.Targets.Select(t => t.OwnerServiceId).ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_owner_names", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = batch.Targets.Select(t => t.OwnerServiceName).ToArray()
    });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    // The function always returns exactly one row.
    _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new OriginReattributionResult {
      Reattributed = reader.GetInt32(0),
      HopDisagreements = reader.GetInt32(1),
      Unattributed = reader.GetInt32(2),
      Advanced = reader.GetBoolean(3),
    };
  }

  private static async Task<IReadOnlyList<IntegrityDomainClaim>> _readDomainClaimsAsync(
      System.Data.Common.DbCommand cmd, CancellationToken cancellationToken) {
    var claims = new List<IntegrityDomainClaim>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      claims.Add(new IntegrityDomainClaim {
        Domain = reader.GetString(0),
        OwnerServiceId = reader.GetGuid(1),
        OwnerServiceName = reader.GetString(2),
        RequestTopic = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
      });
    }
    return claims;
  }

  /// <inheritdoc />
  public async Task<IntegrityDeliveryEnqueueResult> EnqueueIntegrityDeliveryAsync(
      Guid deliveryId, string deliveryType, string payload, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(deliveryType);
    ArgumentNullException.ThrowIfNull(payload);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var fn = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_delivery_enqueue");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant; every argument is bound.
    cmd.CommandText = $"SELECT {fn}(@p_delivery_id, @p_delivery_type, @p_payload)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("p_delivery_id", deliveryId);
    cmd.Parameters.AddWithValue("p_delivery_type", deliveryType);
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_payload", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = payload });
    var recorded = (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    return recorded ? IntegrityDeliveryEnqueueResult.Recorded : IntegrityDeliveryEnqueueResult.AlreadyRecorded;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<IntegrityDelivery>> LeaseIntegrityDeliveriesAsync(
      int maxDeliveries, TimeSpan lease, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var fn = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_delivery_lease");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant; every argument is bound.
    cmd.CommandText = $"SELECT delivery_id, delivery_type, payload::text, attempts, queued_at FROM {fn}(@p_max, @p_lease_seconds)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("p_max", maxDeliveries);
    cmd.Parameters.AddWithValue("p_lease_seconds", _wholeSeconds(lease));
    var leased = new List<IntegrityDelivery>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      leased.Add(new IntegrityDelivery(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
        reader.GetFieldValue<DateTimeOffset>(4)));
    }
    return leased;
  }

  /// <inheritdoc />
  public async Task CompleteIntegrityDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var fn = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_delivery_complete");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant; every argument is bound.
    cmd.CommandText = $"SELECT {fn}(@p_delivery_id)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("p_delivery_id", deliveryId);
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task FailIntegrityDeliveryAsync(
      Guid deliveryId, string failure, TimeSpan backoff, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(failure);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var fn = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_integrity_delivery_fail");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant; every argument is bound.
    cmd.CommandText = $"SELECT {fn}(@p_delivery_id, @p_error, @p_backoff_seconds)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("p_delivery_id", deliveryId);
    cmd.Parameters.AddWithValue("p_error", failure);
    cmd.Parameters.AddWithValue("p_backoff_seconds", _wholeSeconds(backoff));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  // Whole seconds, rounded up, so a sub-second lease or backoff is never truncated to "due now".
  private static int _wholeSeconds(TimeSpan span) => (int)Math.Ceiling(Math.Max(0, span.TotalSeconds));

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
      var schema = _resolveSchema(typeof(OutboxRecord));
      var fn = BuildSchemaQualifiedName(schema, "wh_integrity_ledger_gauges");
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from a validated schema constant.
      cmd.CommandText = $"SELECT unhealed_buckets, repair_exhausted, oldest_unhealed_secs, answer_pending_buckets, answered_buckets, deliveries_pending, oldest_delivery_secs, unhealed_deficit_events, unhealable_buckets, deficit_buckets, surplus_buckets, digest_mismatch_buckets, unknown_kind_buckets FROM {fn}(@max)";
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
        AnswerPendingBuckets = reader.GetInt64(3),
        AnsweredBuckets = reader.GetInt64(4),
        IntegrityDeliveriesPending = reader.GetInt64(5),
        OldestIntegrityDeliveryAgeSeconds = reader.GetDouble(6),
        UnhealedDeficitEvents = reader.GetInt64(7),
        UnhealableBuckets = reader.GetInt64(8),
        DeficitBuckets = reader.GetInt64(9),
        SurplusBuckets = reader.GetInt64(10),
        DigestMismatchBuckets = reader.GetInt64(11),
        UnknownKindBuckets = reader.GetInt64(12),
      };
      await reader.DisposeAsync().ConfigureAwait(false);
      // Per-origin verified watermarks for the sealed_through gauge — a handful of rows read in
      // the same breath (same connection scope, same cadence) as the ledger summary.
      var sealsSchema = BuildSchemaQualifiedName(schema, "wh_integrity_seals");
      await using var sealsCmd = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified table name built from a validated schema constant.
      // #2261: with the latest commit sequence the origin's checkpoints announced, for verified_percent.
      var knownOrigins = BuildSchemaQualifiedName(schema, "wh_integrity_known_origins");
      sealsCmd.CommandText = $"SELECT s.origin_service_id, s.sealed_through, k.last_commit_sequence FROM {sealsSchema} s LEFT JOIN {knownOrigins} k ON k.origin_service_id = s.origin_service_id";
#pragma warning restore S2077
      var seals = new List<Whizbang.Core.Observability.OriginSeal>();
      await using (var sealsReader = await sealsCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
        while (await sealsReader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          seals.Add(new Whizbang.Core.Observability.OriginSeal(sealsReader.GetGuid(0), sealsReader.GetInt64(1),
            await sealsReader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : sealsReader.GetInt64(2)));
        }
      }
      return snapshot with { Seals = seals };
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; a source-generated message would require making this type partial.
      _log.LogWarning(ex,
        "Integrity ledger summary failed; convergence gauges will read as healthy until this is resolved.");
#pragma warning restore CA1848
      return Whizbang.Core.Observability.LedgerGaugeSnapshot.Empty;
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// One grouped read (<c>wh_integrity_ledger_backlog</c>, migration 399). An origin is named by its last checkpoint's
  /// service name, and by its id when none is recorded. Degrades to the not-reported reading rather than throwing, and is
  /// logged: no series is better than a zero, which would read as a healed service.
  /// </remarks>
  public async Task<Whizbang.Core.Observability.LedgerBacklogSnapshot> GetIntegrityLedgerBacklogAsync(
      int maxRepairAttempts, CancellationToken cancellationToken = default) {
    try {
      return await _withCoordinatorCommandAsync(async (cmd, schema) => {
        cmd.CommandText = $"SELECT origin_service_id, origin_service_name, state, buckets, outbound_deliveries FROM {BuildSchemaQualifiedName(schema, "wh_integrity_ledger_backlog")}(@p_max)";
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_MAX, maxRepairAttempts));
        var buckets = new List<Whizbang.Core.Observability.LedgerBacklogCount>();
        var outbound = 0L;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          outbound = reader.GetInt64(4);
          if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)) {
            continue;   // no origin at all: the row carries only the queue depth
          }
          var origin = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? reader.GetGuid(0).ToString()
            : reader.GetString(1);
          buckets.Add(new Whizbang.Core.Observability.LedgerBacklogCount(origin, reader.GetString(2), reader.GetInt64(3)));
        }
        return new Whizbang.Core.Observability.LedgerBacklogSnapshot {
          Reported = true,
          Buckets = buckets,
          OutboundDeliveries = outbound,
        };
      }, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
#pragma warning disable CA1848 // Rare error path; a source-generated message would require making this type partial.
      _log.LogWarning(ex, "Integrity ledger backlog read failed; the backlog gauges report nothing until it succeeds.");
#pragma warning restore CA1848
      return Whizbang.Core.Observability.LedgerBacklogSnapshot.NotReported;
    }
  }

  /// <inheritdoc />
  /// <remarks>A failure propagates: the maintenance tick withdraws the reading and logs it.</remarks>
  public Task<Whizbang.Core.Observability.IntegritySealReading> GetIntegritySealReadingAsync(
      TimeSpan lookback, TimeSpan recheckEvery, DateTimeOffset now, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      cmd.CommandText = $"""
        SELECT origin_service_id, sealed_through, age_seconds, open_window_events, open_window_capped, recheck_coverage,
               origin_generation, carried_streams, carried_capped
        FROM {BuildSchemaQualifiedName(schema, "wh_integrity_seal_reading")}(@p_lookback, @p_every, @p_now, @p_cap)
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_LOOKBACK, (long)lookback.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_every", (long)recheckEvery.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_NOW, now));
      // #2365: the most index entries one lane's open window, and the carried buckets, read; a count that reaches it is
      // reported as at least that many (capped).
      const int countCap = 100_000;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_cap", countCap));
      var lanes = new List<Whizbang.Core.Observability.LaneSeal>();
      double? coverage = null;
      long? generation = null;
      long carried = 0;
      var carriedCapped = false;
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        coverage = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetDouble(5);
        generation = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(6);
        carried = reader.GetInt64(7);
        carriedCapped = reader.GetBoolean(8);
        if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false)) {
          lanes.Add(new Whizbang.Core.Observability.LaneSeal(
            reader.GetGuid(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetBoolean(4)));
        }
      }
      return new Whizbang.Core.Observability.IntegritySealReading {
        Reported = true,
        Lanes = lanes,
        SealRecheckCoverageRatio = coverage,
        OriginGeneration = generation,
        CarriedStreams = carried,
        CarriedStreamsCapped = carriedCapped,
      };
    }, cancellationToken);

  private static void _bindDivergenceKey(Npgsql.NpgsqlCommand cmd, IntegrityRepairLedger.DivergenceKey key) {
    cmd.Parameters.AddWithValue(P_ORIGIN_SERVICE_ID, key.OriginServiceId);
    cmd.Parameters.AddWithValue("p_tenant_scope", (object?)key.TenantScope ?? string.Empty);
    cmd.Parameters.AddWithValue("p_event_type", key.EventType);
    cmd.Parameters.AddWithValue(P_STREAM_ID, key.StreamId);
  }

  /// <summary>
  /// Calls a ledger function. On failure the caller's prior behaviour is preserved via
  /// <paramref name="failOpen"/>: reporting proceeds (over-reporting is recoverable), repair does
  /// not (an unbounded repair request against real data is not).
  /// </summary>
  private async Task<int?> _integrityLedgerOutcomeAsync(
      string fn, Action<Npgsql.NpgsqlCommand> bind, bool failOpen, CancellationToken ct) {
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
      var schema = _resolveSchema(typeof(OutboxRecord));
      var qualified = BuildSchemaQualifiedName(schema, fn);
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), ct);
      var conn = __scope.Connection;
      await using var cmd = conn.CreateCoordinatorCommand();
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
      // 399 (#2256): each single-key function answers a number (an outcome, or the rows it closed), never null.
      return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      // Never silent: a swallowed failure here degrades convergence invisibly, which is exactly
      // how a broken ledger would masquerade as a working one.
#pragma warning disable CA1848 // Rare error path; a source-generated message would require making this type partial.
      _log.LogWarning(ex,
        "Integrity ledger call {Function} failed; continuing with failOpen={FailOpen}. " +
        "Convergence bounding is degraded until this is resolved.", fn, failOpen);
#pragma warning restore CA1848
      return null;
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<TableRewriteCandidate>> GetTablesNeedingRewriteAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await TableMaintenanceSql.TablesNeedingRewriteAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RewriteRegrowthFloorSqlTests.cs:TheCoordinator_ReadsTheQueueStatusAsync</tests>
  public async Task<TableRewriteQueueStatus> GetTableRewriteQueueStatusAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await TableMaintenanceSql.QueueStatusAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RewriteRegrowthFloorSqlTests.cs:TheCoordinator_ReadsATablesTotalBytesAsync</tests>
  public async Task<long?> GetTableTotalBytesAsync(string tableName, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await TableMaintenanceSql.TotalBytesAsync(cmd, PgIdentifier.QualifyPrefix(schema), tableName, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// VACUUM FULL cannot be parameterised and cannot run inside a transaction, so the table name is
  /// validated against the framework's own naming rule and quoted before interpolation, and the
  /// command runs on its own connection outside any ambient transaction. Callers only ever pass
  /// names this coordinator itself returned from <see cref="GetTablesNeedingRewriteAsync"/>, but
  /// the check is here rather than at the call site because that is where the injection risk
  /// actually lands. The rewrite runs on the schema-initialization connection when one is configured: a
  /// least-privilege application role cannot rewrite a table it does not own.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableRewriteUnderDataOnlyRoleTests.cs</tests>
  public async Task<double?> RewriteTableAsync(string tableName, CancellationToken cancellationToken = default) {
    TableMaintenanceSql.RequireFrameworkTable(tableName);

    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    var qualified = BuildSchemaQualifiedName(schema, tableName);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;

    // VACUUM FULL and ANALYZE need the table owner's rights, which a least-privilege application role does not
    // have, and Postgres does not refuse them: it warns and skips the table. So when a schema-initialization
    // connection is configured, the role startup migrations use runs them; otherwise the application role
    // runs them only on a table it owns, and a table it does not own is reported, not rewritten (#1351).
    if (_schemaInitConnectionString() is { } initConnectionString) {
      await using var initConnection = new Npgsql.NpgsqlConnection(initConnectionString);
      await initConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
      await TableMaintenanceSql.VacuumFullAndAnalyzeAsync(initConnection, qualified, cancellationToken).ConfigureAwait(false);
    } else if (await TableMaintenanceSql.NotOwnedReasonAsync(conn, qualified, tableName, cancellationToken).ConfigureAwait(false) is { } notOwned) {
      EFCoreWorkCoordinatorLog.TableNotRewritten(_log, tableName, notOwned);
      return null;
    } else {
      await TableMaintenanceSql.VacuumFullAndAnalyzeAsync(conn, qualified, cancellationToken).ConfigureAwait(false);
    }

    return await TableMaintenanceSql.MeasureRatioAsync(conn, tableName, _resolveSchema(typeof(OutboxRecord)), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// The program gets the same connection <c>VACUUM FULL</c> would run on, as libpq environment variables: the
  /// schema-initialization connection when configured, because it owns the table; otherwise the application's, for a
  /// table its role owns. The application's connection string is the configured one, which still carries its
  /// password; a context configured with a data source has none to give, so the program's libpq finds its own (a
  /// password file or service file). After the program reports success the table is analyzed on the owning connection
  /// and re-measured, exactly as after <c>VACUUM FULL</c>.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ExternalTableRewriteTests.cs</tests>
  public async Task<double?> RewriteTableWithAsync(
      string tableName, Func<TableRewriteTarget, CancellationToken, Task<bool>> rewrite, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(rewrite);
    TableMaintenanceSql.RequireFrameworkTable(tableName);

    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    var qualified = BuildSchemaQualifiedName(schema, tableName);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;

    var initConnectionString = _schemaInitConnectionString();
    if (initConnectionString is null
        && await TableMaintenanceSql.NotOwnedReasonAsync(conn, qualified, tableName, cancellationToken).ConfigureAwait(false) is { } notOwned) {
      EFCoreWorkCoordinatorLog.TableNotRewritten(_log, tableName, notOwned);
      return null;
    }
    var clientConnectionString = initConnectionString ?? _configuredConnectionString() ?? conn.ConnectionString;
    var target = new TableRewriteTarget(qualified, LibpqEnvironment.FromConnectionString(clientConnectionString));
    if (!await rewrite(target, cancellationToken).ConfigureAwait(false)) {
      return null;
    }

    if (initConnectionString is not null) {
      await using var initConnection = new Npgsql.NpgsqlConnection(initConnectionString);
      await initConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
      await TableMaintenanceSql.AnalyzeAsync(initConnection, qualified, cancellationToken).ConfigureAwait(false);
    } else {
      await TableMaintenanceSql.AnalyzeAsync(conn, qualified, cancellationToken).ConfigureAwait(false);
    }
    return await TableMaintenanceSql.MeasureRatioAsync(conn, tableName, _resolveSchema(typeof(OutboxRecord)), cancellationToken).ConfigureAwait(false);
  }


  /// <summary>
  /// The connection string the context was configured with, which still carries its password (the live connection's
  /// has it redacted), or null for a context configured with a data source.
  /// </summary>
  private string? _configuredConnectionString() =>
    Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension.Extract(
      Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
        .GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>(_dbContext)).ConnectionString;

  /// <inheritdoc />
  /// <remarks>
  /// The estimate is the catalog read the rewrites re-measure with. The approximate measurement calls
  /// <c>wh_measure_table_bloat_approx</c>, which runs <c>pgstattuple_approx</c> in whichever schema the extension lives
  /// in and raises when it is not installed. The exact <c>pgstattuple</c> scan is refused: the library never runs it.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs</tests>
  public async Task<double?> MeasureTableBloatAsync(
      string tableName, BloatMeasurementMethod method, CancellationToken cancellationToken = default) {
    TableMaintenanceSql.RequireFrameworkTable(tableName);
    if (method is not (BloatMeasurementMethod.Estimate
        or BloatMeasurementMethod.Approx)) {
      throw new NotSupportedException(
        $"Bloat is measured by estimate or approx only; {method} (the full pgstattuple scan) is never run by the library.");
    }

    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    if (method == BloatMeasurementMethod.Estimate) {
      return await TableMaintenanceSql.MeasureRatioAsync(conn, tableName, _resolveSchema(typeof(OutboxRecord)), cancellationToken).ConfigureAwait(false);
    }

    await using var cmd = conn.CreateCoordinatorCommand();
    return await TableMaintenanceSql.MeasureApproxAsync(cmd, PgIdentifier.QualifyPrefix(schema), tableName, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TableBloatMeasurementSqlTests.cs:AMeasuredRequest_IsQueuedWithItsRatioAndMethod_AndTheQueueOrdersByTheMeasuredRatioAsync</tests>
  public async Task RequestMeasuredTableRewriteAsync(
      string tableName, BloatMeasurement measurement, double estimatedRatio,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await TableMaintenanceSql.RequestMeasuredRewriteAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), tableName, measurement, estimatedRatio, cancellationToken).ConfigureAwait(false);
  }


  /// <summary>
  /// Decides whether the prerequisites a squeeze needs are met (null) or names the first unmet one. Always
  /// <see cref="Whizbang.Data.Postgres.PgSqueezePrerequisites.Unmet"/> in production; a test replaces it to reach the
  /// call on a server without logical WAL, which the test image is.
  /// </summary>
  internal Func<Whizbang.Data.Postgres.PgSqueezePrerequisiteFacts, string, string?> SqueezePrerequisitesUnmet { get; init; } =
    Whizbang.Data.Postgres.PgSqueezePrerequisites.Unmet;

  /// <inheritdoc />
  /// <remarks>
  /// The prerequisites are read on the connection the rewrite runs on, as its role: the schema-initialization connection
  /// when one is configured (its role owns the tables), otherwise the coordinator's own. The table name is validated
  /// against the framework's naming rule, as <see cref="RewriteTableAsync"/> does, and pg_squeeze is given the schema and
  /// name the catalog resolves it to. <c>squeeze_table</c> waits for its background worker, so the call has no timeout.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OnlineTableRewriteSqlTests.cs</tests>
  public async Task<double?> SqueezeTableAsync(string tableName, CancellationToken cancellationToken = default) {
    TableMaintenanceSql.RequireFrameworkTable(tableName);

    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var qualified = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), tableName);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;

    bool squeezed;
    if (_schemaInitConnectionString() is { } initConnectionString) {
      await using var initConnection = new Npgsql.NpgsqlConnection(initConnectionString);
      await initConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
      squeezed = await _squeezeAndAnalyzeAsync(initConnection, qualified, tableName, cancellationToken).ConfigureAwait(false);
    } else {
      squeezed = await _squeezeAndAnalyzeAsync(conn, qualified, tableName, cancellationToken).ConfigureAwait(false);
    }
    return squeezed ? await TableMaintenanceSql.MeasureRatioAsync(conn, tableName, _resolveSchema(typeof(OutboxRecord)), cancellationToken).ConfigureAwait(false) : null;
  }

  /// <summary>Checks the prerequisites, then squeezes and analyzes the table; false (and logged) when one is unmet.</summary>
  private async Task<bool> _squeezeAndAnalyzeAsync(
      System.Data.Common.DbConnection conn, string qualified, string tableName, CancellationToken cancellationToken) {
    if (await TableMaintenanceSql.SqueezeAndAnalyzeAsync(conn, qualified, tableName, SqueezePrerequisitesUnmet, cancellationToken)
          .ConfigureAwait(false) is { } unmet) {
      EFCoreWorkCoordinatorLog.TableNotRewritten(_log, tableName, unmet);
      return false;
    }
    return true;
  }


  /// <inheritdoc />
  /// <remarks>
  /// <c>VACUUM</c> cannot run in a transaction, so it runs on a connection of its own (the
  /// schema-initialization one) or on the coordinator's connection outside any ambient transaction. The
  /// table names are the framework's own constants, so nothing caller-supplied reaches the statement.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkTableVacuumTests.cs</tests>
  public Task<IReadOnlyList<WorkTableVacuum>> VacuumWorkTablesAsync(CancellationToken cancellationToken = default) =>
    VacuumWorkTablesAsync(TimeSpan.Zero, cancellationToken);

  /// <inheritdoc />
  /// <remarks>The loop is <c>TableMaintenanceSql.VacuumWorkTablesAsync</c>, shared with the Dapper coordinator.</remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkTableVacuumTests.cs:ATableVacuumedWithinTheWindow_IsLeftAlone_AndReportedAsRecentAsync</tests>
  public async Task<IReadOnlyList<WorkTableVacuum>> VacuumWorkTablesAsync(TimeSpan skipTablesVacuumedWithin, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    if (_schemaInitConnectionString() is { } initConnectionString) {
      return await _vacuumWorkTablesOnInitAsync(initConnectionString, schema, skipTablesVacuumedWithin, cancellationToken).ConfigureAwait(false);
    }

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    return await TableMaintenanceSql.VacuumWorkTablesAsync(__scope.Connection, table => BuildSchemaQualifiedName(schema, table),
      skipTablesVacuumedWithin, (conn, qualified, table, ct) => TableMaintenanceSql.NotOwnedReasonAsync(conn, qualified, table, ct),
      cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <para>
  /// One catalog read; nothing scans an index. The expected size is the leaf pages the index's rows need at its fill
  /// factor (90 unless set), plus the metapage, and never less than two pages (the metapage and one leaf): each index
  /// tuple is an 8-byte header and the columns' widths (<c>pg_stats.avg_width</c>, else the type's fixed length) rounded
  /// up to 8, plus a 4-byte line pointer, and a page holds its size less its 24-byte header and 16-byte B-tree trailer.
  /// The row count is the index's own <c>reltuples</c>, which counts only the rows a partial index holds. With none
  /// recorded (-1) or a column of unknown width, the expected size is null and the index is not judged.
  /// </para>
  /// <para>
  /// <c>BuildInProgress</c> reads <c>pg_stat_progress_create_index</c>: a build on the index's table, or one in this
  /// database whose table this role is not allowed to see (the view hides it), counts.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs</tests>
  public async Task<IReadOnlyList<QueueIndexBloat>> GetQueueIndexBloatAsync(CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var prefix = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await TableMaintenanceSql.QueueIndexSurveyAsync(cmd, prefix, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <c>DROP INDEX CONCURRENTLY</c> cannot run in a transaction, so it runs on a connection of its own: the
  /// schema-initialization one when configured (its role owns the tables), otherwise the coordinator's, and only for a
  /// table its role owns. The name reaches the statement only once the catalog has said it is an invalid index on a queue
  /// table, and then quoted.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs</tests>
  public Task<bool> DropInvalidQueueIndexAsync(string indexName, CancellationToken cancellationToken = default) =>
    DropInvalidQueueIndexAsync(indexName, QueueIndexRebuild.DefaultLockTimeout, cancellationToken);

  /// <inheritdoc />
  /// <remarks>Each lock the drop waits for is bounded by <paramref name="lockTimeout"/> (#2611).</remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs:Drop_BehindATransactionHoldingTheTable_GivesUpWithinTheLockTimeoutAsync</tests>
  public async Task<bool> DropInvalidQueueIndexAsync(string indexName, TimeSpan lockTimeout, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
    return await _onQueueIndexAsync(indexName, wantValid: false, QueueIndexSql.DROPPED, refused: false,
      (conn, qualified, ct) => TableMaintenanceSql.DropIndexConcurrentlyAsync(conn, qualified, lockTimeout, ct), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// <c>REINDEX INDEX CONCURRENTLY</c> cannot run in a transaction, so it runs on a connection of its own, chosen as for
  /// <see cref="DropInvalidQueueIndexAsync(string, CancellationToken)"/>. It builds a copy while the table stays readable
  /// and writable and swaps it in, bounded by the default lock timeout (#2611).
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs</tests>
  public async Task<long?> ReindexQueueIndexAsync(string indexName, CancellationToken cancellationToken = default) =>
    (await RebuildQueueIndexAsync(indexName, QueueIndexRebuild.DefaultLockTimeout, cancellationToken).ConfigureAwait(false)).SizeAfterBytes;

  /// <inheritdoc />
  /// <remarks>
  /// Each lock the rebuild waits for, the waits for older transactions included, is bounded by
  /// <paramref name="lockTimeout"/> (#2611). Given up, it names the sessions it was waiting on; the invalid copy it left
  /// is the maintenance step's to drop.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueueIndexReindexSqlTests.cs:Rebuild_BehindAnIdleTransaction_GivesUpWithinTheLockTimeout_AndNamesTheBlockerAsync</tests>
  public async Task<QueueIndexRebuild> RebuildQueueIndexAsync(
      string indexName, TimeSpan lockTimeout, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
    return await _onQueueIndexAsync(indexName, wantValid: true, QueueIndexSql.REBUILT, refused: new QueueIndexRebuild(null),
      (conn, qualified, ct) => TableMaintenanceSql.ReindexConcurrentlyAsync(conn, qualified, lockTimeout, ct), cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Runs <paramref name="work"/> on a connection of its own opened from the schema-initialization connection string.</summary>
  private static async Task<T> _onSchemaInitConnectionAsync<T>(
      string initConnectionString, string qualified,
      Func<System.Data.Common.DbConnection, string, CancellationToken, Task<T>> work, CancellationToken cancellationToken) {
    await using var initConnection = new Npgsql.NpgsqlConnection(initConnectionString);
    await initConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
    return await work(initConnection, qualified, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Runs <paramref name="work"/> on the index when the catalog says it is an index on a queue table with the validity
  /// <paramref name="wantValid"/>, on a connection whose role owns the table; otherwise logs why not and returns
  /// <paramref name="refused"/>. The work is handed the index's name quoted and schema-qualified.
  /// </summary>
  private async Task<T> _onQueueIndexAsync<T>(
      string indexName, bool wantValid, string action, T refused,
      Func<System.Data.Common.DbConnection, string, CancellationToken, Task<T>> work, CancellationToken cancellationToken) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    var qualified = BuildSchemaQualifiedName(schema, Whizbang.Data.Postgres.PgIdentifier.Quote(indexName));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;

    var (table, valid) = await TableMaintenanceSql.QueueIndexLookupAsync(conn, BuildSchemaQualifiedName(schema, ""), indexName, cancellationToken).ConfigureAwait(false);
    if (TableMaintenanceSql.QueueIndexRefusal(table, valid, wantValid) is { } refusal) {
      EFCoreWorkCoordinatorLog.QueueIndexNotTouched(_log, indexName, action, refusal);
      return refused;
    }

    if (_schemaInitConnectionString() is { } initConnectionString) {
      return await _onSchemaInitConnectionAsync(initConnectionString, qualified, work, cancellationToken).ConfigureAwait(false);
    }
    var tableQualified = BuildSchemaQualifiedName(schema, table!);
    if (await TableMaintenanceSql.NotOwnedReasonAsync(conn, tableQualified, table!, cancellationToken).ConfigureAwait(false) is { } notOwned) {
      EFCoreWorkCoordinatorLog.QueueIndexNotTouched(_log, indexName, action, notOwned);
      return refused;
    }
    return await work(conn, qualified, cancellationToken).ConfigureAwait(false);
  }



  /// <summary>Every work table on the schema-initialization connection, which owns them.</summary>
  private static async Task<IReadOnlyList<WorkTableVacuum>> _vacuumWorkTablesOnInitAsync(
      string initConnectionString, string schema, TimeSpan skipTablesVacuumedWithin, CancellationToken cancellationToken) {
    await using var initConnection = new Npgsql.NpgsqlConnection(initConnectionString);
    await initConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
    return await TableMaintenanceSql.VacuumWorkTablesAsync(initConnection, table => BuildSchemaQualifiedName(schema, table),
      skipTablesVacuumedWithin, notOwned: null, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The schema-initialization (<c>-init</c>) connection string configured for this context, or null. The
  /// application's services come from the context itself: registered through DI, it carries them. Every
  /// configured context has the core extension (the options builder adds it), so only the provider can be absent.
  /// </summary>
  private string? _schemaInitConnectionString() {
    var applicationServices = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
      .GetService<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptions>(_dbContext)
      .FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()!.ApplicationServiceProvider;
    return applicationServices is null ? null : SchemaInitConnection.For(applicationServices, typeof(TDbContext));
  }





  /// <inheritdoc />
  public async Task RequestTableRewriteAsync(string tableName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await TableMaintenanceSql.RequestRewriteAsync(cmd, PgIdentifier.QualifyPrefix(schema), tableName, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>This driver keeps instance rows, so a stage counts as recorded only once its write lands (#1636).</remarks>
  public bool RecordsInstanceTransitions => true;

  /// <inheritdoc />
  public bool ReadsCursorsInBatches => true;

  /// <inheritdoc />
  public async Task<bool> RecordInstanceTransitionAsync(
      Whizbang.Core.Startup.InstanceTransition transition, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(transition);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    var fn = BuildSchemaQualifiedName(schema, "wh_record_instance_transition");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {fn}(@id, @stage, @message, @phase, @version)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("id", transition.InstanceId);
    // A null stage is a phase-only write (a pause, a stand-by): the row keeps its stage and message.
    cmd.Parameters.Add(new NpgsqlParameter("stage", NpgsqlTypes.NpgsqlDbType.Smallint) {
      Value = transition.Stage is { } stage ? (short)stage : DBNull.Value,
    });
    cmd.Parameters.Add(new NpgsqlParameter("message", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)transition.Message ?? DBNull.Value,
    });
    cmd.Parameters.AddWithValue("phase", transition.Phase.ToString());
    // A null version keeps the recorded one.
    cmd.Parameters.Add(new NpgsqlParameter("version", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)transition.LibraryVersion ?? DBNull.Value,
    });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return ScalarResult.IsTrue(result);
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
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorFleetSql.GetStandbyRequestAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Executes a schema-qualified scalar function with named parameters — the shared
  /// shape of the standby/eviction calls.</summary>
  private async Task<T?> _scalarFunctionAsync<T>(
      string functionName, CancellationToken cancellationToken, params (string Name, object Value)[] args) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    var result = await CoordinatorFleetSql.ScalarFunctionAsync(cmd, PgIdentifier.QualifyPrefix(schema), functionName, cancellationToken, args).ConfigureAwait(false);
    return result is T t ? t : default;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FillFactorDecreaseRewriteTests.cs:TheRewriteHistory_RecordsEachRewrite_AndStaysBoundedAsync</tests>
  public async Task CompleteTableRewriteAsync(TableRewriteOutcome outcome, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(outcome);
    TableMaintenanceSql.RequireFrameworkTable(outcome.TableName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await TableMaintenanceSql.CompleteRewriteAsync(cmd, PgIdentifier.QualifyPrefix(schema), outcome, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task ClearTableRewriteRequestAsync(string tableName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await TableMaintenanceSql.ClearRewriteRequestAsync(cmd, PgIdentifier.QualifyPrefix(schema), tableName, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<EphemeralPointerPruneResult> PruneAncientEphemeralPointersAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorRetentionSql.PruneAncientEphemeralPointersAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:TheLookup_AnswersTheHandledSubset_AndTheCoverageStartAsync</tests>
  public async Task<HandledLedgerAnswer> LookupHandledEventsAsync(
    IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return new HandledLedgerAnswer(null, new HashSet<Guid>());
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorRetentionSql.LookupHandledEventsAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), eventIds, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/HandledLedgerSqlTests.cs:TheSweep_MovesCoverageUpToTheCutoff_ThenRemovesOlderRecordsAsync</tests>
  public async Task<long> SweepHandledLedgerAsync(
    DateTimeOffset now, TimeSpan keep, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorRetentionSql.SweepHandledLedgerAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), now, keep, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<Whizbang.Core.Workers.OperationalRetentionSweepResult>> SweepOperationalRetentionAsync(
    IReadOnlyDictionary<Whizbang.Core.Workers.OperationalCategory, TimeSpan> retention, int batchSize,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(retention);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorRetentionSql.SweepOperationalRetentionAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), retention, batchSize, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> CloseDigestEpochsAsync(
    int settleSeconds, int maxEpochs, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    var fn = BuildSchemaQualifiedName(schema, "close_digest_epochs");
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
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
    return ScalarResult.IntOrZero(result);
  }

  /// <inheritdoc />
  public async Task<int> CloseDigestEpochsAsync(
    int settleSeconds, int maxEpochs, long epochWidth, CancellationToken cancellationToken = default) {
    await _withCoordinatorCommandAsync(async (cmd, schema) => {
      // Its own statement, before the closure: a write in the closure's own snapshot would not be seen by it.
      cmd.CommandText =
        $"INSERT INTO {BuildSchemaQualifiedName(schema, SETTINGS_TABLE)} (setting_key, setting_value, value_type, description) " +
        "VALUES ('integrity_epoch_width', @p_width, 'integer', 'Sequence width of one digest epoch (Whizbang:StreamIntegrity:Sealing:EpochWidth). Pinned on a lane''s frontier at its first close.') " +
        "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value " +
        $"WHERE {BuildSchemaQualifiedName(schema, SETTINGS_TABLE)}.setting_value IS DISTINCT FROM EXCLUDED.setting_value";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_width", epochWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken).ConfigureAwait(false);
    return await CloseDigestEpochsAsync(settleSeconds, maxEpochs, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task SetCutGatesAsync(bool waitForSeal, bool waitForConsumers, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // #2363: one statement for both settings, and a row is rewritten only when its value changed, so a maintenance
      // cycle that changes nothing writes nothing.
      var settings = BuildSchemaQualifiedName(schema, SETTINGS_TABLE);
      cmd.CommandText =
        $"INSERT INTO {settings} (setting_key, setting_value, value_type, description) VALUES " +
        "('integrity_cut_waits_for_seal', @p_seal, 'boolean', 'A cut removes only history sealed here (Whizbang:StreamIntegrity:Sealing:Enabled).'), " +
        "('integrity_cut_waits_for_consumers', @p_consumers, 'boolean', 'A cut of local history also waits for every consumer''s reported seal (Whizbang:StreamIntegrity:Sealing:CutWaitsForConsumers).') " +
        "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value " +
        $"WHERE {settings}.setting_value IS DISTINCT FROM EXCLUDED.setting_value";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_seal", waitForSeal ? "true" : "false"));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_consumers", waitForConsumers ? "true" : "false"));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task RecordConsumerSealAsync(string requesterService, long sealedThrough, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrEmpty(requesterService);
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText = $"SELECT {BuildSchemaQualifiedName(schema, "wh_record_consumer_seal")}(@p_requester, @p_sealed, NOW())";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_requester", requesterService));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_sealed", sealedThrough));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> RollOffCarriedDetailAsync(TimeSpan olderThan, int batchSize, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText = $"SELECT {BuildSchemaQualifiedName(schema, "wh_roll_off_carried_detail")}(@p_older::interval, @p_batch)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_older", $"{(long)olderThan.TotalSeconds} seconds"));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_batch", Math.Max(1, batchSize)));
      return ScalarResult.IntOrZero(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }, cancellationToken);

  /// <inheritdoc />
  public Task<long?> GetIntegritySettledMaxAsync(
    Guid? originServiceId, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText = $"SELECT {BuildSchemaQualifiedName(schema, "integrity_settled_max")}(@p_origin, NOW(), @p_settle)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)originServiceId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, (int)settleWindow.TotalSeconds));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is long max ? (long?)max : null;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<long> GetIntegrityOriginGenerationAsync(CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      var settings = BuildSchemaQualifiedName(schema, SETTINGS_TABLE);
      cmd.CommandText =
        $"SELECT COALESCE((SELECT setting_value::bigint FROM {settings} WHERE setting_key = 'integrity_origin_generation'), 0)";
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return ScalarResult.LongOrZero(result);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<bool> EnsureIntegritySealGenerationAsync(
    Guid originServiceId, long generation, CancellationToken cancellationToken = default) =>
    EnsureIntegritySealGenerationAsync(originServiceId, generation, rewrite: null, cancellationToken);

  /// <inheritdoc />
  public Task<bool> EnsureIntegritySealGenerationAsync(
    Guid originServiceId, long generation, IntegrityHistoryRewrite? rewrite, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        $"SELECT {BuildSchemaQualifiedName(schema, "wh_integrity_seal_guard")}(@p_origin, @p_generation, @p_rewritten_since, @p_rewritten_from)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_generation", generation));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_rewritten_since", NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = (object?)rewrite?.SinceGeneration ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_rewritten_from", NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = (object?)rewrite?.RewrittenFrom ?? DBNull.Value
      });
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return ScalarResult.IsTrue(result);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<long?> GetIntegritySealGenerationAsync(Guid originServiceId, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      cmd.CommandText = $"SELECT origin_generation FROM {schema}.wh_integrity_seals WHERE origin_service_id = @p_origin";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, originServiceId));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return result is long generation ? (long?)generation : null;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IntegrityHistoryRewrite?> GetIntegrityHistoryRewriteAsync(long sinceGeneration, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        $"SELECT vouched, rewritten_from FROM {BuildSchemaQualifiedName(schema, "wh_integrity_history_rewrite")}(@p_since)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SINCE, sinceGeneration));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      if (!reader.GetBoolean(0)) {
        return null;   // the rewrite log does not reach back to the asked generation.
      }
      long? rewrittenFrom = reader.IsDBNull(1) ? null : reader.GetInt64(1);
      return new IntegrityHistoryRewrite(sinceGeneration, rewrittenFrom);
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
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, (int)settleWindow.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_MAX, Math.Max(1, maxEpochs)));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      return new EpochVerificationResult(reader.GetInt32(0), reader.GetInt32(1));
    }, cancellationToken);

  /// <inheritdoc />
  public Task<EpochVerificationResult> RecheckDigestEpochsAsync(
    SealRecheckRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        "SELECT epochs_checked, epochs_drifted " +
        $"FROM {BuildSchemaQualifiedName(schema, "wh_recheck_digest_epochs")}(NOW(), @p_settle, @p_max, @p_lookback, @p_every, @p_refold)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, (int)request.SettleWindow.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_MAX, Math.Max(1, request.MaxEpochs)));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_LOOKBACK, (long)request.Lookback.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_every", (long)request.RecheckEvery.TotalSeconds));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_refold", request.Refold));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      return new EpochVerificationResult(reader.GetInt32(0), reader.GetInt32(1));
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<StreamDigest>?> ComputeStreamDigestsForChunkAsync(
    Guid originServiceId, IReadOnlyList<Guid> streamIds,
    long? sinceSequence, long? untilSequence, TimeSpan settleWindow,
    CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      // Bounded by the chunk: stream_id = ANY(named set). A NULL origin sequence never satisfies
      // a window comparison, so windowed folds naturally exclude unsequenced rows — matching the
      // answer side; a null window includes them (full history).
      var digestId = DigestRuleSql.DigestIdEs(schema);
      // #2363: an unwindowed (full history) fold adds what cuts carried, as the bucket does; a window never reaches below
      // a cut, which removes only sealed, verified history.
      cmd.CommandText = $"""
        SELECT f.tenant, f.event_type, f.stream_id, bit_xor(f.lo), bit_xor(f.hi), SUM(f.n)::int, bit_xor(f.ilo), bit_xor(f.ihi)
        FROM (
          SELECT COALESCE(es.scope->>'t', '') AS tenant, {DigestRuleSql.DIGEST_TYPE_ES} AS event_type, es.stream_id,
                 {schema}._wh_digest_lane({digestId}, 0) AS lo,
                 {schema}._wh_digest_lane({digestId}, 1) AS hi,
                 1 AS n,
                 {schema}._wh_digest_lane({digestId}, 2) AS ilo,
                 {schema}._wh_digest_lane({digestId}, 3) AS ihi
          FROM {schema}.wh_event_store es
          WHERE es.origin_service_id = @p_origin
            AND es.stream_id = ANY(@p_streams::uuid[])
            AND ((@p_since::bigint) IS NULL OR es.origin_commit_sequence >= @p_since)
            AND ((@p_until::bigint) IS NULL OR es.origin_commit_sequence < @p_until)
            AND {DigestRuleSql.EligibleEs}
            AND {DigestRuleSql.SlotCountsEs(schema)}
            AND es.created_at < NOW() - @p_settle::interval
          UNION ALL
          {CarriedDigestSql(schema, "d.origin_service_id = @p_origin AND d.stream_id = ANY(@p_streams::uuid[]) AND (@p_since::bigint) IS NULL AND (@p_until::bigint) IS NULL", byType: false)}
        ) f
        GROUP BY 1, 2, 3
        ORDER BY 1, 2, 3
        """;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAMS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = streamIds.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SINCE, NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = (object?)sinceSequence ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_until", NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = (object?)untilSequence ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, $"{(int)settleWindow.TotalSeconds} seconds"));

      return (IReadOnlyList<StreamDigest>?)await _readStreamDigestsAsync(
        cmd, hasUpdatedAt: false, typeLevel: false, cancellationToken, hasIdLanes: true).ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public async Task<IReadOnlyList<StreamDigest>> ComputeUnsequencedReceivedDigestsAsync(
    Guid originServiceId, IReadOnlyList<string> eventTypes, IReadOnlyList<Guid>? streamIds, TimeSpan settleWindow,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    if (eventTypes.Count == 0 || streamIds is { Count: 0 }) {
      return [];
    }
    return await _withCoordinatorCommandAsync(async (cmd, schema) => {
      var byStream = streamIds is not null;
      cmd.CommandText = byStream ? UnsequencedByStreamSql(schema) : UnsequencedByTypeSql(schema);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes.ToArray()
      });
      if (byStream) {
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAMS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
          Value = streamIds!.ToArray()
        });
      }
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, $"{(int)settleWindow.TotalSeconds} seconds"));
      return await _readStreamDigestsAsync(cmd, hasUpdatedAt: false, typeLevel: !byStream, cancellationToken, hasIdLanes: byStream)
        .ConfigureAwait(false);
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// #2102: the fold of the named streams' events held from the origin without its commit sequence, one row per
  /// (tenant, type, stream). Bounded by the chunk: the named streams' rows, found through the stream index.
  /// </summary>
  /// <param name="schema">The validated schema name.</param>
  /// <returns>The query text.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedReceivedDigestSqlTests.cs:ByStream_UnderAGenericPlan_ReadsTheNamedStreamsRows_NotTheOriginsLaneAsync</tests>
#pragma warning disable S2077, RCS1158 // Validated schema constant, every value bound; shared by every generic instantiation.
  internal static string UnsequencedByStreamSql(string schema) => $"""
        SELECT COALESCE(es.scope->>'t', '') AS tenant, es.event_type, es.stream_id,
               bit_xor({schema}._wh_digest_lane(es.event_id, 0)) AS digest_lo,
               bit_xor({schema}._wh_digest_lane(es.event_id, 1)) AS digest_hi,
               COUNT(*)::int,
               bit_xor({schema}._wh_digest_lane(es.event_id, 2)) AS id_lo,
               bit_xor({schema}._wh_digest_lane(es.event_id, 3)) AS id_hi
        FROM {schema}.wh_event_store es
        WHERE es.stream_id = ANY(@p_streams::uuid[])
          AND es.origin_service_id = @p_origin
          AND es.origin_commit_sequence IS NULL
          AND es.event_type = ANY(@p_types::text[])
          AND {DigestRuleSql.EligibleEs}
          AND es.created_at < NOW() - @p_settle::interval
        GROUP BY 1, 2, 3
        ORDER BY 1, 2, 3
        """;

  /// <summary>
  /// #2102: the fold of the events held from the origin without its commit sequence, one row per (tenant, type).
  /// #2351: maintained as the rows change (migration 452), so the answer costs the types it returns, the changes since
  /// the last answer and the held rows younger than the settle window, never the size of the held history.
  /// </summary>
  /// <param name="schema">The validated schema name.</param>
  /// <returns>The query text.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ByType_UnderAGenericPlan_ReadsOnlyTheUnsettledRowsOfTheEventStore_NeverItsHistoryAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedHeldFoldCostSqlTests.cs:ByType_ALargeHistory_CostsWhatASmallOneDoes_ForTheSameNewWorkAsync</tests>
  internal static string UnsequencedByTypeSql(string schema) => $"""
        SELECT d.scope_tenant AS tenant, d.event_type, d.digest_lo, d.digest_hi, d.event_count
        FROM {schema}.wh_unsequenced_received_type_digests(@p_origin, @p_types::text[], @p_settle::interval) d
        """;
#pragma warning restore S2077, RCS1158


  /// <inheritdoc />
  public Task<long> GetIntegritySealAsync(Guid originServiceId, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      cmd.CommandText = $"SELECT sealed_through FROM {schema}.wh_integrity_seals WHERE origin_service_id = @p_origin";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, originServiceId));
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return ScalarResult.LongOrZero(result);
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
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_through", through));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task IntegrityRecordCompareOutcomeAsync(Guid originServiceId, bool fullMatch, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync<int>(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText = $"SELECT {BuildSchemaQualifiedName(schema, "wh_integrity_record_compare_outcome")}(@p_origin, @p_full_match)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_full_match", fullMatch));
      return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IntegrityAskDecision> IntegrityDecideOriginAskAsync(
      Guid originServiceId, DateTimeOffset now, TimeSpan maxSkip, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText = $"SELECT {BuildSchemaQualifiedName(schema, "wh_integrity_decide_origin_ask")}(@p_origin, @p_now, @p_max_skip)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_NOW, now));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_max_skip", NpgsqlTypes.NpgsqlDbType.Interval) { Value = maxSkip });
      var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return (IntegrityAskDecision)Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
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
      AddDigestFilterParams(cmd, originServiceId, eventTypes);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SINCE, sinceSequence));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_until", through.Value));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, (int)settleWindow.TotalSeconds));

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
      cmd.CommandText = WindowedStreamDigestSql(schema);
      AddDigestFilterParams(cmd, originServiceId, eventTypes);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SINCE, sinceSequence));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_until", through.Value));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_resume", NpgsqlTypes.NpgsqlDbType.Uuid) {
        Value = (object?)resumeAfterStreamId ?? DBNull.Value
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_LIMIT, pageBound + 1));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, (int)settleWindow.TotalSeconds));

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
            IdLo = reader.GetInt64(6),
            IdHi = reader.GetInt64(7),
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
  public Task<StreamTruncateResult> TruncateStreamPrefixAsync(
    Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) =>
    _withPrefixTruncateCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.TruncateAsync(cmd, prefix, streamId, throughVersion, archive, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<EphemeralDestructionTarget>> GetStreamTruncateTargetsAsync(
    Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
    _withPrefixTruncateCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.TargetsAsync(cmd, prefix, streamId, throughVersion, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<ArchivedEvent>> GetArchivedEventsAsync(
    Guid streamId, CancellationToken cancellationToken = default) =>
    _withPrefixTruncateCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.ArchivedEventsAsync(cmd, prefix, streamId, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<string>> GetConsumingPerspectiveNamesAsync(
    Guid streamId, long throughVersion, CancellationToken cancellationToken = default) =>
    _withPrefixTruncateCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.ConsumingPerspectiveNamesAsync(cmd, prefix, streamId, throughVersion, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<long?> GetEventVersionAsync(Guid eventId, CancellationToken cancellationToken = default) =>
    _withPrefixTruncateCommandAsync((cmd, prefix) =>
      StreamPrefixTruncateSql.EventVersionAsync(cmd, prefix, eventId, cancellationToken), cancellationToken);

  /// <summary>
  /// One prefix-truncate statement from <see cref="StreamPrefixTruncateSql"/>, the copy the Dapper coordinator runs too
  /// (#2043): the gate, this context's connection, the schema prefix and a command with the coordinator timeout. The
  /// slot is taken under the public member's name (<paramref name="caller"/>), so the gate's holder diagnostics name
  /// the operation, not this helper.
  /// </summary>
  private async Task<T> _withPrefixTruncateCommandAsync<T>(
      Func<System.Data.Common.DbCommand, string, Task<T>> operation, CancellationToken cancellationToken,
      [System.Runtime.CompilerServices.CallerMemberName] string caller = "<unknown>") {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken, caller).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await operation(cmd, PgIdentifier.QualifyPrefix(schema)).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralDestructionTargetSqlTests.cs:GetEphemeralBodiesAboutToReap_AnAfterTtlBodyNotYetExpired_IsNotOffered_AndOfferedMatchesDeletedAsync</tests>
  public async Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    // The exact predicate of the reap's DELETE, as a SELECT, shared with the Dapper coordinator (#2039).
    return await Whizbang.Data.Postgres.EphemeralDestructionSql.CollectAboutToReapAsync(
      cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task HoldEphemeralDestructionAsync(
    IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    await Whizbang.Data.Postgres.EphemeralDestructionSql.HoldAsync(
      cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), eventIds, holdUntil, cancellationToken).ConfigureAwait(false);
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
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await Whizbang.Data.Postgres.EphemeralDestructionSql.RecordFailureAsync(
      cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), eventIds, retryHoldUntil, maxRetries, onFailure,
      cancellationToken).ConfigureAwait(false);
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
    var schema = _resolveSchema(typeof(OutboxRecord));
    var tableName = BuildSchemaQualifiedName(schema, "wh_service_instances");
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"UPDATE {tableName} SET last_heartbeat_at = NOW() WHERE instance_id = @p_id AND last_heartbeat_at < NOW() - make_interval(secs => @p_freshness)";
    cmd.Parameters.Add(new NpgsqlParameter("p_id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = _instanceProvider.InstanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_freshness", NpgsqlTypes.NpgsqlDbType.Integer) { Value = OPPORTUNISTIC_HEARTBEAT_FRESHNESS_SECONDS });
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task CompletePerspectiveAsync(
    IReadOnlyList<PerspectiveCursorCompletion> cursors,
    IReadOnlyList<Guid> eventWorkIds,
    bool debugMode,
    CancellationToken cancellationToken = default) =>
    CompletePerspectiveAsync(cursors, eventWorkIds, eventWorkEpochs: null, debugMode, cancellationToken);

  /// <inheritdoc />
  public async Task CompletePerspectiveAsync(
    IReadOnlyList<PerspectiveCursorCompletion> cursors,
    IReadOnlyList<Guid> eventWorkIds,
    IReadOnlyList<long?>? eventWorkEpochs,
    bool debugMode,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(cursors);
    ArgumentNullException.ThrowIfNull(eventWorkIds);
    if (cursors.Count == 0 && eventWorkIds.Count == 0) {
      return;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "complete_perspective");

    // #2556: each cursor and work id names the epoch its work was claimed under.
    var cursorsJson = _serializePerspectiveCompletions([.. TenureFill.Cursors(_tenures, cursors)]);
    var idArray = AsArray(eventWorkIds);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_cursors::jsonb, @p_ids, @p_debug_mode, @p_epochs)";
    cmd.Parameters.Add(new NpgsqlParameter("p_cursors", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = cursorsJson });
    cmd.Parameters.Add(new NpgsqlParameter(P_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_debug_mode", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = debugMode });
    // 570 (#2556): the work ids' epochs; each cursor names its own.
    cmd.Parameters.Add(EpochsParameter("p_epochs", TenureFill.Epochs(_tenures, eventWorkIds, eventWorkEpochs)));
    using (Whizbang.Data.Postgres.CoordinatorOwnershipNotices.WatchFencedWrites(conn, _metrics, _logger, nameof(CompletePerspectiveAsync))) {
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    TenureFill.Forget(_tenures, eventWorkIds);
    // #720: the hot call queued its doorbells instead of notifying inside its transaction; ring them now,
    // after the commit, on their own autocommit so no notifying commit waits on another.
    await DoorbellRinger.RingAsync(conn, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
  }

  /// <inheritdoc />
  public async Task FlushCompletionsAsync(
    FlushCompletionsRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "flush_completions");

    Guid[] outboxIds = request.OutboxIds switch { null or { Count: 0 } => [], Guid[] arr => arr, var ids => [.. ids] };
    Guid[] perspIds = request.PerspectiveEventWorkIds switch { null or { Count: 0 } => [], Guid[] parr => parr, var ids => [.. ids] };

    // #2556: each cursor, failure and id names the epoch its work was claimed under.
    var cursorsJson = request.PerspectiveCursors is null || request.PerspectiveCursors.Count == 0
      ? "[]"
      : _serializePerspectiveCompletions([.. TenureFill.Cursors(_tenures, request.PerspectiveCursors)]);

    IReadOnlyList<CategoryFailures>? failures = request.FailuresByCategory?
      .Select(g => g with { Items = TenureFill.Failures(_tenures, g.Items) }).ToList();
    var failuresJson = _buildFailuresByCategoryJson(failures);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_outbox, @p_cursors::jsonb, @p_persp, @p_fail::jsonb, @p_outbox_epochs, @p_persp_epochs)";
    cmd.Parameters.Add(new NpgsqlParameter("p_outbox", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = outboxIds });
    cmd.Parameters.Add(new NpgsqlParameter("p_cursors", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = cursorsJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_persp", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = perspIds });
    cmd.Parameters.Add(new NpgsqlParameter("p_fail", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = failuresJson });
    // 570 (#2556): the epoch each id was claimed under; failures and cursors name their own.
    cmd.Parameters.Add(EpochsParameter("p_outbox_epochs", TenureFill.Epochs(_tenures, outboxIds, request.OutboxEpochs)));
    cmd.Parameters.Add(EpochsParameter("p_persp_epochs", TenureFill.Epochs(_tenures, perspIds, request.PerspectiveEventWorkEpochs)));
    using (Whizbang.Data.Postgres.CoordinatorOwnershipNotices.WatchFencedWrites(conn, _metrics, _logger, nameof(FlushCompletionsAsync))) {
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    TenureFill.Forget(_tenures, outboxIds.Concat(perspIds).Concat(TenureFill.WorkOf(failures)));
    // #720: ring the doorbells the flush queued, after its commit.
    await DoorbellRinger.RingAsync(conn, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "resolve_sync_inquiries");

    var inquiriesJson = BuildInquiriesJson(inquiries);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
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

  /// <inheritdoc />
  /// <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveAppliedLedgerSqlTests.cs</tests>
  public async ValueTask<Whizbang.Core.Perspectives.Sync.AppliedEventStatus?> GetAppliedEventStatusAsync(
    Whizbang.Core.Perspectives.Sync.AppliedEventInquiry inquiry, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(inquiry);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "wh_perspective_applied_status");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT event_id, state FROM {functionName}(@p_name, @p_event, @p_stream, @p_version)";
    cmd.Parameters.Add(new NpgsqlParameter("p_name", NpgsqlTypes.NpgsqlDbType.Text) { Value = inquiry.PerspectiveName });
    cmd.Parameters.Add(new NpgsqlParameter("p_event", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)inquiry.EventId ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_stream", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)inquiry.StreamId ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_version", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)inquiry.StreamPosition ?? DBNull.Value });

    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    await reader.ReadAsync(cancellationToken);
    return new Whizbang.Core.Perspectives.Sync.AppliedEventStatus(
      (Whizbang.Core.Perspectives.Sync.AppliedEventState)reader.GetInt16(1),
      await reader.IsDBNullAsync(0, cancellationToken) ? null : reader.GetGuid(0));
  }

  /// <summary>
  /// Writes the sync inquiries as the JSON array the batch status function reads.
  /// </summary>
  /// <param name="inquiries">The inquiries to send.</param>
  /// <returns>The JSON array.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InquiriesJsonTests.cs</tests>
#pragma warning disable RCS1158 // Static helper shared by every generic instantiation; it does not depend on TDbContext.
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Writes the inquiry array by hand, and a hand-written JSON array needs a separator decision at every element of every nested array. The loops and their index tests are the format.")]
  internal static string BuildInquiriesJson(IReadOnlyList<Whizbang.Core.Perspectives.Sync.SyncInquiry> inquiries) {
    var sb = new System.Text.StringBuilder("[");
    for (var i = 0; i < inquiries.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      var inq = inquiries[i];
      sb.Append("{\"InquiryId\":\"").Append(inq.InquiryId).Append("\",")
        .Append("\"StreamId\":\"").Append(inq.StreamId).Append("\",")
        .Append("\"PerspectiveName\":\"").Append(_jsonEscape(inq.PerspectiveName)).Append("\",")
        .Append("\"DiscoverPendingFromOutbox\":").Append(_jsonBool(inq.DiscoverPendingFromOutbox)).Append(',')
        .Append("\"IncludePendingEventIds\":").Append(_jsonBool(inq.IncludePendingEventIds)).Append(',')
        .Append("\"IncludeProcessedEventIds\":").Append(_jsonBool(inq.IncludeProcessedEventIds));
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
#pragma warning restore RCS1158

  /// <inheritdoc />
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Reads the claim's result set column by column into a work batch and then, when outstanding counts were requested, the second result set as well. The column reads dominate the count.")]
  public async Task<WorkBatch> ClaimWorkAsync(
    ClaimWorkRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "claim_work");
    var outstandingFn = BuildSchemaQualifiedName(schema, "count_outstanding_work");
    // 570 (#2556): each row carries the epoch this instance holds its partition at, read in the claim's own statement
    // (ClaimedTenures.ProjectEpochs).

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = Whizbang.Data.Postgres.ClaimedTenures.ProjectEpochs(
      Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema),
      "c.source, c.work_id, c.work_stream_id, c.partition_number, c.destination, c.message_type, c.envelope_type, "
      + "c.message_data, c.metadata, c.status, c.attempts, c.is_newly_stored, c.is_orphaned, c.perspective_name, c.priority, "
      + "c.received_at",
      $"{functionName}(@p_id, @p_svc, @p_host, @p_pid, @p_max, @p_part, @p_lease, @p_fresh, @p_rows, @p_steal, @p_persp, @p_idle_settled, @p_idle_trickle_after, @p_idle_trickle_slice, @p_idle_force_after, @p_outbox_rows, @p_outbox_run, @p_assignment_epoch, @p_assignment_revision, @p_alive_lock_held, @p_lifecycle_phase, @p_acquire_inbox)",
      "@p_id", "@p_part");
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
    cmd.Parameters.Add(new NpgsqlParameter(P_MAX, NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.MaxStreams });
    cmd.Parameters.Add(new NpgsqlParameter("p_part", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.PartitionCount });
    cmd.Parameters.Add(new NpgsqlParameter("p_lease", NpgsqlTypes.NpgsqlDbType.Integer) { Value = request.LeaseSeconds });
    cmd.Parameters.Add(new NpgsqlParameter("p_fresh", NpgsqlTypes.NpgsqlDbType.Double) { Value = request.FreshWorkShare });
    // 145: acquisition has its own ROW bound and a steal flag; null rows means "bounded by the stream count".
    cmd.Parameters.Add(new NpgsqlParameter("p_rows", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)request.MaxAcquireRows ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_steal", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = request.AllowSteal });
    cmd.Parameters.Add(new NpgsqlParameter("p_persp", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)request.MaxPerspectiveStreams ?? DBNull.Value });
    // 167: the idle band. Null here means "the store's documented default", so the bounds are
    // coalesced to the SAME values IdleBandOptions declares rather than passed as NULL -- a NULL
    // interval compares as unknown, which would leave the band withheld for ever instead of
    // draining on the schedule the defaults promise.
    cmd.Parameters.Add(new NpgsqlParameter("p_idle_settled", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = request.IdleSettled });
    cmd.Parameters.Add(new NpgsqlParameter("p_idle_trickle_after", NpgsqlTypes.NpgsqlDbType.Interval) {
      Value = request.IdleTrickleAfter ?? IdleBandOptions.DEFAULT_TRICKLE_AFTER
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_idle_trickle_slice", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = request.IdleTrickleSlice ?? IdleBandOptions.DEFAULT_TRICKLE_SLICE
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_idle_force_after", NpgsqlTypes.NpgsqlDbType.Interval) {
      Value = request.IdleForceAfter ?? IdleBandOptions.DEFAULT_FORCE_FULL_DRAIN_AFTER
    });
    // 171 (#917): the outbox's own acquisition row bound and its per-stream run. Null keeps the store's
    // previous behavior (bounded by p_max, one row per chosen head).
    cmd.Parameters.Add(new NpgsqlParameter("p_outbox_rows", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)request.MaxOutboxAcquireRows ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_outbox_run", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)request.OutboxRunLength ?? DBNull.Value });
    // 203 (#1254): the cached partition assignment's version, fenced in the claim. Null ranks as before.
    cmd.Parameters.Add(new NpgsqlParameter("p_assignment_epoch", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)request.PartitionAssignment?.Epoch ?? DBNull.Value
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_assignment_revision", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)request.PartitionAssignment?.Revision ?? DBNull.Value
    });
    // 204 (#1286): a direct caller holding its alive-lock is not reported stale between slow beats.
    cmd.Parameters.Add(new NpgsqlParameter("p_alive_lock_held", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = request.AliveLockHeld });
    // 229 (#1638): a row recording another phase than the caller's is reported stale; null judges it by liveness alone.
    cmd.Parameters.Add(new NpgsqlParameter("p_lifecycle_phase", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)request.LifecyclePhase ?? DBNull.Value
    });
    // 390 (#1853): no new inbox rows while the caller's inbox working set is at its byte budget.
    cmd.Parameters.Add(new NpgsqlParameter("p_acquire_inbox", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = request.AcquireInbox });

    var rows = new List<WorkBatchRow>();
    OutstandingWork? outstanding = null;
    // 171: the claim says in-band, as a notice on this connection, when its outbox acquisition filled
    // its bound. Listened for only for the length of this command.
    var outboxAcquisitionFull = false;
    // 196 (#1226): the claim no longer writes this instance's registration row; it says when the row
    // is missing or stale, and the caller registers after the claim.
    var registrationStale = false;
    // 203 (#1254): the cached partition assignment was superseded or expired; the claim ranked itself.
    var assignmentStale = false;
    // #2554: in map mode the claim found no valid partition map, so it claimed nothing and the instance waits.
    var mapUnavailable = false;
    void OnClaimNotice(object? sender, NpgsqlNoticeEventArgs e) {
      mapUnavailable |= string.Equals(e.Notice.MessageText, Whizbang.Data.Postgres.CoordinatorOwnershipNotices.PARTITION_MAP_UNAVAILABLE, StringComparison.Ordinal);
      outboxAcquisitionFull |= string.Equals(e.Notice.MessageText, OutboxClaimNotices.ACQUISITION_FULL, StringComparison.Ordinal);
      registrationStale |= string.Equals(e.Notice.MessageText, OutboxClaimNotices.REGISTRATION_STALE, StringComparison.Ordinal);
      assignmentStale |= string.Equals(e.Notice.MessageText, OutboxClaimNotices.PARTITION_ASSIGNMENT_STALE, StringComparison.Ordinal);
    }
    conn.Notice += OnClaimNotice;
    // #2611: a stale queue table the claim's statistics refresh could not analyze is counted and warned, once per back-off.
    var statisticsSkips = Whizbang.Data.Postgres.QueueStatisticsRefreshNotices.Watch(conn, _metrics, _logger);
    try {
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        rows.Add(new WorkBatchRow {
          Source = reader.GetString(0),
          WorkId = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetGuid(1),
          StreamId = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetGuid(2),
          PartitionNumber = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt32(3),
          // claim_work (migration 390) returns a NULL literal for destination, message type, envelope type, message
          // data, metadata and perspective name in every branch: it returns identity only, and the fetch functions return
          // the rows. Status and attempts are NOT NULL (or 0) in every branch, and the two flags are a false literal.
          Destination = null,
          MessageType = null,
          EnvelopeType = null,
          MessageData = null,
          Metadata = null,
          Status = reader.GetInt32(9),
          Attempts = reader.GetInt32(10),
          IsNewlyStored = reader.GetBoolean(11),
          IsOrphaned = reader.GetBoolean(12),
          PerspectiveName = null,
          // 150: the inbox row's priority and arrival, folded per stream below for the batch hooks.
          Priority = await reader.IsDBNullAsync(14, cancellationToken) ? null : reader.GetInt32(14),
          ReceivedAt = await reader.IsDBNullAsync(15, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(15, cancellationToken),
          // 570 (#2556): null unless the service owns by map.
          PartitionEpoch = await reader.IsDBNullAsync(16, cancellationToken) ? null : reader.GetInt64(16),
          TenurePartition = await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetInt32(17)
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
    } finally {
      conn.Notice -= OnClaimNotice;
      statisticsSkips.Dispose();
    }
    // #2554: a claim that waited for a map is counted, and warned once per streak; one that had a map ends the streak.
    Whizbang.Data.Postgres.CoordinatorOwnershipNotices.ReportPartitionMap(mapUnavailable, _metrics, _logger);
    if (rows.Count > 0) {
      // #720: a claim that leased or re-emitted work may have queued ownership doorbells; ring them
      // after its commit. An empty claim queued nothing and skips the round trip.
      await DoorbellRinger.RingAsync(conn, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
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

    return TenureFill.RecordClaim(_tenures, new WorkBatch {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = [],
      PerspectiveStreamIds = perspectiveStreamIds,
      OutboxStreamIds = outboxStreamIds,
      InboxStreamIds = inboxStreamIds,
      InboxStreams = ClaimedInboxStreamFolder.Fold(rows),
      Outstanding = outstanding,
      OutboxAcquisitionFull = outboxAcquisitionFull,
      InstanceRegistrationStale = registrationStale,
      PartitionAssignmentStale = assignmentStale,
      PartitionMapUnavailable = mapUnavailable,
      StreamEpochs = ClaimedTenures.Epochs(rows, r => r.StreamId, r => r.PartitionEpoch),
      StreamPartitions = ClaimedTenures.Partitions(rows, r => r.StreamId, r => r.PartitionEpoch is null ? null : r.TenurePartition)
    });
  }

  /// <inheritdoc />
  public async Task CommitHandlerResultAsync(
    HandlerCommitRequest request,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "commit_handler_result");

    var payload = _buildHandlerCommitPayload(request);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_request::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_request", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = payload });
    // 570 (#2556): a commit under an epoch that moved on commits nothing; it is counted and logged, never silent.
    using (Whizbang.Data.Postgres.CoordinatorOwnershipNotices.WatchFencedWrites(conn, _metrics, _logger, nameof(CommitHandlerResultAsync))) {
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    // #720: ring the doorbells the commit queued, after the commit.
    await DoorbellRinger.RingAsync(conn, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
  }

#pragma warning disable S3776 // one commit path per outcome; splitting it would separate the rows from the failure that explains them
  /// <inheritdoc />
  public async Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(
    IReadOnlyList<HandlerCommitRequest> requests,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(requests);
    if (requests.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
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
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT handler_id, success, error_message, tier, bulk_error FROM {functionName}(@p_results::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_results", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = batchJson });

    var results = new List<HandlerBatchResult>(requests.Count);
    var fellBack = false;
    string? bulkError = null;
    using (Whizbang.Data.Postgres.CoordinatorOwnershipNotices.WatchFencedWrites(conn, _metrics, _logger, nameof(CommitHandlerBatchAsync)))
    await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken)) {
      while (await reader.ReadAsync(cancellationToken)) {
        results.Add(new HandlerBatchResult(
          HandlerId: reader.GetGuid(0),
          Success: reader.GetBoolean(1),
          ErrorMessage: await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
          // 570 (#2556): tier 0 is the fenced outcome: nothing committed, and the message stays for the current owner.
          Fenced: reader.GetInt32(3) == FENCED_TIER));
        if (!fellBack && reader.GetInt32(3) == 2) {
          fellBack = true;
          bulkError = await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4);
        }
      }
    }
    // #720: ring the doorbells the commits queued, after the commits and with the reader closed.
    await DoorbellRinger.RingAsync(conn, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
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
#pragma warning restore S3776

  private string _buildHandlerCommitPayload(HandlerCommitRequest request) {
    // #2556: the commit names its inbox message's claimed epoch, unless the caller named one.
    request = TenureFill.Handler(_tenures, request);
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
    sb.Append(",\"debug_mode\":").Append(_jsonBool(request.DebugMode));
    // 570 (#2556): the epoch the message was claimed under; a commit under one that moved on commits nothing.
    if (request.PartitionEpoch is { } partitionEpoch) {
      sb.Append(",\"partition_epoch\":").Append(partitionEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
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

  /// <summary>
  /// The array a Postgres array parameter binds: the caller's own array when it passed one, otherwise
  /// a copy. Every id and name list the coordinator sends goes through here.
  /// </summary>
  /// <typeparam name="T">The element type.</typeparam>
  /// <param name="items">The caller's collection.</param>
  /// <returns>An array holding <paramref name="items"/>.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorParameterBindingTests.cs</tests>
#pragma warning disable RCS1158 // Static helper shared by every generic instantiation; it does not depend on TDbContext.
  internal static T[] AsArray<T>(IEnumerable<T> items) => items as T[] ?? [.. items];

  /// <summary>The tier <c>commit_handler_batch</c> returns for a fenced item (570, #2556).</summary>
  internal const int FENCED_TIER = 0;

  /// <summary>
  /// The bigint[] parameter carrying the partition epochs a write was made under (570, #2556): NULL when the caller names
  /// none, so nothing is fenced.
  /// </summary>
  /// <param name="name">The parameter name.</param>
  /// <param name="epochs">The epochs by position, or null.</param>
  /// <returns>The parameter.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AFencedWrite_IsCountedByKind_AndLoggedAsync</tests>
  internal static NpgsqlParameter EpochsParameter(string name, IReadOnlyList<long?>? epochs) =>
    new(name, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = epochs is null ? DBNull.Value : AsArray(epochs)
    };
#pragma warning restore RCS1158

  // One JSON literal per bool, so every hand-written payload spells its flags the same way and the
  // choice is made (and tested) once.
  private static string _jsonBool(bool value) => value ? JSON_TRUE : JSON_FALSE;

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

    var schema = _resolveSchema(typeof(OutboxRecord));
    // 572 (#2556): report_failures keeps its signature; the fenced report is its own function, which leaves out a failure
    // whose epoch has moved on and hands the rest to report_failures. Each failure names its work's claimed epoch.
    var functionName = BuildSchemaQualifiedName(schema, "wh_report_failures_fenced");

    var failuresJson = _serializeFailures([.. TenureFill.Failures(_tenures, failures)]);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_category, @p_failures::jsonb)";
    cmd.Parameters.Add(new NpgsqlParameter("p_category", NpgsqlTypes.NpgsqlDbType.Text) { Value = category.ToSqlCategory() });
    cmd.Parameters.Add(new NpgsqlParameter("p_failures", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = failuresJson });
    using (Whizbang.Data.Postgres.CoordinatorOwnershipNotices.WatchFencedWrites(conn, _metrics, _logger, nameof(ReportFailuresAsync))) {
      _ = await cmd.ExecuteScalarAsync(cancellationToken);
    }
    // Failed work is no longer in hand; perspective work no longer holds its stream's writes (#2557).
    TenureFill.Forget(_tenures, failures.Select(f => f.MessageId));
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreFlusherMethodsTests.cs:RenewHeldLeasesAsync_ARowAnotherInstanceHolds_IsReportedAndLeftAloneAsync</tests>
  public async Task<IReadOnlyList<Guid>> RenewHeldLeasesAsync(
    WorkCategory category,
    Guid instanceId,
    IReadOnlyList<Guid> ids,
    int leaseSeconds = 300,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(ids);
    if (ids.Count == 0) {
      return [];
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "wh_renew_held_leases");

    var idArray = AsArray(ids);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_category, @p_instance_id, @p_ids, @p_lease)";
    cmd.Parameters.Add(new NpgsqlParameter("p_category", NpgsqlTypes.NpgsqlDbType.Text) { Value = category.ToSqlCategory() });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = instanceId });
    cmd.Parameters.Add(new NpgsqlParameter(P_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_lease", NpgsqlTypes.NpgsqlDbType.Integer) { Value = leaseSeconds });
    // The function returns an empty array, never NULL, so the result is always a uuid[].
    return (Guid[])(await cmd.ExecuteScalarAsync(cancellationToken))!;
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorFleetSql.ReleaseUnprocessedInboxAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), instanceId, messageIds, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Calls <c>release_unstarted_leases</c> (migration 145). Two empty lists cost no round trip.
  /// </remarks>
  /// <docs>fundamentals/work-coordinator/claim-loop</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BoundedAcquisitionRewriteSqlTests.cs</tests>
  public async Task<UnstartedLeaseRelease> ReleaseUnstartedLeasesAsync(
    Guid instanceId,
    IReadOnlyList<Guid> inboxStreamIds,
    IReadOnlyList<Guid> perspectiveStreamIds,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(inboxStreamIds);
    ArgumentNullException.ThrowIfNull(perspectiveStreamIds);
    if (inboxStreamIds.Count == 0 && perspectiveStreamIds.Count == 0) {
      return new UnstartedLeaseRelease(0, 0);
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    // release_unstarted_leases RETURNS TABLE and always emits exactly one row, so a missing row would be a broken
    // function and surfaces as the read's own exception rather than as a silent zero.
    return await CoordinatorFleetSql.ReleaseUnstartedLeasesAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), instanceId, inboxStreamIds, perspectiveStreamIds, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Stream-integrity R1a selection (see <see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/>).
  /// Joins the event body so reaped ephemeral events are excluded structurally, filters
  /// at-most-once occurrences by their <see cref="EventFlags.AtMostOnce"/> flag, and returns rows
  /// ordered (stream, version) under a hard LIMIT.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  /// <docs>data/event-store#what-leaves-the-database</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SelectRedeliveryEventsTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorExportSqlTests.cs:Redelivery_APackedEvent_IsSelectedInTheWireForm_WhichAPlainReaderReadsAsync</tests>
  public async Task<IReadOnlyList<RedeliveryEvent>> SelectRedeliveryEventsAsync(
    RedeliveryRequest request,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));

    // Every filter but the type, shared by both orders. #2032: only events the answering service authored (no origin,
    // and a first hop naming it or no service). #2626: the body in the wire form, its packed vectors written back as
    // JSON numbers: the selection is sent to a peer (integrity repair) and re-recorded by the audit rebuild, both over
    // the transport, so a packed vector never leaves.
    var columns = $"""
      es.event_id, es.stream_id, es.version::bigint AS version, es.commit_sequence,
             es.event_type, {schema}.wh_event_body_wire_form(es.event_type, eb.event_data)::text AS event_data,
             eb.metadata::text AS metadata, es.scope::text AS scope, COALESCE(es.flags, 0) AS flags
      """;
    var filters = $"""
      (@p_tenant IS NULL OR es.scope->>'t' = @p_tenant)
        AND ((@p_streams::uuid[]) IS NULL OR es.stream_id = ANY(@p_streams::uuid[]))
        AND ((@p_event_ids::uuid[]) IS NULL OR es.event_id = ANY(@p_event_ids::uuid[]))
        AND (@p_to_seq::bigint IS NULL OR es.commit_sequence <= @p_to_seq)
        -- not the digest rule: an at-most-once event is never redelivered, whatever counts it
        AND COALESCE(es.flags, 0) & {AT_MOST_ONCE_FLAG} = 0
        AND (@p_authored_by::text IS NULL OR (
          es.origin_service_id IS NULL
          AND COALESCE(NULLIF(lower(btrim(COALESCE(
                (eb.metadata -> 'Hops') -> 0 -> 'si' ->> 'sn',
                (eb.metadata -> 'Hops') -> 0 -> 'ServiceInstance' ->> 'ServiceName'))), ''), 'unknown')
              IN ('unknown', lower(btrim(@p_authored_by::text)))))
      """;
    var normalize = BuildSchemaQualifiedName(schema, NORMALIZE_EVENT_TYPE_FUNCTION);
    string sql;
    if (request.OrderByCommitSequence) {
      // #2092: one type's stamped history in commit order from the floor. Each type is read through
      // idx_event_store_type_commit_sequence in order under the limit (a lateral per type), so a window costs its own
      // rows however much of the type's history lies behind or ahead of it. That index is built concurrently after the
      // migrations (ConcurrentIndexBuild); until it is valid the same read answers correctly, more slowly.
      if (request.EventTypes is not { Count: > 0 }) {
        throw new ArgumentException("A commit-sequence selection names its event types.", nameof(request));
      }
      sql = $"""
        SELECT w.event_id, w.stream_id, w.version, w.commit_sequence, w.event_type, w.event_data, w.metadata, w.scope, w.flags
        FROM (SELECT DISTINCT {normalize}(t) AS name FROM unnest(@p_types::text[]) AS t) AS types
        CROSS JOIN LATERAL (
          SELECT {columns}
          FROM {schema}.wh_event_store es
          JOIN {schema}.wh_event_body eb ON eb.event_id = es.event_id
          WHERE es.event_type = types.name
            AND es.commit_sequence IS NOT NULL
            AND es.commit_sequence > COALESCE(@p_from_seq::bigint, -1)
            AND {filters}
          ORDER BY es.commit_sequence
          LIMIT @p_max) AS w
        ORDER BY w.commit_sequence
        LIMIT @p_max
        """;
    } else {
      sql = $"""
        SELECT {columns}
        FROM {schema}.wh_event_store es
        JOIN {schema}.wh_event_body eb ON eb.event_id = es.event_id
        WHERE ((@p_types::text[]) IS NULL OR es.event_type IN (SELECT {normalize}(t) FROM unnest(@p_types::text[]) AS t))
          AND (@p_from_seq::bigint IS NULL OR es.commit_sequence > @p_from_seq)
          AND ((@p_after_stream::uuid) IS NULL OR (es.stream_id, es.version) > (@p_after_stream::uuid, @p_after_version::bigint))
          AND {filters}
        ORDER BY es.stream_id, es.version
        LIMIT @p_max
        """;
    }

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = sql;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_tenant", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)request.TenantScope ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = request.EventTypes is null ? DBNull.Value : request.EventTypes.ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAMS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = request.StreamIds is null ? DBNull.Value : request.StreamIds.ToArray()
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_EVENT_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = request.EventIds is null ? DBNull.Value : request.EventIds.ToArray()
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
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_MAX, NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = request.MaxEvents
    });
    // #2032: only events the answering service authored (no origin, and a first hop naming it or no service).
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_authored_by", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)request.AuthoredBy ?? DBNull.Value
    });

    var results = new List<RedeliveryEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new RedeliveryEvent {
        EventId = reader.GetGuid(0),
        StreamId = reader.GetGuid(1),
        Version = reader.GetInt64(2),
        CommitSequence = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetInt64(3),
        EventType = reader.GetString(4),
        EventData = reader.GetString(5),
        // Both queries inner-join wh_event_body, whose metadata is NOT NULL.
        Metadata = reader.GetString(6),
        Scope = await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7),
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
      CoordinatorCatalogSql.TYPE_DEFINITION_RECONCILE_KEY, CoordinatorCatalogSql.TYPE_DEFINITION_RECONCILE_DESCRIPTION, claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task RecordOffloadClaimAsync(
      string storageKey, string providerName, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      await CoordinatorRetentionSql.RecordOffloadClaimAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), storageKey, providerName, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<OffloadClaimRecord>> GetExpiredOffloadClaimsAsync(
      TimeSpan olderThan, int batchSize, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) => CoordinatorRetentionSql.ExpiredOffloadClaimsAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), olderThan, batchSize, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task RemoveOffloadClaimsAsync(
      IReadOnlyCollection<string> storageKeys, CancellationToken cancellationToken = default) {
    if (storageKeys.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      await CoordinatorRetentionSql.RemoveOffloadClaimsAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), storageKeys, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<bool> TryClaimOffloadSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      CoordinatorRetentionSql.OFFLOAD_SWEEP_KEY, CoordinatorRetentionSql.OFFLOAD_SWEEP_DESCRIPTION, claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
      IReadOnlyCollection<string> clrTypeNames, int perTableLimit = 500, CancellationToken cancellationToken = default) {
    if (clrTypeNames.Count == 0) {
      return Task.FromResult<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>([]);
    }
    return _withCoordinatorCommandAsync((cmd, schema) =>
      PerspectiveRowRetentionSql.CollectReapTargetsAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), clrTypeNames, perTableLimit, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task HoldPerspectiveRowDestructionAsync(
      IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
      DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      await PerspectiveRowRetentionSql.HoldAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), rows, holdUntil, cancellationToken).ConfigureAwait(false);
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
      await PerspectiveRowRetentionSql.ReleaseHoldsAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), rows, cancellationToken).ConfigureAwait(false);
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
    return _withCoordinatorCommandAsync((cmd, schema) => PerspectiveRowRetentionSql.RecordDestructionFailureAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), rows, retryBackoff, maxRetries, onDestroyFailure, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(
      int batchSize = 5000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) =>
      PerspectiveRowRetentionSql.ReapAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), PerspectiveRowRetentionSql.EXPIRY_SWEEP, batchSize, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapPerspectiveRowCapsAsync(
      int batchSize = 5000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) =>
      PerspectiveRowRetentionSql.ReapAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), PerspectiveRowRetentionSql.CAP_SWEEP, batchSize, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimRowCapSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      PerspectiveRowRetentionSql.ROW_CAP_SWEEP_KEY,
      PerspectiveRowRetentionSql.ROW_CAP_SWEEP_DESCRIPTION,
      claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task AcknowledgeRetentionEnforcementAsync(
      string clrTypeName, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
      await PerspectiveRowRetentionSql.AcknowledgeAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), clrTypeName, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRetentionAdoption>> AdoptEnrolledPerspectiveRetentionAsync(
      CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) => PerspectiveRowRetentionSql.AdoptAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<long> CountPerspectiveRetentionBacklogAsync(
      string clrTypeName, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) =>
      PerspectiveRowRetentionSql.CountBacklogAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), clrTypeName, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowRef>> DrainRowEvictionJournalAsync(
      int limit = 1000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) => PerspectiveRowRetentionSql.DrainJournalAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), limit, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task RequeueRowEvictionsAsync(
      IReadOnlyCollection<Whizbang.Core.Lifecycle.PerspectiveRowRef> rows,
      CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
      await PerspectiveRowRetentionSql.RequeueAsync(cmd, PgIdentifier.QualifyPrefix(schema), rows, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
      IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken = default) {
    if (clrTypeNames.Count == 0) {
      return Task.FromResult<IReadOnlyList<PerspectiveTableName>>([]);
    }
    return _withCoordinatorCommandAsync((cmd, schema) =>
      PerspectiveRowRetentionSql.TableNamesAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), clrTypeNames, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>> GetPerspectiveRowsByIdsAsync(
      string clrTypeName, string tableName, IReadOnlyCollection<Guid> rowIds,
      CancellationToken cancellationToken = default) {
    if (rowIds.Count == 0) {
      return Task.FromResult<IReadOnlyList<Whizbang.Core.Lifecycle.PerspectiveRowDestructionTarget>>([]);
    }
    return _withCoordinatorCommandAsync((cmd, schema) =>
      PerspectiveRowRetentionSql.RowsByIdsAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), clrTypeName, tableName, rowIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> CascadeDeletePerspectiveRowsAsync(
      string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default) {
    if (rowIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withCoordinatorCommandAsync((cmd, schema) =>
      PerspectiveRowRetentionSql.CascadeDeleteAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), tableName, rowIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> FoldSettledApplyPathsAsync(
      TimeSpan idleWindow, int limit = 1000, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) => CoordinatorRetentionSql.FoldSettledApplyPathsAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), idleWindow, limit, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimSettledFoldSweepAsync(
    TimeSpan claimWindow,
    CancellationToken cancellationToken = default) =>
    _tryClaimWatermarkAsync(
      CoordinatorRetentionSql.SETTLED_FOLD_KEY, CoordinatorRetentionSql.SETTLED_FOLD_DESCRIPTION, claimWindow, cancellationToken);

  /// <inheritdoc />
  public Task<int> FoldStreamApplyPathsAsync(
      IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) {
    if (streamIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withCoordinatorCommandAsync((cmd, schema) =>
      StreamPrefixTruncateSql.FoldStreamApplyPathsAsync(cmd, PgIdentifier.QualifyPrefix(schema), streamIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> ReconcileFollowerPresenceAsync(
      string followerTable, IReadOnlyCollection<string> announcerTables,
      CancellationToken cancellationToken = default) =>
    ReconcileFollowerPresenceAsync(followerTable, announcerTables, streamIds: null, cancellationToken);

  /// <inheritdoc />
  public Task<int> ReconcileFollowerPresenceAsync(
      string followerTable, IReadOnlyCollection<string> announcerTables, IReadOnlyCollection<Guid>? streamIds,
      CancellationToken cancellationToken = default) {
    if (announcerTables.Count == 0 || streamIds is { Count: 0 }) {
      return Task.FromResult(0);
    }
    return _withCoordinatorCommandAsync((cmd, schema) => PerspectiveRowRetentionSql.ReconcileFollowerPresenceAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), followerTable, announcerTables, streamIds, cancellationToken), cancellationToken);
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

    var schema = _resolveSchema(typeof(OutboxRecord));

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await PerspectiveRowRetentionSql.TryClaimWatermarkAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), settingKey, description, claimWindow, cancellationToken)
      .ConfigureAwait(false);
  }


  /// <inheritdoc />
  public async Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    const string WATERMARK_KEY = "integrity_checkpoint_watermark";
    const string HISTORY_FLOOR_KEY = "integrity_checkpoint_history_floor";
    const string HISTORY_FLOOR_DESCRIPTION =
      "Stream-integrity checkpoint history floor (highest insert_order written before counting began; rows at or below it are verified by digest, not counted in windows)";
    var settings = BuildSchemaQualifiedName(schema, SETTINGS_TABLE);

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;

    // Head watermark = the highest STAMPED sequence (the async stamper's barrier guarantees
    // everything at/below it is committed and stable), plus the previously advanced watermark, the
    // history floor, and the last insertion-order value handed out (the floor a baseline takes).
    long current;
    long? prior;
    long? floor;
    long insertOrderHead;
    await using (var read = conn.CreateCoordinatorCommand()) {
      read.CommandText =
        $"SELECT COALESCE((SELECT MAX(commit_sequence) FROM {schema}.wh_event_store), 0), " +
        $"       (SELECT setting_value FROM {settings} WHERE setting_key = @p_key), " +
        $"       (SELECT setting_value FROM {settings} WHERE setting_key = @p_floor_key), " +
        $"       (SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END FROM {schema}.wh_event_insert_order_seq)";
      read.Parameters.Add(new Npgsql.NpgsqlParameter(P_KEY, WATERMARK_KEY));
      read.Parameters.Add(new Npgsql.NpgsqlParameter(P_FLOOR_KEY, HISTORY_FLOOR_KEY));
      await using var reader = await read.ExecuteReaderAsync(cancellationToken);
      await reader.ReadAsync(cancellationToken);
      current = reader.GetInt64(0);
      prior = await _readSettingInt64Async(reader, 1, cancellationToken);
      floor = await _readSettingInt64Async(reader, 2, cancellationToken);
      insertOrderHead = reader.GetInt64(3);
    }

    if (prior is null) {
      // First run: BASELINE at the current head without counting history — a fresh consumer set
      // has nothing to compare retroactive counts against, and a startup count storm helps no one.
      // The baseline also takes the history floor (#1791): rows written before it that are still
      // unstamped get sequences ABOVE the baseline later, and no consumer can hold them under those
      // sequences, so the windows leave them to the digests. Both rows are written by one statement,
      // and only by the instance whose baseline wins. The command's scope closes before the return:
      // returning from inside an `await using` puts that scope's end sequence point on cleanup
      // nothing reaches.
      long inserted;
      await using (var init = conn.CreateCoordinatorCommand()) {
        init.CommandText = $"""
          WITH baseline AS (
            INSERT INTO {settings} (setting_key, setting_value, value_type, description)
            VALUES (@p_key, @p_value, 'integer', 'Stream-integrity checkpoint watermark (highest commit_sequence already checkpointed)')
            ON CONFLICT DO NOTHING
            RETURNING 1
          ), history_floor AS (
            INSERT INTO {settings} (setting_key, setting_value, value_type, description)
            SELECT @p_floor_key, @p_floor, 'integer', @p_floor_description FROM baseline
            ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
            RETURNING 1
          )
          SELECT COUNT(*) FROM baseline
          """;
        init.Parameters.Add(new Npgsql.NpgsqlParameter(P_KEY, WATERMARK_KEY));
        init.Parameters.Add(new Npgsql.NpgsqlParameter("p_value", current.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _addHistoryFloorParams(init, HISTORY_FLOOR_KEY, HISTORY_FLOOR_DESCRIPTION, insertOrderHead);
        inserted = (long)(await init.ExecuteScalarAsync(cancellationToken))!;
      }

      return inserted == 1
        ? new IntegrityCheckpointWindow { FromCommitSequence = current, ToCommitSequence = current }
        : null;   // another instance baselined first — it owns this window
    }

    if (floor is null) {
      // A store an earlier version baselined has a watermark and no floor, and may still be stamping the
      // history it baselined over. The first advance to find it missing takes the floor here; whatever
      // value is stored first is the one every instance counts against.
      await using var take = conn.CreateCoordinatorCommand();
      take.CommandText = $"""
        INSERT INTO {settings} AS s (setting_key, setting_value, value_type, description)
        VALUES (@p_floor_key, @p_floor, 'integer', @p_floor_description)
        ON CONFLICT (setting_key) DO UPDATE SET setting_value = s.setting_value
        RETURNING s.setting_value
        """;
      _addHistoryFloorParams(take, HISTORY_FLOOR_KEY, HISTORY_FLOOR_DESCRIPTION, insertOrderHead);
      floor = long.Parse((string)(await take.ExecuteScalarAsync(cancellationToken))!, System.Globalization.CultureInfo.InvariantCulture);
    }

    if (current < prior.Value) {
      current = prior.Value;   // the watermark never regresses
    }

    // Optimistic advance: exactly one instance wins each window; losers skip the cycle. An
    // unchanged watermark (quiet window) still "wins" — the empty checkpoint is the liveness beat.
    await using (var cas = conn.CreateCoordinatorCommand()) {
      cas.CommandText =
        $"UPDATE {settings} SET setting_value = @p_new WHERE setting_key = @p_key AND setting_value = @p_old";
      cas.Parameters.Add(new Npgsql.NpgsqlParameter("p_new", current.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      cas.Parameters.Add(new Npgsql.NpgsqlParameter(P_KEY, WATERMARK_KEY));
      cas.Parameters.Add(new Npgsql.NpgsqlParameter("p_old", prior.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      if (await cas.ExecuteNonQueryAsync(cancellationToken) != 1) {
        return null;   // lost the advance race
      }
    }

    var buckets = new List<CheckpointBucket>();
    if (current > prior.Value) {
      // At-most-once events (EventFlags.AtMostOnce) are excluded (non-delivery is their declared
      // behavior, not a gap); checkpoints never count themselves; audit records are left out as every
      // digest leaves them out (#1554), since the origin need not store what a subscriber does. Ephemeral events stay INCLUDED,
      // reaped or not: the consumer received them live and counts them too.
      await using var count = conn.CreateCoordinatorCommand();
      count.CommandText = $"""
        SELECT COALESCE(es.scope->>'t', '') AS tenant, es.event_type, COUNT(*)::int
        FROM {schema}.wh_event_store es
        WHERE es.commit_sequence > @p_from AND es.commit_sequence <= @p_to
          AND es.insert_order > @p_floor
          AND {DigestRuleSql.CountedEs}
          AND es.event_type <> @p_checkpoint_type
        GROUP BY 1, 2
        ORDER BY 1, 2
        """;
      count.Parameters.Add(new Npgsql.NpgsqlParameter(P_FROM, prior.Value));
      count.Parameters.Add(new Npgsql.NpgsqlParameter(P_TO, current));
      count.Parameters.Add(new Npgsql.NpgsqlParameter(P_FLOOR, floor.Value));
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

  /// <summary>Reads a <c>wh_settings</c> value holding an integer, or null when the setting is absent.</summary>
  private static async Task<long?> _readSettingInt64Async(
      System.Data.Common.DbDataReader reader, int ordinal, CancellationToken cancellationToken) =>
    await reader.IsDBNullAsync(ordinal, cancellationToken)
      ? null
      : long.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);

  /// <summary>The parameters both statements that write the checkpoint history floor take.</summary>
  private static void _addHistoryFloorParams(
      System.Data.Common.DbCommand cmd, string key, string description, long insertOrderHead) {
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_FLOOR_KEY, key));
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_FLOOR, insertOrderHead.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_floor_description", description));
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
    Guid originServiceId,
    long fromCommitSequence,
    long toCommitSequence,
    CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    // Received events persist the ORIGIN identity (1:1 forward stamping) — the consumer's half of
    // a checkpoint comparison counts by it, windowed on the ORIGIN's commit sequence. It counts by the origin's rule
    // (__INTEGRITY_COUNTED__, #2352): audit records (#1554) and at-most-once events are left out on both halves.
    cmd.CommandText = $"""
      SELECT COALESCE(es.scope->>'t', '') AS tenant, es.event_type, COUNT(*)::int
      FROM {schema}.wh_event_store es
      WHERE es.origin_service_id = @p_origin
        AND es.origin_commit_sequence > @p_from AND es.origin_commit_sequence <= @p_to
        AND {DigestRuleSql.CountedEs}
      GROUP BY 1, 2
      ORDER BY 1, 2
      """;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = originServiceId });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_FROM, fromCommitSequence));
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TO, toCommitSequence));

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
    var schema = _resolveSchema(typeof(OutboxRecord));

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
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
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
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
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes.ToArray()
      });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_pending", NpgsqlTypes.NpgsqlDbType.Smallint) {
        Value = (short)ConsumedTypeBackfillStatus.Pending
      });
      return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task EnqueueSubscriptionBackfillsAsync(
    IReadOnlyList<SubscriptionBackfillSeed> seeds,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(seeds);
    if (seeds.Count == 0) {
      return;
    }
    await _withCoordinatorCommandAsync(async (cmd, schema) => {
      await Whizbang.Data.Postgres.SubscriptionBackfillSql.EnqueueAsync(
        cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), seeds, cancellationToken).ConfigureAwait(false);
      return 0;
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<SubscriptionBackfillCursor>> GetSubscriptionBackfillsAsync(CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync((cmd, schema) => Whizbang.Data.Postgres.SubscriptionBackfillSql.ReadAllAsync(
      cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), cancellationToken), cancellationToken);

  /// <inheritdoc />
  public async Task MarkSubscriptionBackfillWindowRequestedAsync(
    Guid backfillId, long window, DateTimeOffset now, DateTimeOffset answerDueBy, CancellationToken cancellationToken = default) =>
    await _withCoordinatorCommandAsync(async (cmd, schema) => {
      await Whizbang.Data.Postgres.SubscriptionBackfillSql.MarkRequestedAsync(
        cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), backfillId, window, now, answerDueBy, cancellationToken)
        .ConfigureAwait(false);
      return 0;
    }, cancellationToken).ConfigureAwait(false);

  /// <inheritdoc />
  public async Task WithdrawSubscriptionBackfillWindowRequestAsync(
    SubscriptionBackfillCursor requestedFrom, long window, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(requestedFrom);
    await _withCoordinatorCommandAsync(async (cmd, schema) => {
      await Whizbang.Data.Postgres.SubscriptionBackfillSql.WithdrawRequestAsync(
        cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), requestedFrom, window, cancellationToken).ConfigureAwait(false);
      return 0;
    }, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task<SubscriptionBackfillCursor?> RecordSubscriptionBackfillAnswerAsync(
    BackfillWindowAnswer answer, DateTimeOffset now, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(answer);
    return _withCoordinatorCommandAsync((cmd, schema) => Whizbang.Data.Postgres.SubscriptionBackfillSql.RecordAnswerAsync(
      cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), answer, now, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public async Task EndSubscriptionBackfillAsync(
    Guid backfillId, SubscriptionBackfillOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken = default) =>
    await _withCoordinatorCommandAsync(async (cmd, schema) => {
      await Whizbang.Data.Postgres.SubscriptionBackfillSql.EndAsync(
        cmd, Whizbang.Data.Postgres.PgIdentifier.QualifyPrefix(schema), backfillId, outcome, now, cancellationToken).ConfigureAwait(false);
      return 0;
    }, cancellationToken).ConfigureAwait(false);

  /// <inheritdoc />
  /// <remarks>One index probe per type (a lateral over idx_event_store_type_commit_sequence, once the concurrent
  /// framework index build has made it valid), never a scan of the type.</remarks>
  public Task<long?> GetRedeliveryHeadCommitSequenceAsync(IReadOnlyList<string> eventTypes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypes);
    return _withCoordinatorCommandAsync<long?>(async (cmd, schema) => {
      var normalize = BuildSchemaQualifiedName(schema, NORMALIZE_EVENT_TYPE_FUNCTION);
      cmd.CommandText =
        "SELECT max(h.head) FROM unnest(@p_types::text[]) AS t CROSS JOIN LATERAL (" +
        $"SELECT max(es.commit_sequence) AS head FROM {schema}.wh_event_store es " +
        $"WHERE es.event_type = {normalize}(t) AND es.commit_sequence IS NOT NULL) AS h";
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
        Value = eventTypes.ToArray()
      });
      var head = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
      return head is long value ? value : null;
    }, cancellationToken);
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
      AddDigestFilterParams(cmd, originServiceId, eventTypes);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, (int)settleWindow.TotalSeconds));

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
      var digestId = DigestRuleSql.DigestIdEs(schema);
      // #2363: a recomputed answer is the live fold plus what cuts carried, the same comparable digest the bucket stores,
      // so a stream this service cut answers exactly as it did before the cut.
      cmd.CommandText = $"""
        SELECT f.tenant, f.event_type, f.stream_id, bit_xor(f.lo), bit_xor(f.hi), SUM(f.n)::int, bit_xor(f.ilo), bit_xor(f.ihi)
        FROM (
          SELECT COALESCE(es.scope->>'t', '') AS tenant, {DigestRuleSql.DIGEST_TYPE_ES} AS event_type, es.stream_id,
                 {schema}._wh_digest_lane({digestId}, 0) AS lo,
                 {schema}._wh_digest_lane({digestId}, 1) AS hi,
                 1 AS n,
                 {schema}._wh_digest_lane({digestId}, 2) AS ilo,
                 {schema}._wh_digest_lane({digestId}, 3) AS ihi
          FROM {schema}.wh_event_store es
          WHERE ((@p_origin::uuid) IS NULL AND es.origin_service_id IS NULL
                 OR es.origin_service_id = @p_origin)
            AND ((@p_types::text[]) IS NULL OR {DigestRuleSql.DIGEST_TYPE_ES} IN (SELECT {BuildSchemaQualifiedName(schema, NORMALIZE_EVENT_TYPE_FUNCTION)}(t) FROM unnest(@p_types::text[]) AS t))
            AND {DigestRuleSql.EligibleEs}
            AND {DigestRuleSql.SlotCountsEs(schema)}
            AND es.created_at < NOW() - @p_settle::interval
          UNION ALL
          {CarriedDigestSql(schema, "d.origin_service_id = COALESCE(@p_origin::uuid, '00000000-0000-0000-0000-000000000000'::uuid)", byType: true)}
        ) f
        GROUP BY 1, 2, 3
        ORDER BY 1, 2, 3
        """;
      AddDigestFilterParams(cmd, originServiceId, eventTypes);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, $"{(int)settleWindow.TotalSeconds} seconds"));

      return await _readStreamDigestsAsync(cmd, hasUpdatedAt: false, typeLevel: false, cancellationToken, hasIdLanes: true)
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
      cmd.CommandText = PerspectiveCoverageGapSql.Build(schema);
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, $"{(int)settleWindow.TotalSeconds} seconds"));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_MAX, Math.Max(1, maxGaps)));

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
      var normalizeFn = BuildSchemaQualifiedName(schema, NORMALIZE_EVENT_TYPE_FUNCTION);
      cmd.CommandText = $"""
        SELECT scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count, updated_at, id_lo, id_hi
        FROM {schema}.wh_stream_digests
        WHERE origin_service_id = COALESCE(@p_origin::uuid, '{ZERO_ORIGIN_UUID}'::uuid)
          AND ((@p_types::text[]) IS NULL OR event_type IN (SELECT {normalizeFn}(t) FROM unnest(@p_types::text[]) AS t))
        ORDER BY 1, 2, 3
        """;
      AddDigestFilterParams(cmd, originServiceId, eventTypes);

      return await _readStreamDigestsAsync(cmd, hasUpdatedAt: true, typeLevel: false, cancellationToken, hasIdLanes: true)
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
      var normalizeFn = BuildSchemaQualifiedName(schema, NORMALIZE_EVENT_TYPE_FUNCTION);
      cmd.CommandText = $"""
        SELECT scope_tenant, event_type, bit_xor(digest_lo), bit_xor(digest_hi),
               SUM(event_count)::int, MAX(updated_at)
        FROM {schema}.wh_stream_digests
        WHERE origin_service_id = COALESCE(@p_origin::uuid, '{ZERO_ORIGIN_UUID}'::uuid)
          AND ((@p_types::text[]) IS NULL OR event_type IN (SELECT {normalizeFn}(t) FROM unnest(@p_types::text[]) AS t))
        GROUP BY 1, 2
        ORDER BY 1, 2
        """;
      AddDigestFilterParams(cmd, originServiceId, eventTypes);

      return await _readStreamDigestsAsync(cmd, hasUpdatedAt: true, typeLevel: true, cancellationToken)
        .ConfigureAwait(false);
    }, cancellationToken);

  /// <summary>Materializes digest rows. Stream-level column order is (tenant, type, stream, lo,
  /// hi, count[, updated]); type-level omits the stream column and carries <see cref="Guid.Empty"/>.
  /// Recomputed reads have no update time.</summary>
  private static async Task<IReadOnlyList<StreamDigest>> _readStreamDigestsAsync(
      System.Data.Common.DbCommand cmd, bool hasUpdatedAt, bool typeLevel, CancellationToken cancellationToken,
      bool hasIdLanes = false) {
    var results = new List<StreamDigest>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    var idx = typeLevel ? 2 : 3;
    // #2103: the id lanes follow the count (and the update time, when read); NULL when a stored bucket has none yet.
    var idLanes = idx + (hasUpdatedAt ? 4 : 3);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var tenant = reader.GetString(0);
      results.Add(new StreamDigest {
        TenantScope = tenant.Length == 0 ? null : tenant,
        EventType = reader.GetString(1),
        StreamId = typeLevel ? Guid.Empty : reader.GetGuid(2),
        DigestLo = reader.GetInt64(idx),
        DigestHi = reader.GetInt64(idx + 1),
        EventCount = reader.GetInt32(idx + 2),
        UpdatedAt = hasUpdatedAt ? await reader.GetFieldValueAsync<DateTimeOffset>(idx + 3, cancellationToken) : null,
        IdLo = hasIdLanes ? await _nullableInt64Async(reader, idLanes, cancellationToken) : null,
        IdHi = hasIdLanes ? await _nullableInt64Async(reader, idLanes + 1, cancellationToken) : null,
      });
    }
    return results;
  }

  private static async Task<long?> _nullableInt64Async(
      System.Data.Common.DbDataReader reader, int ordinal, CancellationToken cancellationToken) =>
    await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(ordinal);

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

  /// <inheritdoc />
  public Task<DigestVerificationResult> VerifyDigestTableSliceAsync(
    FullHistoryCompareRequest request, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    return _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        "SELECT buckets_checked, drift_updated, drift_removed, drift_added, streams_checked, pass_completed, pass_runs " +
        $"FROM {BuildSchemaQualifiedName(schema, "wh_heal_stream_digests_slice")}(@p_settle::interval, @p_lookback::interval, @p_max)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, $"{(long)request.SettleWindow.TotalSeconds} seconds"));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_LOOKBACK, $"{(long)request.Lookback.TotalSeconds} seconds"));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_MAX, Math.Max(1, request.MaxPerRun)));
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
      return new DigestVerificationResult {
        BucketsChecked = reader.GetInt32(0),
        DriftUpdated = reader.GetInt32(1),
        DriftRemoved = reader.GetInt32(2),
        DriftAdded = reader.GetInt32(3),
        StreamsChecked = reader.GetInt32(4),
        PassCompleted = reader.GetBoolean(5),
        PassRuns = reader.GetInt32(6),
      };
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<long> GetFullHistoryCompareStartAsync(
    Guid originServiceId, TimeSpan lookback, long epochWidth, long seal, CancellationToken cancellationToken = default) =>
    _withCoordinatorCommandAsync(async (cmd, schema) => {
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
      cmd.CommandText =
        $"SELECT {BuildSchemaQualifiedName(schema, "wh_full_history_compare_start")}(@p_origin, @p_lookback::interval, @p_width, @p_seal)";
#pragma warning restore S2077
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_origin", originServiceId));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_LOOKBACK, $"{(long)lookback.TotalSeconds} seconds"));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_width", epochWidth));
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_seal", seal));
      return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }, cancellationToken);

  private static void _prepareVerifyDigestCommand(
      System.Data.Common.DbCommand cmd, string schema, TimeSpan settleWindow) {
    // A1c trust-but-verify: recompute settled buckets from the event store and heal the table three ways (update
    // drifted / delete phantom / insert missing). The settle gates (bucket updated_at, event created_at) keep in-flight
    // folds out of both sides: a bucket touched inside the window is skipped this pass, and a fresh event with no
    // bucket is not "missing", it simply has not settled.
    // #2106: the heal is the shared SQL helper, which recomputes with the one lane function and rewrites every lane
    // of a drifted bucket together. It returns one row per bucket it changed; the sweep counts them by kind. Id lanes
    // never computed are left to the bounded backfill (p_fill_id_lanes false): filling them here would rewrite the
    // whole table in one statement.
    cmd.CommandText = $"""
      WITH heal AS (
        SELECT h.kind FROM {schema}._wh_heal_stream_digests(NULL, @p_settle::interval, false) h
      )
      SELECT (SELECT COUNT(*)::int FROM {schema}.wh_stream_digests
              WHERE updated_at < NOW() - @p_settle::interval),
             (SELECT COUNT(*)::int FROM heal WHERE kind = 'drift_updated'),
             (SELECT COUNT(*)::int FROM heal WHERE kind = 'drift_removed'),
             (SELECT COUNT(*)::int FROM heal WHERE kind = 'drift_added')
      """;
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_SETTLE, $"{(int)settleWindow.TotalSeconds} seconds"));
  }

  /// <summary>
  /// The carried part of the stream digest buckets a recomputed fold adds to its live events (#2363): what cuts removed
  /// here, which the bucket's digest still counts. One row per bucket that carries anything, in the recomputed fold's
  /// columns (tenant, type, stream, the four lanes as lo, hi, ilo, ihi, and the count as n). A bucket whose carried detail
  /// rolled off (NULL) adds nothing: its digest is no longer the live fold plus a known part.
  /// </summary>
  /// <param name="schema">The validated schema name.</param>
  /// <param name="predicate">The buckets to read, as a condition on <c>d</c>.</param>
  /// <param name="byType">Whether to apply the <c>@p_types</c> filter, normalized as the live fold applies it.</param>
  /// <returns>The SELECT, ready to UNION ALL with the live fold.</returns>
  /// <docs>resilience/stream-integrity#cuts-carry-their-digest</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CutsCarryTheirDigestCoordinatorTests.cs</tests>
#pragma warning disable S2077, RCS1158 // Validated schema constant, every value bound; shared by every generic instantiation.
  internal static string CarriedDigestSql(string schema, string predicate, bool byType) =>
    "SELECT d.scope_tenant, d.event_type, d.stream_id, d.carried_lo, d.carried_hi, d.carried_count, " +
    "d.carried_id_lo, d.carried_id_hi " +
    $"FROM {schema}.wh_stream_digests d " +
    $"WHERE {predicate} AND d.carried_count <> 0" +
    (byType
      ? $" AND ((@p_types::text[]) IS NULL OR d.event_type IN (SELECT {BuildSchemaQualifiedName(schema, NORMALIZE_EVENT_TYPE_FUNCTION)}(t) FROM unnest(@p_types::text[]) AS t))"
      : "");
#pragma warning restore S2077, RCS1158

  /// <summary>
  /// The windowed stream-digest query <see cref="ComputeStreamDigestsWindowedAsync"/> runs: one page of whole streams
  /// in stream-id order, folded over the lane's window <c>[@p_since, @p_until)</c>. The fold is
  /// <c>wh_compute_stream_digests_window</c> (#2107), which plans each call against its own window and types, so a sparse
  /// type is read through an index on the type instead of being filtered from every event in the window.
  /// </summary>
  /// <param name="schema">The validated schema name.</param>
  /// <returns>The query text.</returns>
  /// <docs>resilience/stream-integrity#windowed-manifests</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestLaneReadTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WindowedDigestTypeIndexTests.cs</tests>
#pragma warning disable S2077, RCS1158 // Validated schema constant, every value bound; shared by every generic instantiation.
  internal static string WindowedStreamDigestSql(string schema) =>
    "SELECT tenant, event_type, stream_id, digest_lo, digest_hi, event_count, id_lo, id_hi " +
    $"FROM {BuildSchemaQualifiedName(schema, "wh_compute_stream_digests_window")}" +
    "(@p_origin, @p_types, @p_since, @p_until, @p_resume, @p_limit, NOW(), @p_settle)";
#pragma warning restore S2077, RCS1158

  /// <summary>
  /// Binds the digest queries' shared filter: the origin service and the event types, each null when
  /// the caller does not filter on it, which the SQL reads as "every origin" and "every type".
  /// </summary>
  /// <param name="cmd">The command the parameters are added to.</param>
  /// <param name="originServiceId">The origin to filter on, or null for every origin.</param>
  /// <param name="eventTypes">The event types to filter on, or null for every type.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorParameterBindingTests.cs</tests>
#pragma warning disable RCS1158 // Static helper shared by every generic instantiation; it does not depend on TDbContext.
  internal static void AddDigestFilterParams(
      System.Data.Common.DbCommand cmd, Guid? originServiceId, IReadOnlyList<string>? eventTypes) {
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_ORIGIN, NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = (object?)originServiceId ?? DBNull.Value
    });
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_TYPES, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = eventTypes is null ? DBNull.Value : eventTypes.ToArray()
    });
  }

#pragma warning restore RCS1158

  /// <inheritdoc />
  public async Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));

    var sql = $"""
      SELECT
        (SELECT COUNT(*) FROM {schema}.wh_perspective_events WHERE processed_at IS NULL)::bigint as "PendingPerspectiveEvents",
        (SELECT COUNT(*) FROM {schema}.wh_outbox WHERE processed_at IS NULL)::bigint as "PendingOutbox",
        (SELECT COUNT(*) FROM {schema}.wh_inbox_state WHERE processed_at IS NULL)::bigint as "PendingInbox",
        (SELECT COUNT(*) FROM {schema}.wh_active_streams)::bigint as "ActiveStreams"
      """;

    var result = await _dbContext.Database
      .SqlQueryRaw<WorkCoordinatorStatistics>(sql)
      .ToListAsync(cancellationToken);

    // Scalar subqueries with no FROM of their own: always exactly one row.
    return result.Single();
  }

  /// <inheritdoc />
  public async Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(
    int partitionCount,
    CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "recompute_partition_numbers");

    long inbox = 0;
    long outbox = 0;
    long active = 0;

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var connection = __scope.Connection;
    await using var cmd = connection.CreateCoordinatorCommand();
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
        case OUTBOX_TABLE: outbox = count; break;
        case "wh_active_streams": active = count; break;
      }
    }

    return new PartitionRecomputeResult {
      InboxRowsRecomputed = inbox,
      OutboxRowsRecomputed = outbox,
      ActiveStreamsRowsRecomputed = active
    };
  }

  /// <summary>
  /// Stores inbox messages directly via the store_inbox_messages SQL function, which only inserts
  /// them with deduplication. Event storage and perspective work for inbox events happen when
  /// claim_work later claims the rows (its _emit_event_store_chain_for_inbox step).
  /// </summary>
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

    var schema = _resolveSchema(typeof(OutboxRecord));
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
      // #720: ring the doorbells the store queued, after its commit. Under an ambient transaction the
      // rows are not visible yet and the next hot call rings them.
      if (_dbContext.Database.CurrentTransaction is null) {
        await using var ringScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
            (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
        await DoorbellRinger.RingAsync(ringScope.Connection, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
      }
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

    var schema = _resolveSchema(typeof(OutboxRecord));
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
      await using var cmd = scope.Connection.CreateCoordinatorCommand();
      cmd.CommandText = sql;
      cmd.Parameters.AddWithValue(nameof(messages), json);
      cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
      cmd.Parameters.AddWithValue(nameof(partitionCount), partitionCount);
      cmd.Parameters.AddWithValue("observedIds", observedIds);
      await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
        // Skip the store's own rowset, then read the observation projection.
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { /* discard */ }
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)
            && await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          // An aggregate with no GROUP BY returns one row, and its COALESCE makes that row's value never NULL.
          projection = reader.GetString(0);
        }
      }
      // #720: ring the doorbells the store queued, after its commit.
      await DoorbellRinger.RingAsync(scope.Connection, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "store_outbox_messages");

#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    var sql = $"SELECT message_id, was_newly_created FROM {functionName}(@p_messages::jsonb, NULL::uuid, NULL::timestamptz, @p_now, @p_partition_count)";
#pragma warning restore S2077

    // #1546: a WHIZ133 refusal of an event this write's caller awaits reaches it as MixedStreamEventException, and
    // (#1733) an awaited event on a sealed stream as StreamSealedException, and (#1738) one on a stream archived whole as
    // StreamArchivedException.
    await Whizbang.Data.Postgres.StreamArchivedRefusal.RunAsync(() => Whizbang.Data.Postgres.StreamSealedRefusal.RunAsync(() => Whizbang.Data.Postgres.MixedStreamRefusal.RunAsync(() => PostgresDeadlockRetry.ExecuteAsync(async () => {
      var now = DateTime.UtcNow;
      // The store reports which rows it skipped because the same message id already existed. With
      // deterministic emission identity (EmissionIdentity) a skipped row is the republish a retry would
      // have produced, so it is counted rather than silently discarded. Same connection as the
      // DbContext, enlisted in its transaction when one is open, so the semantics of the former
      // ExecuteSqlRawAsync call are unchanged.
      var connection = (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection();
      await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(connection, cancellationToken);
      await using var cmd = connectionScope.Connection.CreateCoordinatorCommand();
      cmd.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction() as Npgsql.NpgsqlTransaction;
      cmd.CommandText = sql;
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_messages", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = json });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_NOW, NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = now });
      cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_partition_count", NpgsqlTypes.NpgsqlDbType.Integer) { Value = partitionCount });
      // The reader is closed before the ring so the ring can use the same connection.
      await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken)) {
        await _reportDeduplicatedOutboxRowsAsync(reader, messages, cancellationToken);
      }
      // #720: ring after the store. Inside an ambient transaction the queued rows commit with it and
      // the ring below finds nothing yet; the next hot call on this connection rings them.
      if (cmd.Transaction is null) {
        await DoorbellRinger.RingAsync(connectionScope.Connection, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
      }
    }, logger: _logger, cancellationToken: cancellationToken))));
  }

  /// <inheritdoc />
  /// <docs>extending/internals/outbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OutboxFlushPoolExhaustionTests.cs:StoreOutboxDeadLetters_WritesARecoverableDeadLetterAndALogRowPerMessageAsync</tests>
  public async Task<bool> StoreOutboxDeadLettersAsync(
    OutboxMessage[] messages,
    OutboxDeadLetterRequest request,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(request);
    if (messages.Length == 0) {
      return true;
    }

    var call = new UnstoredDeadLetters(
      _serializeNewOutboxMessages(messages), messages.Length, "wh_store_outbox_dead_letters",
      (int)request.FailureReason, request.ErrorText, request.Attempts, request.InstanceId, request.Generation);
    await _storeUnstoredDeadLettersAsync(call, cancellationToken);
    return true;
  }

  /// <inheritdoc />
  /// <docs>extending/internals/inbox-batch-strategy#when-a-flush-fails</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InboxFlushDeadLetterTests.cs:StoreInboxDeadLetters_WritesARecoverableDeadLetterAndALogRowPerMessageAsync</tests>
  public async Task<bool> StoreInboxDeadLettersAsync(
    InboxMessage[] messages,
    InboxDeadLetterRequest request,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(request);
    if (messages.Length == 0) {
      return true;
    }

    await _storeUnstoredDeadLettersAsync(new UnstoredDeadLetters(
      _serializeNewInboxMessages(messages), messages.Length, "wh_store_inbox_dead_letters",
      (int)request.FailureReason, request.ErrorText, request.Attempts, request.InstanceId, request.Generation), cancellationToken);
    return true;
  }

  /// <summary>The arguments of <c>wh_store_outbox_dead_letters</c> (256) and <c>wh_store_inbox_dead_letters</c> (257).</summary>
  private sealed record UnstoredDeadLetters(
    string MessagesJson, int Count, string Function, int FailureReason, string? ErrorText, int Attempts, Guid InstanceId, string Generation);

  /// <summary>
  /// Writes messages a batcher accepted and never stored to the dead-letter table through the given function, one
  /// dead letter id per message (#1871, #1885).
  /// </summary>
  private async Task _storeUnstoredDeadLettersAsync(UnstoredDeadLetters call, CancellationToken cancellationToken) {
    var deadLetterIds = new Guid[call.Count];
    for (var i = 0; i < deadLetterIds.Length; i++) {
      deadLetterIds[i] = (Guid)TrackedGuid.New();
    }
    var functionName = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), call.Function);

    var connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
    await using var connectionScope = await CoordinatorConnectionScope.AcquireForEfCoreAsync(connection, cancellationToken);
    await using var cmd = connectionScope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT {functionName}(@p_messages::jsonb, @p_dead_letter_ids, @p_failure_reason, @p_error_text, @p_attempts, @p_instance_id, @p_generation)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("p_messages", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = call.MessagesJson });
    cmd.Parameters.Add(new NpgsqlParameter("p_dead_letter_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = deadLetterIds });
    cmd.Parameters.Add(new NpgsqlParameter("p_failure_reason", NpgsqlTypes.NpgsqlDbType.Integer) { Value = call.FailureReason });
    cmd.Parameters.Add(new NpgsqlParameter("p_error_text", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)call.ErrorText ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_attempts", NpgsqlTypes.NpgsqlDbType.Integer) { Value = call.Attempts });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = call.InstanceId });
    cmd.Parameters.Add(new NpgsqlParameter("p_generation", NpgsqlTypes.NpgsqlDbType.Text) { Value = call.Generation });
    await cmd.ExecuteScalarAsync(cancellationToken);
  }

  /// <summary>
  /// An outbox row was skipped because its message id already existed: a retry re-emitted a message whose
  /// first emission had already committed. Debug because the metric is the operator's signal; the log
  /// names the row for forensics.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreOutboxEmissionDedupTests.cs</tests>
  /// <summary>
  /// Counts and logs each row <c>store_outbox_messages</c> skipped because the same message id already existed: with
  /// deterministic emission identity a skipped row is the republish a retry would have produced.
  /// </summary>
  /// <remarks>The store echoes exactly the ids it was given, so an unknown id here is a schema bug worth a loud failure.</remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreOutboxEmissionDedupTests.cs</tests>
  private async Task _reportDeduplicatedOutboxRowsAsync(
      System.Data.Common.DbDataReader reader, OutboxMessage[] messages, CancellationToken cancellationToken) {
    while (await reader.ReadAsync(cancellationToken)) {
      if (reader.GetBoolean(1)) {
        continue;
      }
      var skippedId = reader.GetGuid(0);
      var messageType = messages.First(m => m.MessageId == skippedId).MessageType;
      _metrics?.OutboxEmissionDeduplicated.Add(1, new KeyValuePair<string, object?>("message_type", messageType));
      _logOutboxEmissionDeduplicated(_logger, skippedId, messageType);
    }
  }

  private static void _logOutboxEmissionDeduplicated(ILogger? logger, Guid messageId, string messageType) {
    if (logger is null) {
      return;
    }
    if (!logger.IsEnabled(LogLevel.Debug)) {
      return;
    }
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    logger.LogDebug("store_outbox_messages skipped {MessageId} ({MessageType}): already stored, so this emission was a retry's republish and is counted, not duplicated",
      messageId, messageType);
#pragma warning restore CA1848
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<Whizbang.Core.Messaging.CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(
    CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoalesceSql.PendingGroupStatsAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(
    string group,
    int limit,
    CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(group);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    // 300 (#1636): with an instance identity, a caller whose own row says it may not claim folds nothing.
    return await CoalesceSql.FetchPendingAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), group, limit, _instanceProvider?.InstanceId, _jsonOptions, cancellationToken).ConfigureAwait(false);
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    var foldedIdArray = AsArray(foldedIds);

    // ONE transaction: the composite row(s) appear and the folded singles complete together —
    // a single is either still pending (floor intact) or folded (composite exists), never both,
    // never neither. BeginTransactionAsync MUST run inside the connection's execution strategy:
    // an unwrapped user transaction throws under the retrying strategy.
    var strategy = _dbContext.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () => {
      await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
      await CoalesceSql.StoreCompositeAndCompleteSinglesAsync(
        transaction.GetDbTransaction(), PgIdentifier.QualifyPrefix(schema), json,
        DateTime.UtcNow, partitionCount, foldedIdArray, cancellationToken).ConfigureAwait(false);
      await transaction.CommitAsync(cancellationToken);
      // #720: ring the doorbells the fold queued, after the commit.
      await using var ringScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await DoorbellRinger.RingAsync(ringScope.Connection, BuildSchemaQualifiedName(schema, DoorbellRinger.FUNCTION_NAME), _logger, cancellationToken);
    });
  }

  /// <inheritdoc />
  public async Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(group);

    var schema = _resolveSchema(typeof(OutboxRecord));

    // The deadline degrade: clearing group + floor moves the row into the eligible-scan index,
    // so the normal pump ships it individually. Explicit transition, never a query union.
    var released = 0;
    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      released = await CoalesceSql.ReleaseMaturedAsync(cmd, PgIdentifier.QualifyPrefix(schema), group, cancellationToken).ConfigureAwait(false);
    }, logger: _logger, cancellationToken: cancellationToken);
    return released;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<Whizbang.Core.Messaging.StuckRow>> FindStuckOutboxRowsAsync(
    int maxAttempts,
    int limit,
    CancellationToken cancellationToken = default)
    => await _findStuckRowsAsync(CoordinatorFleetSql.STUCK_OUTBOX, maxAttempts, limit, cancellationToken);

  /// <inheritdoc />
  public async Task<IReadOnlyList<Whizbang.Core.Messaging.StuckRow>> FindStuckInboxRowsAsync(
    int maxAttempts,
    int limit,
    CancellationToken cancellationToken = default)
    => await _findStuckRowsAsync(CoordinatorFleetSql.STUCK_INBOX, maxAttempts, limit, cancellationToken);

  /// <inheritdoc />
  /// <remarks>Calls <c>wh_drop_exhausted_inbox_rows</c> (migration 350, #2117). An empty set costs no round trip.</remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ExhaustedInboxDropSqlTests.cs</tests>
  public async Task<int> DropExhaustedInboxRowsAsync(
    IReadOnlyList<Guid> messageIds,
    int maxAttempts,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(messageIds);
    if (messageIds.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorFleetSql.DropExhaustedInboxRowsAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), messageIds, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  // v0.657 slice 5c: shared shape for both sentinel methods — they only differ
  // in the SQL function name. Both use the partial indexes from migration 054
  // so the cost is O(log N) on a near-empty partial in steady state.
  private async Task<IReadOnlyList<Whizbang.Core.Messaging.StuckRow>> _findStuckRowsAsync(
      string functionShortName, int maxAttempts, int limit, CancellationToken ct) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(ct).ConfigureAwait(false);
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), ct);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorFleetSql.FindStuckRowsAsync(cmd, PgIdentifier.QualifyPrefix(schema), functionShortName, maxAttempts, limit, ct).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<int> CleanupCompletedStreamsAsync(
    IReadOnlyList<Guid> streamIds,
    CancellationToken cancellationToken = default) {
    if (streamIds is null || streamIds.Count == 0) {
      return 0;
    }
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "cleanup_completed_streams");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    await using var cmd = conn.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_stream_ids)";
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter(P_STREAM_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streamIds.ToArray(),
    });
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return ScalarResult.IntOrZero(result);
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
      new CursorCompletionWrite(
        completion.StreamId, completion.PerspectiveName,
        completion.LastEventId, completion.ProcessedEventIds,
        (short)completion.Status, Error: null,
        completion.ReplayedThroughCommitSequence),
      cancellationToken);

    await _logCheckpointDiagnosticAsync(completion.StreamId, completion.PerspectiveName, cancellationToken);
  }

  /// <summary>
  /// The arguments of one complete_perspective_cursor_work call: a completion or a failure of one stream and perspective.
  /// </summary>
  private readonly record struct CursorCompletionWrite(
    Guid StreamId, string PerspectiveName, Guid LastEventId,
    Guid[] ProcessedEventIds,
    short Status, string? Error,
    long? ReplayedThroughCommitSequence);

  /// <summary>
  /// Executes the complete_perspective_cursor_work SQL function within a managed transaction.
  /// Creates a new transaction if one does not already exist on the DbContext.
  /// </summary>
  private async Task _executeCursorCompletionAsync(CursorCompletionWrite write, CancellationToken cancellationToken) {
    var (streamId, perspectiveName, lastEventId, processedEventIds, status, error, replayedThroughCommitSequence) = write;
    var transaction = _dbContext.Database.CurrentTransaction;
    var needsCommit = transaction == null;

    if (needsCommit) {
      transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    try {
      var schema = _resolveSchema(typeof(OutboxRecord));
      var functionName = BuildSchemaQualifiedName(schema, "complete_perspective_cursor_work");
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant; parameters use EF Core positional placeholders ({0}..{6})
      var sql = $"SELECT {functionName}({{0}}, {{1}}, {{2}}, {{3}}::jsonb, {{4}}, {{5}}::text, {{6}}::bigint)";
#pragma warning restore S2077

      // Serialize ProcessedEventIds as JSON string for the JSONB parameter (AOT-safe)
      var processedEventIdsJson = System.Text.Json.JsonSerializer.Serialize(
        processedEventIds,
        _jsonOptions.GetTypeInfo(typeof(Guid[])));

      await _dbContext.Database.ExecuteSqlRawAsync(
        sql,
        // #2366: the replay frontier, stored on the cursor; a typed null when the run was not a replay.
        [streamId, perspectiveName, lastEventId, processedEventIdsJson, status, error!, replayedThroughCommitSequence!],
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
    var diagnosticSchema = _resolveSchema(typeof(OutboxRecord));
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
      new CursorCompletionWrite(
        failure.StreamId, failure.PerspectiveName,
        failure.LastEventId, failure.ProcessedEventIds,
        (short)failure.Status, failure.Error,
        ReplayedThroughCommitSequence: null),
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
    var schema = _resolveSchema(typeof(OutboxRecord));
    var eventStoreTable = BuildSchemaQualifiedName(schema, EVENT_STORE_TABLE);
#pragma warning disable S2077 // Schema-qualified table names built from validated schema constant; parameters use EF Core positional placeholders ({0}, {1})
    // Slice 26.13: LEFT JOIN wh_event_store so cold-cache cursor hydration warms the
    // commit_sequence half of PerspectiveCursorCache (see GetPerspectiveCursorsBatchAsync).
    // #1734: the live cursor, else the one a whole-stream archive parked (a NewGeneration stream resumes after it).
    var resumeCursors = BuildSchemaQualifiedName(schema, RESUME_CURSORS_FUNCTION);
    var cursorsTable = BuildSchemaQualifiedName(schema, PERSPECTIVE_CURSORS_TABLE);
    var sql = "SELECT c.stream_id, c.perspective_name, c.last_event_id, c.status, "
            + "c.rewind_trigger_event_id, e.commit_sequence AS last_commit_sequence, "
            // #2366: the frontier a completed replay read through, from the live cursor.
            + "lc.replayed_through_commit_sequence "
            + $"FROM {resumeCursors}(ARRAY[{{0}}]::uuid[], {{1}}) c "
            + $"LEFT JOIN {eventStoreTable} e ON e.event_id = c.last_event_id "
            + $"LEFT JOIN {cursorsTable} lc ON lc.stream_id = c.stream_id AND lc.perspective_name = c.perspective_name";
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
      LastCommitSequence = result.LastCommitSequence,
      ReplayedThroughCommitSequence = result.ReplayedThroughCommitSequence
    };
  }

  /// <inheritdoc />
  public async Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(
    Guid[] streamIds,
    CancellationToken cancellationToken = default) {

    if (streamIds.Length == 0) {
      return [];
    }

    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    // Slice 26.13: the commit sequence of each cursor's last event warms the cursor cache's inversion check.
    return await CoordinatorCatalogSql.PerspectiveCursorsBatchAsync(cmd, PgIdentifier.QualifyPrefix(schema), streamIds, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc/>
  public async Task RecordLifecycleCompletionAsync(
    Guid eventId,
    CancellationToken cancellationToken = default) {

    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    // Idempotent: ON CONFLICT DO NOTHING handles duplicate event IDs.
    await OrphanedLifecycleEventsSql.RecordCompletionAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), eventId, _instanceId(), cancellationToken).ConfigureAwait(false);
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    var eventStoreTable = BuildSchemaQualifiedName(schema, EVENT_STORE_TABLE);
    var bodyTable = BuildSchemaQualifiedName(schema, EVENT_BODY_TABLE);
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
    var (pairTypes, pairNames) = OrphanedLifecycleEventsSql.Pairs(perspectivesPerEventType);
    if (pairTypes.Length == 0) {
      return [];
    }

    var sql = OrphanedLifecycleEventsSql.Build(eventStoreTable, bodyTable, cursorsTable, completionsTable);

    try {
      // Bounded even when the store is degraded: the reconcile may time out a pass and retry
      // later — it must never hold the host's resources indefinitely.
      var previousTimeout = _dbContext.Database.GetCommandTimeout();
      _dbContext.Database.SetCommandTimeout(120);
      List<OrphanedEventRow> rows;
      try {
        rows = await _dbContext.Database
          .SqlQueryRaw<OrphanedEventRow>(sql, pairTypes, pairNames, cutoff, Math.Max(1, maxOrphans))
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    var cutoff = DateTimeOffset.UtcNow - retentionPeriod;

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var conn = __scope.Connection;
    return await CoordinatorRetentionSql.CleanupLifecycleCompletionsAsync(
      () => conn.CreateCoordinatorCommand(), PgIdentifier.QualifyPrefix(schema), cutoff, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Deserializes an orphaned event row from the event store into a MessageEnvelope with JsonElement payload.
  /// Falls back to JsonElement.Parse when the type resolver returns incompatible type info.
  /// </summary>
  /// <docs>fundamentals/events/event-store-serialization</docs>
  private MessageEnvelope<JsonElement> _deserializeEventEnvelope(OrphanedEventRow row) =>
    OrphanedLifecycleEventsSql.ToEnvelope(row.EventId, row.EventData, row.Scope, _jsonOptions,
      ex => EFCoreWorkCoordinatorLog.ReconcileScopeParseFailed(_logger ?? NullLogger<EFCoreWorkCoordinator<TDbContext>>.Instance, ex));

  /// <summary>
  /// Broker DLQ import (migration 118): gives a broker-dead-lettered message durable custody as a
  /// wh_dead_letters row via <c>wh_import_dead_letter</c>. The RAW wire body travels as TEXT —
  /// the SQL side parses defensively (non-JSON bodies become JSON strings), so this path never
  /// deserializes and never loses a message to a parse failure. Idempotent on the wire message id.
  /// </summary>
  /// <remarks>
  /// The caller hands over the wire's envelope type name; custody records the PAYLOAD type it wraps,
  /// the form a received inbox row stores (#934). Recovery re-emits the row into the inbox under
  /// this name, and the inbox gate judges it by this name: recorded as the envelope, which nothing
  /// consumes, every recovered message was skipped as having no consumer.
  /// </remarks>
  /// <docs>operations/dead-letter-queue/transport-recovery</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BrokerDeadLetterImportSqlTests.cs:Import_OfAnEnvelopeTypedMessage_RecordsAndRecoversItsPayloadTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredBrokerDeadLetterDispatchIntegrationTests.cs:RecoveredBrokerDeadLetter_ReachesItsCompileTimeReceptorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RecoveredBrokerDeadLetterDispatchIntegrationTests.cs:RecoveredBrokerDeadLetter_ReachesItsRuntimeRegisteredConsumerAsync</tests>
  public async Task<bool> ImportBrokerDeadLetterAsync(
      Whizbang.Core.Transports.BrokerDeadLetterImport import,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(import);
    try {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      var schema = _resolveSchema(typeof(OutboxRecord));
      await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
          (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
      await using var cmd = __scope.Connection.CreateCoordinatorCommand();
      return await CoordinatorFleetSql.ImportBrokerDeadLetterAsync(
        cmd, PgIdentifier.QualifyPrefix(schema), import, (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New(), _instanceId(),
        new Whizbang.Core.Messaging.DefaultGenerationProvider().GetGeneration(), cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      // Throw, never return: FALSE means "duplicate — custody already exists, safe to settle at the
      // broker". A failed import must NOT look like a duplicate, or the drainer would complete the
      // broker message and lose it. Throwing makes the drainer abandon, so the broker re-offers it.
      throw CoordinatorFleetSql.ImportFailed(import, ex);
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
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorCatalogSql.CursorsRequiringRewindAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Deletes processed perspective event rows via complete_perspective_events SQL function.
  /// Called after drain mode processing completes for a batch of events.
  /// </summary>
  /// <docs>operations/workers/perspective-worker#completion-flush-path</docs>
  public async Task<int> CompletePerspectiveEventsAsync(
    Guid[] workItemIds,
    bool debugMode,
    CancellationToken cancellationToken = default) {
    if (workItemIds.Length == 0) {
      return 0;
    }

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "complete_perspective_events");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
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
    return ScalarResult.IntOrZero(result);
  }

  /// <summary>
  /// Batch-fetches events for multiple streams in a single call via get_stream_events SQL function.
  /// Returns denormalized rows: one per (stream, event). C# groups by StreamId for processing.
  /// </summary>
  /// <docs>operations/workers/perspective-worker#error-tracking-retry</docs>
  /// <inheritdoc />
  public async Task<int> ReapExhaustedOrphanedPerspectiveRowsAsync(
    Guid instanceId,
    IReadOnlyList<Guid> streamIds,
    int maxAttempts,
    CancellationToken cancellationToken = default) {
    if (streamIds.Count == 0) {
      return 0;
    }

    var schema = _resolveSchema(typeof(OutboxRecord));

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorRetentionSql.ReapExhaustedOrphanedPerspectiveRowsAsync(
      cmd, PgIdentifier.QualifyPrefix(schema), instanceId, streamIds, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Reads the event rows column by column, and each optional column is read through its own catch so a provider that omits it yields the default rather than failing the whole read.")]
  public async Task<List<StreamEventData>> GetStreamEventsAsync(
    Guid instanceId,
    Guid[] streamIds,
    CancellationToken cancellationToken = default) {
    if (streamIds.Length == 0) {
      return [];
    }

    var schema = _resolveSchema(typeof(OutboxRecord));
    // 576 (#2551): the read that carries each event's version, its stream position.
    var functionName = BuildSchemaQualifiedName(schema, "get_stream_events_in_stream_order");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_instance_id, @p_stream_ids)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
#pragma warning disable RCS1130 // NpgsqlDbType third-party enum; bitwise composition is its documented API.
    cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
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
    var hasFailuresColumn = false;
    var failuresOrdinal = -1;
    try {
      failuresOrdinal = reader.GetOrdinal("out_failures");
      hasFailuresColumn = true;
    } catch (IndexOutOfRangeException) {
      // Pre-139 SQL function: no failure counter. Field stays 0, so the dead-letter check
      // never fires off a lease count (the safe direction; attempts is still tracked for diagnostics).
    }
    var firstInStreamOrdinal = -1;
    try {
      firstInStreamOrdinal = reader.GetOrdinal("out_first_in_stream");
    } catch (IndexOutOfRangeException) {
      // Pre-264 SQL function: no first-in-stream fact. Field stays false: an event whose position the read does not
      // know is never the first in its stream (#1878).
    }
    // 576 (#2551): the event's version, its stream position.
    var versionOrdinal = reader.GetOrdinal("out_version");
    var eventDataOrdinal = reader.GetOrdinal("out_event_data");
    while (await reader.ReadAsync(cancellationToken)) {
      // AOT-safe: read columns by ordinal, parse event_data as string
      var metadataOrdinal = reader.GetOrdinal("out_metadata");
      var scopeOrdinal = reader.GetOrdinal("out_scope");
      // #2591: a body the reap deleted reads as NULL. The row is returned named as unreadable, never cast: a cast threw
      // for the whole read and lost every other stream of the batch with it.
      var bodyMissing = await reader.IsDBNullAsync(eventDataOrdinal, cancellationToken).ConfigureAwait(false);
      results.Add(new StreamEventData {
        StreamId = reader.GetGuid(reader.GetOrdinal("out_stream_id")),
        EventId = reader.GetGuid(reader.GetOrdinal("out_event_id")),
        EventType = reader.GetString(reader.GetOrdinal("out_event_type")),
        EventData = bodyMissing ? string.Empty : reader.GetString(eventDataOrdinal),
        // Named reaped or missing by MissingEventBodySql below, once the read is done.
        UnreadableReason = bodyMissing ? StreamEventData.BODY_MISSING : null,
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
        Failures = hasFailuresColumn && !await reader.IsDBNullAsync(failuresOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetInt32(failuresOrdinal)
          : 0,
        IsFirstInStream = firstInStreamOrdinal >= 0 && reader.GetBoolean(firstInStreamOrdinal),
        Version = reader.GetInt64(versionOrdinal),
      });
    }

    await reader.CloseAsync().ConfigureAwait(false);
    // #2591: a row without a body is named reaped (an ephemeral event) or missing (any other).
    results = await Whizbang.Data.Postgres.MissingEventBodySql.NameAsync(
      dbConnection, PgIdentifier.QualifyPrefix(schema), results, cancellationToken).ConfigureAwait(false);

    // #2556: each fetched piece of work is recorded under its stream's claimed tenure, which its result then names.
    TenureFill.RecordFetchedPerspective(_tenures, results, r => r.EventWorkId, r => r.StreamId);
    return results;
  }

  /// <inheritdoc />
  /// <docs>messaging/work-coordinator#local-service-identity</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorServiceIdTests.cs</tests>
  public async Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) {
    // Schema-qualified like every other query here (issue #630): wh_service_config is created in the DbContext's schema.
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    return await CoordinatorCatalogSql.LocalServiceIdAsync(cmd, PgIdentifier.QualifyPrefix(schema), cancellationToken).ConfigureAwait(false);
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    // 579 (#2551): the stream-ordered fetch, which ranks each stream by emission position and returns it.
    var functionName = BuildSchemaQualifiedName(schema, "fetch_outbox_batch_in_stream_order");

    var streamArr = AsArray(streamIds);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_ids, @p_instance_id, @p_max_per_stream, @p_max_bytes)";
    cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamArr });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_max_per_stream", maxPerStream));
    // NULL = count bound only, which is exactly what this function did before the byte budget.
    cmd.Parameters.Add(new NpgsqlParameter("p_max_bytes", NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = (object?)maxBytes ?? DBNull.Value,
    });

    // 579 (#2551): the function is new in 579 and returns its full column set, so there are no older shapes to
    // tolerate here; its rows read as the continuation's do.
    var results = new List<OutboxBatchRow>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(await _readOutboxRowAsync(reader, cancellationToken));
    }
    // #2556: each fetched message is recorded under its stream's claimed tenure, which its result then names.
    TenureFill.RecordFetched(_tenures, results, r => r.MessageId, r => r.StreamId);
    return results;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<OutboxBatchRow>> ContinueOutboxStreamsAsync(
    IReadOnlyList<OutboxStreamCursor> streams,
    Guid instanceId,
    int runLength,
    long? maxBytes,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streams);
    if (streams.Count == 0) {
      return [];
    }

    var schema = _resolveSchema(typeof(OutboxRecord));
    // 579 (#2551): the continuation in emission order, after the last position the drain published.
    var functionName = BuildSchemaQualifiedName(schema, "wh_continue_outbox_streams_in_emission_order");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var cmd = __scope.Connection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_instance_id, @p_stream_ids, @p_after_positions, @p_run_length, @p_max_bytes)";
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streams.Select(s => s.StreamId).ToArray()
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_after_positions", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) {
      Value = streams.Select(s => s.LastPublishedPosition).ToArray()
    });
    cmd.Parameters.Add(new NpgsqlParameter("p_run_length", runLength));
    cmd.Parameters.Add(new NpgsqlParameter("p_max_bytes", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)maxBytes ?? DBNull.Value });

    // The function is new in 171 and returns fetch_outbox_batch's full column set, so there are no
    // older shapes to tolerate here.
    var results = new List<OutboxBatchRow>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(await _readOutboxRowAsync(reader, cancellationToken));
    }
    // #2556: as the fetch.
    TenureFill.RecordFetched(_tenures, results, r => r.MessageId, r => r.StreamId);
    return results;
  }

  /// <summary>
  /// One row of <c>fetch_outbox_batch_in_stream_order</c> or <c>wh_continue_outbox_streams_in_emission_order</c>, which
  /// share one column order.
  /// </summary>
  private static async Task<OutboxBatchRow> _readOutboxRowAsync(
      System.Data.Common.DbDataReader reader, CancellationToken cancellationToken) => new() {
        MessageId = reader.GetGuid(0),
        StreamId = await _valueOrNullAsync(reader, 1, reader.GetGuid, cancellationToken),
        Destination = await _stringOrNullAsync(reader, 2, cancellationToken),
        MessageType = reader.GetString(3),
        EnvelopeType = await _stringOrNullAsync(reader, 4, cancellationToken),
        EventData = reader.GetString(5),
        Metadata = reader.GetString(6),
        Scope = await _stringOrNullAsync(reader, 7, cancellationToken),
        Status = reader.GetInt32(8),
        Attempts = reader.GetInt32(9),
        PartitionNumber = await _valueOrNullAsync(reader, 10, reader.GetInt32, cancellationToken),
        IsEvent = reader.GetBoolean(11),
        CommitSequence = await _valueOrNullAsync(reader, 12, reader.GetInt64, cancellationToken),
        OriginServiceId = await _valueOrNullAsync(reader, 13, reader.GetGuid, cancellationToken),
        OriginCommitSequence = await _valueOrNullAsync(reader, 14, reader.GetInt64, cancellationToken),
        Error = await _stringOrNullAsync(reader, 15, cancellationToken),
        Priority = reader.GetInt32(16),
        StreamPosition = reader.GetInt64(17),
      };

  private static async Task<string?> _stringOrNullAsync(
      System.Data.Common.DbDataReader reader, int ordinal, CancellationToken cancellationToken) =>
    await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(ordinal);

  private static async Task<T?> _valueOrNullAsync<T>(
      System.Data.Common.DbDataReader reader, int ordinal, Func<int, T> read, CancellationToken cancellationToken)
      where T : struct =>
    await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) ? null : read(ordinal);

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

    var schema = _resolveSchema(typeof(OutboxRecord));
    // 576 (#2551): the fetch that ranks each stream in stream order and returns the rank.
    var functionName = BuildSchemaQualifiedName(schema, "fetch_inbox_batch_in_stream_order");

    var streamArr = AsArray(streamIds);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_ids, @p_instance_id, @p_max_per_stream, @p_max_bytes)";
    cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamArr });
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
    var errorOrdinal = _ordinalOrAbsent(reader, "error");
    // 149: the row's priority; a fetch_inbox_batch that predates the column leaves it undeclared, which the
    // dispatch worker reads as the standard band.
    var priorityOrdinal = _ordinalOrAbsent(reader, "priority");
    // 576 (#2551): the row's position in its stream.
    var streamPositionOrdinal = reader.GetOrdinal("stream_position");
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
        Error = errorOrdinal >= 0 && !await reader.IsDBNullAsync(errorOrdinal, cancellationToken).ConfigureAwait(false)
          ? reader.GetString(errorOrdinal)
          : null,
        Priority = priorityOrdinal >= 0 ? reader.GetInt32(priorityOrdinal) : 0,
        StreamPosition = reader.GetInt64(streamPositionOrdinal),
      });
    }
    // #2556: each fetched message is recorded under its stream's claimed tenure, which its result then names.
    TenureFill.RecordFetched(_tenures, results, r => r.MessageId, r => r.StreamId);
    return results;
  }

  /// <summary>
  /// The ordinal of <paramref name="name"/>, or -1 when the function this build talks to predates the column, so a
  /// mid-rollout package mix reads the row without the column instead of failing.
  /// </summary>
  private static int _ordinalOrAbsent(System.Data.Common.DbDataReader reader, string name) {
    try {
      return reader.GetOrdinal(name);
    } catch (IndexOutOfRangeException) {
      return -1;
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PendingPerspectiveEvent>> FetchPendingPerspectiveEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid instanceId,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(perspectiveName);

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "fetch_pending_perspective_events");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_id, @p_perspective_name, @p_instance_id)";
    cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_ID, streamId));
    cmd.Parameters.Add(new NpgsqlParameter("p_perspective_name", perspectiveName));
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));

    var results = new List<PendingPerspectiveEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new PendingPerspectiveEvent(
        EventWorkId: reader.GetGuid(0),
        EventId: reader.GetGuid(1),
        // Both functions return only rows whose commit_sequence IS NOT NULL (migration 042).
        CommitSequence: reader.GetInt64(2)));
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "claim_and_fetch_pending_perspective_events");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    var now = DateTime.UtcNow;
    var leaseExpiry = now + leaseDuration;

    await using var cmd = dbConnection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_id, @p_perspective_name, @p_instance_id, @p_lease_expiry, @p_now)";
    cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_ID, streamId));
    cmd.Parameters.Add(new NpgsqlParameter("p_perspective_name", perspectiveName));
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_lease_expiry", leaseExpiry));
    cmd.Parameters.Add(new NpgsqlParameter(P_NOW, now));

    var results = new List<PendingPerspectiveEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new PendingPerspectiveEvent(
        EventWorkId: reader.GetGuid(0),
        EventId: reader.GetGuid(1),
        // Both functions return only rows whose commit_sequence IS NOT NULL (migration 042).
        CommitSequence: reader.GetInt64(2)));
    }
    return results;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:FetchCollectiveSinkQueueAsync_ReturnsTheQueueInCommitOrderAsync</tests>
  public async Task<IReadOnlyList<CollectiveSinkQueueEntry>?> FetchCollectiveSinkQueueAsync(
    Guid streamId,
    CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "wh_collective_sink_queue");

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_id)";
    cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamId });

    var results = new List<CollectiveSinkQueueEntry>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new CollectiveSinkQueueEntry(
        EventWorkId: reader.GetGuid(0),
        EventId: reader.GetGuid(1),
        CommitSequence: await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(2)));
    }
    return results;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:FetchCollectiveSinkQueueHeadAsync_ReturnsTheHeadAsync</tests>
  public async Task<IReadOnlyList<CollectiveSinkQueueEntry>?> FetchCollectiveSinkQueueHeadAsync(
    Guid streamId,
    IReadOnlyCollection<Guid> throughWorkIds,
    CancellationToken cancellationToken = default) {
    var functionName = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_collective_sink_queue_head");
    return await _readCollectiveSinkQueueAsync(
      $"SELECT * FROM {functionName}(@p_stream_id, @p_through)",
      cmd => {
        cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamId });
        cmd.Parameters.Add(new NpgsqlParameter("p_through", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = throughWorkIds.ToArray() });
      },
      cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveSinkQueueSqlTests.cs:FetchCollectiveSinkQueueMembersAsync_ReturnsTheNamedRowsStillQueuedAsync</tests>
  public async Task<IReadOnlyList<CollectiveSinkQueueEntry>> FetchCollectiveSinkQueueMembersAsync(
    Guid streamId,
    IReadOnlyCollection<Guid> workIds,
    IReadOnlyCollection<Guid> eventIds,
    CancellationToken cancellationToken = default) {
    var functionName = BuildSchemaQualifiedName(_resolveSchema(typeof(OutboxRecord)), "wh_collective_sink_queue_members");
    return await _readCollectiveSinkQueueAsync(
      $"SELECT * FROM {functionName}(@p_stream_id, @p_work_ids, @p_event_ids)",
      cmd => {
        cmd.Parameters.Add(new NpgsqlParameter(P_STREAM_ID, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamId });
        cmd.Parameters.Add(new NpgsqlParameter("p_work_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = workIds.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter(P_EVENT_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = eventIds.ToArray() });
      },
      cancellationToken).ConfigureAwait(false);
  }

  // One read of collective sink queue rows (work id, event id, commit sequence), for the head and membership reads.
  private async Task<List<CollectiveSinkQueueEntry>> _readCollectiveSinkQueueAsync(
      string commandText, Action<NpgsqlCommand> bind, CancellationToken cancellationToken) {
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
    cmd.CommandText = commandText;
    bind(cmd);

    var results = new List<CollectiveSinkQueueEntry>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new CollectiveSinkQueueEntry(
        EventWorkId: reader.GetGuid(0),
        EventId: reader.GetGuid(1),
        CommitSequence: await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(2)));
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

    var schema = _resolveSchema(typeof(OutboxRecord));
    var functionName = BuildSchemaQualifiedName(schema, "fetch_events_by_ids");

    var idArr = AsArray(eventIds);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var dbConnection = __scope.Connection;
    await using var cmd = dbConnection.CreateCoordinatorCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_event_ids)";
    cmd.Parameters.Add(new NpgsqlParameter(P_EVENT_IDS, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArr });

    var results = new List<StreamEventData>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new StreamEventData {
        StreamId = reader.GetGuid(0),
        EventId = reader.GetGuid(1),
        EventType = reader.GetString(2),
        EventData = reader.GetString(3),
        // The body's event_data and metadata are both NOT NULL: metadata is null only with no body row, and then
        // reading event_data above has already failed.
        Metadata = reader.GetString(4),
        Scope = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
        EventWorkId = Guid.Empty
      });
    }
    return results;
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) =>
    _performMaintenanceAsync(null, cancellationToken);

  /// <inheritdoc />
  /// <remarks>Calls <c>wh_perform_maintenance</c> (migration 310) with the ids, in the one statement the unrestricted call runs.</remarks>
  public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(
      IReadOnlyCollection<Guid>? ephemeralReapEventIds, CancellationToken cancellationToken = default) =>
    _performMaintenanceAsync(ephemeralReapEventIds, cancellationToken);

  private async Task<IReadOnlyList<MaintenanceResult>> _performMaintenanceAsync(
      IReadOnlyCollection<Guid>? ephemeralReapEventIds, CancellationToken cancellationToken) {
    var schema = _resolveSchema(typeof(OutboxRecord));

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var connection = __scope.Connection;

    await using var command = connection.CreateCoordinatorCommand();
    // Always qualified, the default schema included: the function is called by its schema's name, never through
    // search_path. Shared with the Dapper coordinator (#2039).
    return await Whizbang.Data.Postgres.EphemeralDestructionSql.PerformMaintenanceAsync(
      command, Whizbang.Data.Postgres.PgIdentifier.Quote(schema) + ".", ephemeralReapEventIds, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<PurgedOrphanInboxRow>> PurgeOrphanInboxAsync(
      IReadOnlyList<string> handledTypeNames,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(handledTypeNames);

    var schema = _resolveSchema(typeof(InboxRecord));

    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var connection = __scope.Connection;

    await using var command = connection.CreateCoordinatorCommand();
    command.CommandText = $"SELECT * FROM \"{schema}\".purge_orphan_inbox(@handled_types)";
    var param = command.CreateParameter();
    param.ParameterName = "handled_types";
    param.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text;
    param.Value = AsArray(handledTypeNames);
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

  /// <inheritdoc />
  public async Task<IReadOnlyList<string>> GetPendingInboxMessageTypesAsync(CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(InboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var command = __scope.Connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // Schema-qualified table names built from a validated schema constant.
    command.CommandText = Whizbang.Data.Postgres.PendingInboxTypesSql.Query($"\"{schema}\".");
#pragma warning restore S2077
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    var types = new List<string>();
    while (await reader.ReadAsync(cancellationToken)) {
      types.Add(reader.GetString(0));
    }
    return types;
  }

  /// <inheritdoc />
  public Task<long> DiscardPendingInboxMessagesAsync(
      IReadOnlyList<string> messageTypeNames,
      CancellationToken cancellationToken = default)
    // 162: the inbox variant can no longer share the outbox's predicate. message_type is a property
    // of the message and stays on wh_inbox; processed_at and instance_id are work state and moved to
    // wh_inbox_state, so the predicate now spans both tables. The DELETE stays on wh_inbox because
    // that is the row being removed, and its state row goes with it through ON DELETE CASCADE.
    // USING keeps this one statement. Mirrors DapperWorkCoordinator's DISCARD_PENDING_INBOX_SQL --
    // both coordinators implement this sweep, and only one of them was redirected the first time.
    => _discardPendingAsync(
      static schema =>
        $"DELETE FROM \"{schema}\".wh_inbox r USING \"{schema}\".wh_inbox_state s "
        + "WHERE s.message_id = r.message_id AND s.processed_at IS NULL AND s.instance_id IS NULL "
        + "AND EXISTS (SELECT 1 FROM unnest(@type_names) AS t(name) WHERE strpos(r.message_type, t.name) > 0)",
      typeof(InboxRecord), messageTypeNames, cancellationToken);

  /// <inheritdoc />
  public Task<long> DiscardPendingOutboxMessagesAsync(
      IReadOnlyList<string> messageTypeNames,
      CancellationToken cancellationToken = default)
    => _discardPendingAsync(
      static schema =>
        $"DELETE FROM \"{schema}\".{OUTBOX_TABLE} r "
        + "WHERE r.processed_at IS NULL AND r.instance_id IS NULL "
        + "AND EXISTS (SELECT 1 FROM unnest(@type_names) AS t(name) WHERE strpos(r.message_type, t.name) > 0)",
      typeof(OutboxRecord), messageTypeNames, cancellationToken);

  /// <inheritdoc />
  public async Task<IReadOnlySet<Guid>?> GetStreamsWithPendingMessagesAsync(
      IReadOnlyList<Guid> streamIds,
      IReadOnlyList<string> messageTypeNames,
      CancellationToken cancellationToken = default) {
    var schema = _resolveSchema(typeof(OutboxRecord));
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    await using var command = __scope.Connection.CreateCoordinatorCommand();
    return await Whizbang.Data.Postgres.StreamsWithPendingMessagesSql.ExecuteAsync(
      command, schema, streamIds, messageTypeNames, cancellationToken);
  }

  /// <summary>
  /// The maintenance sweep behind "a feature that is off leaves nothing behind", for one table.
  /// Containment, not equality: a stored message_type may carry assembly version metadata or an envelope
  /// wrapper around the normalized name. Unleased only: a leased row is mid-flight and its own seam
  /// (the dispatch worker for the inbox, the publisher for the outbox) applies the same mode check.
  /// </summary>
  /// <param name="buildSql">
  /// Renders the statement for the resolved schema. The two tables no longer share a predicate, so
  /// the caller supplies the whole statement rather than a table name substituted into one shape.
  /// </param>
  /// <param name="entityType">The entity whose mapped schema the statement runs in.</param>
  private async Task<long> _discardPendingAsync(
      Func<string, string> buildSql, [DynamicallyAccessedMembers(EntityTypeTrimming.MEMBERS)] Type entityType, IReadOnlyList<string> messageTypeNames, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(buildSql);
    ArgumentNullException.ThrowIfNull(messageTypeNames);
    if (messageTypeNames.Count == 0) {
      return 0;
    }
    var schema = _resolveSchema(entityType);
    await using var __scope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
        (Npgsql.NpgsqlConnection)_dbContext.Database.GetDbConnection(), cancellationToken);
    var connection = __scope.Connection;
    await using var command = connection.CreateCoordinatorCommand();
    command.CommandText = buildSql(schema);
    var param = Whizbang.Data.Postgres.PostgresArrayHelper.ToVarcharArray([.. messageTypeNames]);
    param.ParameterName = "type_names";
    command.Parameters.Add(param);
    return await command.ExecuteNonQueryAsync(cancellationToken);
  }
}

/// <summary>
/// Internal DTO for mapping claim_work function results.
/// Matches the function's return type structure.
/// </summary>
internal class WorkBatchRow {
  /// <summary>150: the inbox row's effective priority; null for other sources.</summary>
  [Column("priority")]
  public int? Priority { get; set; }

  /// <summary>150: the inbox row's arrival; null for other sources.</summary>
  [Column("received_at")]
  public DateTimeOffset? ReceivedAt { get; set; }

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

  /// <summary>570 (#2556): the epoch this instance held the row's partition at when it claimed it; null off map mode.</summary>
  public long? PartitionEpoch { get; set; }

  /// <summary>570 (#2556): the partition the epoch is of: the row's, or its stream's when the row names none.</summary>
  public int? TenurePartition { get; set; }
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

  [Column("replayed_through_commit_sequence")]
  public long? ReplayedThroughCommitSequence { get; set; }
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
/// <summary>Notices <c>claim_work</c> raises for the coordinator to act on.</summary>
internal static class OutboxClaimNotices {
  /// <summary>Raised when the outbox acquisition leased its whole row bound (171).</summary>
  internal const string ACQUISITION_FULL = "whizbang.outbox_acquisition_full=true";

  /// <summary>Raised when the calling instance's registration row is missing or stale (196).</summary>
  internal const string REGISTRATION_STALE = "whizbang.instance_registration_stale=true";

  /// <summary>203 (#1254): the claim was handed a partition assignment version that is no longer published or leased.</summary>
  internal const string PARTITION_ASSIGNMENT_STALE = "whizbang.partition_assignment_stale=true";
}


internal static partial class EFCoreWorkCoordinatorLog {
  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Failed to parse reconciliation scope JSON; the replayed lifecycle event will run without its stored scope.")]
  public static partial void ReconcileScopeParseFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 74, Level = LogLevel.Warning,
    Message = "Handler-commit batch of {HandlerCount} fell back from the bulk tier to per-handler savepoints: {BulkError} — "
            + "sustained fallbacks mean every commit pays the slow path; the SQLSTATE names why")]
  public static partial void CommitBulkTierFellBack(ILogger logger, int handlerCount, string bulkError);

  [LoggerMessage(EventId = 75, Level = LogLevel.Warning,
    Message = "Row-retention declaration for {ClrTypeName} matched no wh_perspective_registry row: the perspective is NOT enrolled. "
            + "The registry key is the CLR type name (Outer+Model for a nested model); a row keyed in another form is a key drift")]
  public static partial void RetentionDeclarationMatchedNoRegistryRow(ILogger logger, string clrTypeName);

  [LoggerMessage(EventId = 76, Level = LogLevel.Warning,
    Message = "Table {Table} was not rewritten: {Reason}")]
  public static partial void TableNotRewritten(ILogger logger, string table, string reason);

  [LoggerMessage(EventId = 77, Level = LogLevel.Warning,
    Message = "Index {Index} was not {Action}: {Reason}")]
  public static partial void QueueIndexNotTouched(ILogger logger, string index, string action, string reason);
}
