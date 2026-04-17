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
public class EFCoreWorkCoordinator<TDbContext>(
  TDbContext dbContext,
  JsonSerializerOptions jsonOptions,
  ILogger<EFCoreWorkCoordinator<TDbContext>>? logger = null,
  WorkCoordinatorMetrics? metrics = null
) : IWorkCoordinator
  where TDbContext : DbContext {
  private const string DEFAULT_SCHEMA = "public";

  private readonly TDbContext _dbContext = _initDbContext(dbContext);
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<EFCoreWorkCoordinator<TDbContext>>? _logger = logger;

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
  internal static string BuildSchemaQualifiedName(string schema, string identifier) {
    // CRITICAL: Never produce a leading dot
    if (string.IsNullOrWhiteSpace(schema) || schema == DEFAULT_SCHEMA) {
      return identifier;
    }
    // Quote schema name to handle PostgreSQL reserved words
    return $"\"{schema}\".{identifier}";
  }

  public async Task<WorkBatch> ProcessWorkBatchAsync(
    ProcessWorkBatchRequest request,
    CancellationToken cancellationToken = default
  ) {
    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var instanceId = request.InstanceId;
      var serviceName = request.ServiceName;
      var hostName = request.HostName;
      var processId = request.ProcessId;
      var outboxCompletionsLength = request.OutboxCompletions.Length;
      var outboxFailuresLength = request.OutboxFailures.Length;
      var inboxCompletionsLength = request.InboxCompletions.Length;
      var inboxFailuresLength = request.InboxFailures.Length;
      var newOutboxLength = request.NewOutboxMessages.Length;
      var newInboxLength = request.NewInboxMessages.Length;
      var flags = request.Flags;
      _logger.LogDebug(
        "Processing work batch for instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}): {OutboxCompletions} outbox completions, {OutboxFailures} outbox failures, {InboxCompletions} inbox completions, {InboxFailures} inbox failures, {NewOutbox} new outbox, {NewInbox} new inbox, Flags={Flags}",
        instanceId,
        serviceName,
        hostName,
        processId,
        outboxCompletionsLength,
        outboxFailuresLength,
        inboxCompletionsLength,
        inboxFailuresLength,
        newOutboxLength,
        newInboxLength,
        flags
      );
    }

    // Convert to JSONB parameters
    var outboxCompletionsJson = _serializeCompletions(request.OutboxCompletions);
    var outboxFailuresJson = _serializeFailures(request.OutboxFailures);
    var inboxCompletionsJson = _serializeCompletions(request.InboxCompletions);
    var inboxFailuresJson = _serializeFailures(request.InboxFailures);
    var perspectiveCompletionsJson = _serializePerspectiveCompletions(request.PerspectiveCompletions);
    var perspectiveFailuresJson = _serializePerspectiveFailures(request.PerspectiveFailures);
    var newOutboxJson = _serializeNewOutboxMessages(request.NewOutboxMessages);
    var newInboxJson = _serializeNewInboxMessages(request.NewInboxMessages);
    var metadataJson = _serializeMetadata(request.Metadata);
    var renewOutboxJson = _serializeLeaseRenewals(request.RenewOutboxLeaseIds);
    var renewInboxJson = _serializeLeaseRenewals(request.RenewInboxLeaseIds);

    var outboxCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(outboxCompletionsJson);
    outboxCompletionsParam.ParameterName = "p_outbox_completions";

    var outboxFailuresParam = PostgresJsonHelper.JsonStringToJsonb(outboxFailuresJson);
    outboxFailuresParam.ParameterName = "p_outbox_failures";

    var inboxCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(inboxCompletionsJson);
    inboxCompletionsParam.ParameterName = "p_inbox_completions";

    var inboxFailuresParam = PostgresJsonHelper.JsonStringToJsonb(inboxFailuresJson);
    inboxFailuresParam.ParameterName = "p_inbox_failures";


    var newOutboxParam = PostgresJsonHelper.JsonStringToJsonb(newOutboxJson);
    newOutboxParam.ParameterName = "p_new_outbox_messages";

    var newInboxParam = PostgresJsonHelper.JsonStringToJsonb(newInboxJson);
    newInboxParam.ParameterName = "p_new_inbox_messages";

    var metadataParam = PostgresJsonHelper.JsonStringToJsonb(metadataJson);
    metadataParam.ParameterName = "p_metadata";

    var renewOutboxParam = PostgresJsonHelper.JsonStringToJsonb(renewOutboxJson);
    renewOutboxParam.ParameterName = "p_renew_outbox_lease_ids";

    var renewInboxParam = PostgresJsonHelper.JsonStringToJsonb(renewInboxJson);
    renewInboxParam.ParameterName = "p_renew_inbox_lease_ids";

    var perspectiveEventCompletionsJson = _serializePerspectiveEventCompletions(request.PerspectiveEventCompletions);
    var perspectiveEventCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(perspectiveEventCompletionsJson);
    perspectiveEventCompletionsParam.ParameterName = "p_perspective_event_completions";

    var perspectiveCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(perspectiveCompletionsJson);
    perspectiveCompletionsParam.ParameterName = "p_perspective_completions";

    var perspectiveEventFailuresParam = PostgresJsonHelper.JsonStringToJsonb("[]");
    perspectiveEventFailuresParam.ParameterName = "p_perspective_event_failures";

    var perspectiveFailuresParam = PostgresJsonHelper.JsonStringToJsonb(perspectiveFailuresJson);
    perspectiveFailuresParam.ParameterName = "p_perspective_failures";

    var newPerspectiveEventsParam = PostgresJsonHelper.JsonStringToJsonb("[]");
    newPerspectiveEventsParam.ParameterName = "p_new_perspective_events";

    var renewPerspectiveEventLeaseIdsParam = PostgresJsonHelper.JsonStringToJsonb("[]");
    renewPerspectiveEventLeaseIdsParam.ParameterName = "p_renew_perspective_event_lease_ids";

    var syncInquiriesJson = _serializeSyncInquiries(request.PerspectiveSyncInquiries);
    var syncInquiriesParam = PostgresJsonHelper.JsonStringToJsonb(syncInquiriesJson);
    syncInquiriesParam.ParameterName = "p_sync_inquiries";

    var maxStreamsParam = new Npgsql.NpgsqlParameter("p_max_streams", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = request.MaxStreamsPerBatch
    };

    var now = DateTimeOffset.UtcNow;

    // CRITICAL: Get schema from DbContext model to schema-qualify the function call
    // Functions are database-wide in PostgreSQL - multiple schemas sharing a database
    // must use schema-qualified function names to avoid calling the wrong function
    var rawSchema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    var schema = GetSchemaWithFallback(rawSchema, DEFAULT_SCHEMA, _logger);
    var functionName = BuildSchemaQualifiedName(schema, "process_work_batch");

    // DIAGNOSTIC: Log schema resolution for troubleshooting multi-schema deployments
    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var rawSchemaStr = rawSchema ?? "(null)";
      _logger.LogDebug(
        "Schema resolution: rawSchema='{RawSchema}', schema='{Schema}', functionName='{FunctionName}'",
        rawSchemaStr, schema, functionName);
    }

    // Execute the process_work_batch function (new signature after decomposition)
    var sql = $@"
      SELECT * FROM {functionName}(
        @p_instance_id,
        @p_service_name,
        @p_host_name,
        @p_process_id,
        @p_metadata,
        @p_now,
        @p_lease_duration_seconds,
        @p_partition_count,
        @p_outbox_completions,
        @p_inbox_completions,
        @p_perspective_event_completions,
        @p_perspective_completions,
        @p_outbox_failures,
        @p_inbox_failures,
        @p_perspective_event_failures,
        @p_perspective_failures,
        @p_new_outbox_messages,
        @p_new_inbox_messages,
        @p_new_perspective_events,
        @p_renew_outbox_lease_ids,
        @p_renew_inbox_lease_ids,
        @p_renew_perspective_event_lease_ids,
        @p_flags,
        @p_stale_threshold_seconds,
        @p_sync_inquiries,
        @p_max_streams
      )";

    // Hook PostgreSQL RAISE DEBUG messages for debugging
    // Access the underlying NpgsqlConnection from EF Core's DbContext
    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection is NpgsqlConnection npgsqlConnection && npgsqlConnection.State != System.Data.ConnectionState.Open) {
      // Wire up notice handler if not already connected
      npgsqlConnection.Notice += _onNotice;
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    List<WorkBatchRow> results;
    try {
      results = await _dbContext.Database
        .SqlQueryRaw<WorkBatchRow>(
          sql,
          new Npgsql.NpgsqlParameter("p_instance_id", request.InstanceId),
          new Npgsql.NpgsqlParameter("p_service_name", request.ServiceName),
          new Npgsql.NpgsqlParameter("p_host_name", request.HostName),
          new Npgsql.NpgsqlParameter("p_process_id", request.ProcessId),
          metadataParam,
          new Npgsql.NpgsqlParameter("p_now", now),
          new Npgsql.NpgsqlParameter("p_lease_duration_seconds", request.LeaseSeconds),
          new Npgsql.NpgsqlParameter("p_partition_count", request.PartitionCount),
          outboxCompletionsParam,
          inboxCompletionsParam,
          perspectiveEventCompletionsParam,
          perspectiveCompletionsParam,
          outboxFailuresParam,
          inboxFailuresParam,
          perspectiveEventFailuresParam,
          perspectiveFailuresParam,
          newOutboxParam,
          newInboxParam,
          newPerspectiveEventsParam,
          renewOutboxParam,
          renewInboxParam,
          renewPerspectiveEventLeaseIdsParam,
          new Npgsql.NpgsqlParameter("p_flags", (int)request.Flags),
          new Npgsql.NpgsqlParameter("p_stale_threshold_seconds", request.StaleThresholdSeconds),
          syncInquiriesParam,
          maxStreamsParam
        )
        .ToListAsync(cancellationToken);
    } catch (Exception ex) {
      metrics?.ProcessBatchErrors.Add(1, new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      metrics?.ProcessBatchDuration.Record(sw.Elapsed.TotalMilliseconds);
      metrics?.ProcessBatchCalls.Add(1);
    }

    // Record batch composition metrics
    metrics?.BatchOutboxMessages.Record(request.NewOutboxMessages.Length);
    metrics?.BatchInboxMessages.Record(request.NewInboxMessages.Length);
    metrics?.BatchCompletions.Record(request.OutboxCompletions.Length + request.InboxCompletions.Length);
    metrics?.BatchFailures.Record(request.OutboxFailures.Length + request.InboxFailures.Length);

    // Process results and return work batch
    var workBatch = _processResults(results);

    // Record returned work metrics
    metrics?.ReturnedOutboxWork.Record(workBatch.OutboxWork.Count);
    metrics?.ReturnedInboxWork.Record(workBatch.InboxWork.Count);
    metrics?.ReturnedPerspectiveWork.Record(workBatch.PerspectiveWork.Count);

    return workBatch;
  }

  /// <summary>
  /// Processes the query results and maps them to a WorkBatch
  /// </summary>
  private WorkBatch _processResults(List<WorkBatchRow> results) {

    // Check for storage failure rows (error tracking from Phase 4.5)
    var errorRows = results.Where(r => r.Error != null && r.FailureReason.HasValue).ToList();
    if (errorRows.Count > 0) {
      _logger?.LogError(
        "Event storage failures detected: {FailureCount} rows with errors",
        errorRows.Count
      );

      foreach (var errorRow in errorRows) {
        _logger?.LogError(
          "Event storage failure: WorkId={WorkId}, Source={Source}, Reason={Reason}, Error={Error}",
          errorRow.WorkId,
          errorRow.Source,
          (MessageFailureReason)(errorRow.FailureReason ?? (int)MessageFailureReason.Unknown),
          errorRow.Error
        );
      }

      // Currently we don't throw - messages remain in tables with failed status
      // Future: Could make this configurable (fail-fast vs. continue)
    }

    // Filter out error rows from further processing
    var validResults = results.Where(r => r.Error == null).ToList();

    // Map results to WorkBatch - deserialize envelopes from database
    var outboxWork = validResults
      .Where(r => r.Source == "outbox")
      .Select(_mapOutboxWork)
      .ToList();

    var inboxWork = validResults
      .Where(r => r.Source == "inbox")
      .Select(_mapInboxWork)
      .ToList();

    // Drain mode: SQL returns 'perspective_stream' rows (one per distinct stream, no per-event detail).
    // Legacy: SQL returns 'perspective' rows (one per event with perspective_name).
    var perspectiveStreamRows = validResults
      .Where(r => r.Source == "perspective_stream")
      .ToList();

    var perspectiveRows = validResults
      .Where(r => r.Source == "perspective")
      .ToList();

    // Drain mode: stream IDs from dedicated rows (skip PerspectiveWork construction entirely)
    // Legacy: deduplicate stream IDs from per-event rows
    var perspectiveStreamIds = perspectiveStreamRows.Count > 0
      ? perspectiveStreamRows.Where(r => r.StreamId.HasValue).Select(r => r.StreamId!.Value).ToList()
      : perspectiveRows.Where(r => r.StreamId.HasValue).Select(r => r.StreamId!.Value).Distinct().ToList();

    var perspectiveWork = perspectiveStreamRows.Count > 0
      ? new List<PerspectiveWork>()
      : perspectiveRows.Select(_mapPerspectiveWork).ToList();

    var syncInquiryResults = validResults
      .Where(r => r.Source == "sync_result")
      .Select(r => new SyncInquiryResult {
        InquiryId = r.WorkId!.Value,
        StreamId = r.StreamId ?? Guid.Empty,
        PendingCount = r.PartitionNumber ?? 0,
        ProcessedCount = r.Status ?? 0,
        PendingEventIds = _parsePendingEventIds(r.MessageData),
        ProcessedEventIds = _parseProcessedEventIds(r.Metadata)
      })
      .ToList();

    // Only log when there's actual work to report
    if ((outboxWork.Count > 0 || inboxWork.Count > 0 || perspectiveWork.Count > 0 || syncInquiryResults.Count > 0) &&
        _logger?.IsEnabled(LogLevel.Debug) == true) {
      var outboxCount = outboxWork.Count;
      var inboxCount = inboxWork.Count;
      var perspectiveCount = perspectiveWork.Count;
      var syncResultsCount = syncInquiryResults.Count;
      _logger.LogDebug(
        "Work batch processed: {OutboxWork} outbox work, {InboxWork} inbox work, {PerspectiveWork} perspective work, {SyncResults} sync results",
        outboxCount,
        inboxCount,
        perspectiveCount,
        syncResultsCount
      );
    }

    return new WorkBatch {
      OutboxWork = outboxWork,
      InboxWork = inboxWork,
      PerspectiveWork = perspectiveWork,
      PerspectiveStreamIds = perspectiveStreamIds,
      SyncInquiryResults = syncInquiryResults.Count > 0 ? syncInquiryResults : null
    };
  }

  private OutboxWork _mapOutboxWork(WorkBatchRow r) {
    var envelope = _deserializeEnvelope(r.MessageType!, r.MessageData!);
    var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.WorkId}");

    var messageType = !string.IsNullOrWhiteSpace(r.MessageType)
      ? r.MessageType
      : _extractMessageTypeFromEnvelopeType(r.EnvelopeType!);

    return new OutboxWork {
      MessageId = r.WorkId!.Value,
      Destination = r.Destination!,
      Envelope = jsonEnvelope,
      EnvelopeType = r.EnvelopeType!,
      MessageType = messageType,
      StreamId = r.StreamId,
      PartitionNumber = r.PartitionNumber,
      Attempts = r.Attempts ?? 0,
      Status = (MessageProcessingStatus)(r.Status ?? 0),
      Flags = _buildFlags(r),
      Metadata = _parseMetadataJson(r)
    };
  }

  private InboxWork _mapInboxWork(WorkBatchRow r) {
    if (string.IsNullOrWhiteSpace(r.MessageType)) {
      throw new InvalidOperationException(
        $"Inbox message {r.WorkId} has null/empty message_type. " +
        "This indicates the message was not properly serialized by the transport consumer. " +
        "Ensure ServiceBusConsumerWorker or equivalent is correctly populating MessageType.");
    }

    var envelope = _deserializeEnvelope(r.MessageType, r.MessageData!);
    var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.WorkId}");

    return new InboxWork {
      MessageId = r.WorkId!.Value,
      Envelope = jsonEnvelope,
      MessageType = r.MessageType,
      StreamId = r.StreamId,
      PartitionNumber = r.PartitionNumber,
      Attempts = r.Attempts ?? 0,
      Status = (MessageProcessingStatus)(r.Status ?? 0),
      Flags = _buildFlags(r),
      Metadata = _parseMetadataJson(r)
    };
  }

  private PerspectiveWork _mapPerspectiveWork(WorkBatchRow r) {
    return new PerspectiveWork {
      WorkId = r.WorkId ?? Guid.Empty,  // NULL in stream assignment model (drain mode) — worker uses PerspectiveStreamIds instead
      StreamId = r.StreamId ?? throw new InvalidOperationException("Perspective work must have StreamId"),
      PerspectiveName = r.PerspectiveName ?? throw new InvalidOperationException("Perspective work must have PerspectiveName"),
      LastProcessedEventId = null,
      Status = (PerspectiveProcessingStatus)(r.Status ?? 0),
      PartitionNumber = r.PartitionNumber,
      Flags = _buildFlags(r),
      Metadata = _parseMetadataJson(r)
    };
  }

  private static WorkBatchOptions _buildFlags(WorkBatchRow r) {
    var flags = WorkBatchOptions.None;
    if (r.IsNewlyStored == true) {
      flags |= WorkBatchOptions.NewlyStored;
    }
    if (r.IsOrphaned == true) {
      flags |= WorkBatchOptions.Orphaned;
    }
    return flags;
  }

  private Dictionary<string, JsonElement>? _parseMetadataJson(WorkBatchRow r) {
    if (string.IsNullOrWhiteSpace(r.Metadata)) {
      return null;
    }

    try {
      var metadataDoc = JsonDocument.Parse(r.Metadata);
      return metadataDoc.RootElement.EnumerateObject()
        .ToDictionary(p => p.Name, p => p.Value.Clone());
    } catch (JsonException ex) {
      _logger?.LogWarning(ex, "Failed to parse metadata JSON for work item {WorkId}", r.WorkId);
      return null;
    }
  }

  /// <summary>
  /// Serializes message completions to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
  private string _serializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <summary>
  /// Serializes message failures to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync</tests>
  private string _serializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  /// <summary>
  /// Serializes new outbox messages to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
  private string _serializeNewOutboxMessages(OutboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(OutboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for OutboxMessage[]. Ensure the type is registered in InfrastructureJsonContext.");
    var json = JsonSerializer.Serialize(messages, typeInfo);

    // Log the first message for debugging
    if (messages.Length > 0) {
      // OutboxMessage is non-generic - access properties directly
      var firstMessage = messages[0];

      if (_logger?.IsEnabled(LogLevel.Debug) == true) {
        var messageId = firstMessage.MessageId;
        var destination = firstMessage.Destination;
        var envelopeType = firstMessage.EnvelopeType;
        var hopsCount = firstMessage.Envelope.Hops?.Count ?? 0;
        _logger.LogDebug("Serializing outbox message: MessageId={MessageId}, Destination={Destination}, EnvelopeType={EnvelopeType}, HopsCount={HopsCount}",
          messageId, destination, envelopeType, hopsCount);
        var jsonPreview = json.Length > 500 ? json[..500] + "..." : json;
        _logger.LogDebug("First outbox message JSON (first 500 chars): {Json}", jsonPreview);
      }
    }

    return json;
  }

  /// <summary>
  /// Serializes new inbox messages to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
  private string _serializeNewInboxMessages(InboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for InboxMessage[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  /// <summary>
  /// Serializes metadata dictionary to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithMetadata_StoresMetadataCorrectlyAsync</tests>
  private string _serializeMetadata(Dictionary<string, JsonElement>? metadata) {
    if (metadata == null || metadata.Count == 0) {
      return "{}";  // Return empty JSON object instead of null (matches NOT NULL constraint)
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(metadata, typeInfo);
  }

  /// <summary>
  /// Serializes lease renewal message IDs to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string _serializeLeaseRenewals(Guid[] messageIds) {
    if (messageIds.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Guid[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messageIds, typeInfo);
  }

  /// <summary>
  /// Serializes perspective cursor completions to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string _serializePerspectiveEventCompletions(PerspectiveEventCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveEventCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveEventCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string _serializePerspectiveCompletions(PerspectiveCursorCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCursorCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCursorCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <summary>
  /// Serializes perspective cursor failures to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string _serializePerspectiveFailures(PerspectiveCursorFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCursorFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCursorFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  /// <summary>
  /// Serializes perspective sync inquiries to JSON for database storage.
  /// </summary>
  /// <docs>fundamentals/perspectives/perspective-sync</docs>
  private string _serializeSyncInquiries(SyncInquiry[]? inquiries) {
    if (inquiries == null || inquiries.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(SyncInquiry[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for SyncInquiry[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(inquiries, typeInfo);
  }

  /// <summary>
  /// Parses pending event IDs from the message_data JSON array.
  /// </summary>
  private Guid[]? _parsePendingEventIds(string? messageData) {
    if (string.IsNullOrWhiteSpace(messageData)) {
      return null;
    }

    try {
      var typeInfo = _jsonOptions.GetTypeInfo(typeof(Guid[]))
        ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]. Ensure the type is registered in InfrastructureJsonContext.");
      var ids = JsonSerializer.Deserialize(messageData, typeInfo) as Guid[];
      return ids;
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Parses processed event IDs from the metadata JSON object.
  /// SQL returns: {"processed_event_ids": [...]}
  /// </summary>
  private static Guid[]? _parseProcessedEventIds(string? metadata) {
    if (string.IsNullOrWhiteSpace(metadata)) {
      return null;
    }

    try {
      using var doc = JsonDocument.Parse(metadata);
      if (!doc.RootElement.TryGetProperty("processed_event_ids", out var idsElement)) {
        return null;
      }

      var ids = new List<Guid>();
      foreach (var element in idsElement.EnumerateArray()) {
        if (element.TryGetGuid(out var id)) {
          ids.Add(id);
        }
      }
      return ids.Count > 0 ? [.. ids] : [];
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Deserializes envelope from database envelope_type and envelope_data columns.
  /// Always deserializes as MessageEnvelope&lt;JsonElement&gt; for AOT-compatible, type-safe serialization.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
  private IMessageEnvelope _deserializeEnvelope(string envelopeTypeName, string envelopeDataJson) {
    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var dataPreview = envelopeDataJson.Length > 500 ? envelopeDataJson[..500] + "..." : envelopeDataJson;
      _logger.LogDebug("Deserializing envelope: Type={EnvelopeType}, Data (first 500 chars)={EnvelopeData}",
        envelopeTypeName, dataPreview);
    }

    // Always deserialize as MessageEnvelope<JsonElement> for AOT compatibility
    // This eliminates the need for Type.GetType() and runtime type resolution
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageEnvelope<JsonElement>. Ensure it is registered via JsonContextRegistry.");

    // Deserialize the complete envelope as MessageEnvelope<JsonElement>
    var envelope = JsonSerializer.Deserialize(envelopeDataJson, typeInfo) as IMessageEnvelope
      ?? throw new InvalidOperationException("Failed to deserialize envelope as MessageEnvelope<JsonElement>");

    if (_logger?.IsEnabled(LogLevel.Debug) == true) {
      var messageId = envelope.MessageId;
      var hopsCount = envelope.Hops?.Count ?? 0;
      _logger.LogDebug("Deserialized envelope: MessageId={MessageId}, HopsCount={HopsCount}",
        messageId, hopsCount);
    }

    return envelope;
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
  public async Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) {
    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);

    var sql = $@"SELECT
      (SELECT COUNT(*) FROM {schema}.wh_perspective_events WHERE processed_at IS NULL)::bigint as ""PendingPerspectiveEvents"",
      (SELECT COUNT(*) FROM {schema}.wh_outbox WHERE processed_at IS NULL)::bigint as ""PendingOutbox"",
      (SELECT COUNT(*) FROM {schema}.wh_inbox WHERE processed_at IS NULL)::bigint as ""PendingInbox"",
      (SELECT COUNT(*) FROM {schema}.wh_active_streams)::bigint as ""ActiveStreams""";

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
  public async Task StoreInboxMessagesAsync(
    InboxMessage[] messages,
    int partitionCount = 2,
    CancellationToken cancellationToken = default) {
    if (messages.Length == 0) {
      return;
    }

    var json = _serializeNewInboxMessages(messages);
    var jsonParam = PostgresJsonHelper.JsonStringToJsonb(json);
    jsonParam.ParameterName = "p_messages";

    var schema = GetSchemaWithFallback(
      _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema(),
      DEFAULT_SCHEMA,
      _logger);
    var functionName = BuildSchemaQualifiedName(schema, "store_inbox_messages");

#pragma warning disable S2077 // Schema-qualified function name built from validated schema constant
    var sql = $"SELECT * FROM {functionName}({{0}}::jsonb, NULL::uuid, NULL::timestamptz, {{1}}, {{2}})";
#pragma warning restore S2077

    var now = DateTime.UtcNow;

    await _dbContext.Database.ExecuteSqlRawAsync(
      sql,
      [json, now, partitionCount],
      cancellationToken);
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
    var diagnosticTable = BuildSchemaQualifiedName(diagnosticSchema, "wh_perspective_cursors");
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
    var tableName = BuildSchemaQualifiedName(schema, "wh_perspective_cursors");
#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant; parameters use EF Core positional placeholders ({0}, {1})
    var sql = $"SELECT stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id FROM {tableName} WHERE stream_id = {{0}} AND perspective_name = {{1}}";
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
      RewindTriggerEventId = result.RewindTriggerEventId
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
    var tableName = BuildSchemaQualifiedName(schema, "wh_perspective_cursors");

    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection.State != System.Data.ConnectionState.Open) {
      await dbConnection.OpenAsync(cancellationToken);
    }

    await using var cmd = (Npgsql.NpgsqlCommand)dbConnection.CreateCommand();
#pragma warning disable S2077 // Schema-qualified table name built from validated schema constant
    cmd.CommandText = $"SELECT stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id FROM {tableName} WHERE stream_id = ANY(@p_stream_ids)";
#pragma warning restore S2077
    cmd.Parameters.Add(new Npgsql.NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streamIds
    });

    var results = new List<PerspectiveCursorInfo>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      results.Add(new PerspectiveCursorInfo {
        StreamId = reader.GetGuid(0),
        PerspectiveName = reader.GetString(1),
        LastEventId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
        Status = (PerspectiveProcessingStatus)reader.GetInt32(3),
        RewindTriggerEventId = reader.IsDBNull(4) ? null : reader.GetGuid(4)
      });
    }

    return results;
  }

  /// <summary>
  /// Extracts the message type name from an envelope type name.
  /// Example: "MessageEnvelope`1[[MyApp.ProductCreatedEvent, MyApp]], Whizbang.Core"
  /// Returns: "MyApp.ProductCreatedEvent, MyApp"
  /// </summary>
  private static string _extractMessageTypeFromEnvelopeType(string envelopeTypeName) {
    var startIndex = envelopeTypeName.IndexOf("[[", StringComparison.Ordinal);
    var endIndex = envelopeTypeName.IndexOf("]]", StringComparison.Ordinal);

    if (startIndex == -1 || endIndex == -1 || startIndex >= endIndex) {
      throw new InvalidOperationException(
        $"Invalid envelope type name format: '{envelopeTypeName}'. " +
        "Expected format: 'MessageEnvelope`1[[MessageType, Assembly]], EnvelopeAssembly'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException(
        $"Failed to extract message type name from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
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
    var cursorsTable = BuildSchemaQualifiedName(schema, "wh_perspective_cursors");
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
    // Deserialize event_data as JsonElement for AOT compatibility — same pattern as _deserializeEnvelope.
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
    var cursorsTable = BuildSchemaQualifiedName(schema, "wh_perspective_cursors");

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
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.IsDBNull(3) ? null : reader.GetGuid(3)));
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
    cmd.CommandText = $"SELECT {functionName}(@p_event_work_ids)";
#pragma warning restore S2077
    cmd.Parameters.Add(new NpgsqlParameter("p_event_work_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = workItemIds
    });

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
    cmd.Parameters.Add(new NpgsqlParameter("p_instance_id", instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = streamIds
    });

    var results = new List<StreamEventData>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) {
      // AOT-safe: read columns by ordinal, parse event_data as string
      var metadataOrdinal = reader.GetOrdinal("out_metadata");
      var scopeOrdinal = reader.GetOrdinal("out_scope");
      results.Add(new StreamEventData {
        StreamId = reader.GetGuid(reader.GetOrdinal("out_stream_id")),
        EventId = reader.GetGuid(reader.GetOrdinal("out_event_id")),
        EventType = reader.GetString(reader.GetOrdinal("out_event_type")),
        EventData = reader.GetString(reader.GetOrdinal("out_event_data")),
        Metadata = reader.IsDBNull(metadataOrdinal) ? null : reader.GetString(metadataOrdinal),
        Scope = reader.IsDBNull(scopeOrdinal) ? null : reader.GetString(scopeOrdinal),
        EventWorkId = reader.GetGuid(reader.GetOrdinal("out_event_work_id"))
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
