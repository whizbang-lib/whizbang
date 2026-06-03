using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
  WorkCoordinatorGate? gate = null
) : IWorkCoordinator
  where TDbContext : DbContext {
  private const string DEFAULT_SCHEMA = "public";
  private const string PERSPECTIVE_CURSORS_TABLE = "wh_perspective_cursors";
  private const string PARAM_INSTANCE_ID = "p_instance_id";

  private readonly TDbContext _dbContext = _initDbContext(dbContext);
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<EFCoreWorkCoordinator<TDbContext>>? _logger = logger;
  private readonly WorkCoordinatorMetrics? _metrics = metrics;
  private readonly WorkCoordinatorGate? _gate = gate;

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

  private string _serializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) { return "[]"; }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageCompletion[]. Ensure the type is registered.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
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
    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_ids, @p_debug_mode)";
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_debug_mode", NpgsqlTypes.NpgsqlDbType.Boolean) { Value = debugMode });
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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
    var perspectiveStreamIds = rows
      .Where(r => r.Source == "perspective_stream" && r.StreamId.HasValue)
      .Select(r => r.StreamId!.Value)
      .ToList();
    var outboxStreamIds = rows
      .Where(r => r.Source == "outbox")
      .Select(r => r.StreamId ?? r.WorkId ?? Guid.Empty)
      .Where(g => g != Guid.Empty)
      .Distinct()
      .ToList();
    var inboxStreamIds = rows
      .Where(r => r.Source == "inbox")
      .Select(r => r.StreamId ?? r.WorkId ?? Guid.Empty)
      .Where(g => g != Guid.Empty)
      .Distinct()
      .ToList();

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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT {functionName}(@p_category, @p_ids, @p_lease)";
    cmd.Parameters.Add(new NpgsqlParameter("p_category", NpgsqlTypes.NpgsqlDbType.Text) { Value = category.ToSqlCategory() });
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = idArray });
    cmd.Parameters.Add(new NpgsqlParameter("p_lease", NpgsqlTypes.NpgsqlDbType.Integer) { Value = leaseSeconds });
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
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

    var connection = _dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken);
    }
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

    var conn = _dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken);
    }
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

    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

    var eventStoreTable = BuildSchemaQualifiedName(schema, "wh_event_store");

    await using var cmd = (Npgsql.NpgsqlCommand)dbConnection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table names built from validated schema constant
    // Slice 26.13: LEFT JOIN wh_event_store so cold-cache cursor prefetch can warm the
    // commit_sequence half of PerspectiveCursorCache. Without it, the inversion detector
    // falls back to event_id (UUIDv7 lex) comparison and re-introduces same-millisecond
    // generation-vs-commit-order false positives (JDX run 11 surfaced 1,403 such logs).
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

  /// <summary>
  /// Handles PostgreSQL RAISE DEBUG messages by logging them at Debug level.
  /// Notices are only generated when WorkBatchOptions.DebugMode is set in the SQL function.
  /// </summary>
  private void _onNotice(object? sender, NpgsqlNoticeEventArgs args) {
    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var severity = args.Notice.Severity;
      var message = args.Notice.MessageText;
      _logger.LogDebug("PostgreSQL Notice [{Severity}]: {Message}",
        severity, message);
    }
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
        SELECT e.event_id, e.stream_id, e.event_data, e.metadata, e.event_type, e.scope
        FROM {eventStoreTable} e
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
    } catch {
      // Scope parsing is best-effort for reconciliation
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

    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

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

    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

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

    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

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
    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }
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
    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

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
    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

    await using var cmd = (NpgsqlCommand)dbConnection.CreateCommand();
    cmd.CommandText = $"SELECT * FROM {functionName}(@p_stream_ids, @p_instance_id, @p_max_per_stream)";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) { Value = streamArr });
    cmd.Parameters.Add(new NpgsqlParameter(PARAM_INSTANCE_ID, instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_max_per_stream", maxPerStream));

    var results = new List<InboxBatchRow>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
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
        IsEvent = reader.GetBoolean(10)
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

    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

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

    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

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
    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

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

    var connection = _dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await _dbContext.Database.OpenConnectionAsync(cancellationToken);
    }

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

    var connection = _dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await _dbContext.Database.OpenConnectionAsync(cancellationToken);
    }

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
