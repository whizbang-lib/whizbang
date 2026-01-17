using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
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
  ILogger<EFCoreWorkCoordinator<TDbContext>>? logger = null
) : IWorkCoordinator
  where TDbContext : DbContext {
  private const string DEFAULT_SCHEMA = "public";

  private readonly TDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<EFCoreWorkCoordinator<TDbContext>>? _logger = logger;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3776:Cognitive Complexity of methods should not be too high", Justification = "This method orchestrates complex work batch processing with multiple parameter preparations and SQL execution. Splitting would reduce clarity of the atomic database operation flow.")]
  public async Task<WorkBatch> ProcessWorkBatchAsync(
    ProcessWorkBatchRequest request,
    CancellationToken cancellationToken = default
  ) {
    _logger?.LogDebug(
      "Processing work batch for instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}): {OutboxCompletions} outbox completions, {OutboxFailures} outbox failures, {InboxCompletions} inbox completions, {InboxFailures} inbox failures, {NewOutbox} new outbox, {NewInbox} new inbox, Flags={Flags}",
      request.InstanceId,
      request.ServiceName,
      request.HostName,
      request.ProcessId,
      request.OutboxCompletions.Length,
      request.OutboxFailures.Length,
      request.InboxCompletions.Length,
      request.InboxFailures.Length,
      request.NewOutboxMessages.Length,
      request.NewInboxMessages.Length,
      request.Flags
    );

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

    var perspectiveEventCompletionsParam = PostgresJsonHelper.JsonStringToJsonb("[]");
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

    var now = DateTimeOffset.UtcNow;

    // CRITICAL: Get schema from DbContext model to schema-qualify the function call
    // Functions are database-wide in PostgreSQL - multiple schemas sharing a database
    // must use schema-qualified function names to avoid calling the wrong function
    var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema() ?? DEFAULT_SCHEMA;
    var functionName = string.IsNullOrEmpty(schema) || schema == DEFAULT_SCHEMA
      ? "process_work_batch"
      : $"{schema}.process_work_batch";

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
        @p_stale_threshold_seconds
      )";

    // Hook PostgreSQL RAISE NOTICE messages for debugging
    // Access the underlying NpgsqlConnection from EF Core's DbContext
    var dbConnection = _dbContext.Database.GetDbConnection();
    if (dbConnection is NpgsqlConnection npgsqlConnection) {
      // Wire up notice handler if not already connected
      if (npgsqlConnection.State != System.Data.ConnectionState.Open) {
        npgsqlConnection.Notice += _onNotice;
      }
    }

    var results = await _dbContext.Database
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
        new Npgsql.NpgsqlParameter("p_stale_threshold_seconds", request.StaleThresholdSeconds)
      )
      .ToListAsync(cancellationToken);

    // Process results and return work batch
    return _processResults(results);
  }

  /// <summary>
  /// Processes the query results and maps them to a WorkBatch
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3776:Cognitive Complexity of methods should not be too high", Justification = "This method deserializes and categorizes work batch results across three work types (outbox, inbox, perspective). The branching logic is inherent to the data mapping process.")]
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
      .Select(r => {
        var envelope = _deserializeEnvelope(r.MessageType!, r.MessageData!);
        // Cast to IMessageEnvelope<JsonElement> - envelope is always deserialized as MessageEnvelope<JsonElement>
        var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
          ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.WorkId}");

        var flags = WorkBatchFlags.None;
        if (r.IsNewlyStored) {
          flags |= WorkBatchFlags.NewlyStored;
        }

        if (r.IsOrphaned) {
          flags |= WorkBatchFlags.Orphaned;
        }

        // Deserialize metadata if present
        Dictionary<string, JsonElement>? metadata = null;
        if (!string.IsNullOrWhiteSpace(r.Metadata)) {
          try {
            var metadataDoc = JsonDocument.Parse(r.Metadata);
            metadata = metadataDoc.RootElement.EnumerateObject()
              .ToDictionary(p => p.Name, p => p.Value.Clone());
          } catch (JsonException ex) {
            _logger?.LogWarning(ex, "Failed to parse metadata JSON for work item {WorkId}", r.WorkId);
          }
        }

        // Extract message type - prefer direct column, fall back to parsing EnvelopeType if null (backward compat)
        var messageType = !string.IsNullOrWhiteSpace(r.MessageType)
          ? r.MessageType
          : _extractMessageTypeFromEnvelopeType(r.EnvelopeType!);

        return new OutboxWork {
          MessageId = r.WorkId,
          Destination = r.Destination!,
          Envelope = jsonEnvelope,
          EnvelopeType = r.EnvelopeType!,
          MessageType = messageType,
          StreamId = r.StreamId,
          PartitionNumber = r.PartitionNumber,
          Attempts = r.Attempts,
          Status = (MessageProcessingStatus)r.Status,
          Flags = flags,
          Metadata = metadata
        };
      })
      .ToList();

    var inboxWork = validResults
      .Where(r => r.Source == "inbox")
      .Select(r => {
        // Inbox messages MUST have message_type populated (envelope_type not stored for inbox)
        if (string.IsNullOrWhiteSpace(r.MessageType)) {
          throw new InvalidOperationException(
            $"Inbox message {r.WorkId} has null/empty message_type. " +
            $"This indicates the message was not properly serialized by the transport consumer. " +
            $"Ensure ServiceBusConsumerWorker or equivalent is correctly populating MessageType.");
        }

        var envelope = _deserializeEnvelope(r.MessageType, r.MessageData!);
        // Cast to IMessageEnvelope<JsonElement> - envelope is always deserialized as MessageEnvelope<JsonElement>
        var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
          ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.WorkId}");

        var flags = WorkBatchFlags.None;
        if (r.IsNewlyStored) {
          flags |= WorkBatchFlags.NewlyStored;
        }

        if (r.IsOrphaned) {
          flags |= WorkBatchFlags.Orphaned;
        }

        // Deserialize metadata if present
        Dictionary<string, JsonElement>? metadata = null;
        if (!string.IsNullOrWhiteSpace(r.Metadata)) {
          try {
            var metadataDoc = JsonDocument.Parse(r.Metadata);
            metadata = metadataDoc.RootElement.EnumerateObject()
              .ToDictionary(p => p.Name, p => p.Value.Clone());
          } catch (JsonException ex) {
            _logger?.LogWarning(ex, "Failed to parse metadata JSON for work item {WorkId}", r.WorkId);
          }
        }

        return new InboxWork {
          MessageId = r.WorkId,
          Envelope = jsonEnvelope,
          MessageType = r.MessageType,
          StreamId = r.StreamId,
          PartitionNumber = r.PartitionNumber,
          Status = (MessageProcessingStatus)r.Status,
          Flags = flags,
          Metadata = metadata
        };
      })
      .ToList();

    var perspectiveWork = validResults
      .Where(r => r.Source == "perspective")
      .Select(r => {
        var flags = WorkBatchFlags.None;
        if (r.IsNewlyStored) {
          flags |= WorkBatchFlags.NewlyStored;
        }

        if (r.IsOrphaned) {
          flags |= WorkBatchFlags.Orphaned;
        }

        // Deserialize metadata if present
        Dictionary<string, JsonElement>? metadata = null;
        if (!string.IsNullOrWhiteSpace(r.Metadata)) {
          try {
            var metadataDoc = JsonDocument.Parse(r.Metadata);
            metadata = metadataDoc.RootElement.EnumerateObject()
              .ToDictionary(p => p.Name, p => p.Value.Clone());
          } catch (JsonException ex) {
            _logger?.LogWarning(ex, "Failed to parse metadata JSON for work item {WorkId}", r.WorkId);
          }
        }

        return new PerspectiveWork {
          StreamId = r.StreamId ?? throw new InvalidOperationException($"Perspective work must have StreamId"),
          PerspectiveName = r.PerspectiveName ?? throw new InvalidOperationException($"Perspective work must have PerspectiveName"),
          LastProcessedEventId = null,  // No longer returned by process_work_batch
          Status = (PerspectiveProcessingStatus)r.Status,
          PartitionNumber = r.PartitionNumber,
          Flags = flags,
          Metadata = metadata
        };
      })
      .ToList();

    // Only log when there's actual work to report
    if (outboxWork.Count > 0 || inboxWork.Count > 0 || perspectiveWork.Count > 0) {
      _logger?.LogInformation(
        "Work batch processed: {OutboxWork} outbox work, {InboxWork} inbox work, {PerspectiveWork} perspective work",
        outboxWork.Count,
        inboxWork.Count,
        perspectiveWork.Count
      );
    }

    return new WorkBatch {
      OutboxWork = outboxWork,
      InboxWork = inboxWork,
      PerspectiveWork = perspectiveWork
    };
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

      _logger?.LogDebug("Serializing outbox message: MessageId={MessageId}, Destination={Destination}, EnvelopeType={EnvelopeType}, HopsCount={HopsCount}",
        firstMessage.MessageId, firstMessage.Destination, firstMessage.EnvelopeType,
        firstMessage.Envelope.Hops.Count);
      _logger?.LogDebug("First outbox message JSON (first 500 chars): {Json}", json.Length > 500 ? json.Substring(0, 500) + "..." : json);
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
  /// Serializes perspective checkpoint completions to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string _serializePerspectiveCompletions(PerspectiveCheckpointCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCheckpointCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCheckpointCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <summary>
  /// Serializes perspective checkpoint failures to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string _serializePerspectiveFailures(PerspectiveCheckpointFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCheckpointFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCheckpointFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
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
    _logger?.LogDebug("Deserializing envelope: Type={EnvelopeType}, Data (first 500 chars)={EnvelopeData}",
      envelopeTypeName,
      envelopeDataJson.Length > 500 ? envelopeDataJson.Substring(0, 500) + "..." : envelopeDataJson);

    // Always deserialize as MessageEnvelope<JsonElement> for AOT compatibility
    // This eliminates the need for Type.GetType() and runtime type resolution
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageEnvelope<JsonElement>. Ensure it is registered via JsonContextRegistry.");

    // Deserialize the complete envelope as MessageEnvelope<JsonElement>
    var envelope = JsonSerializer.Deserialize(envelopeDataJson, typeInfo) as IMessageEnvelope
      ?? throw new InvalidOperationException("Failed to deserialize envelope as MessageEnvelope<JsonElement>");

    _logger?.LogDebug("Deserialized envelope: MessageId={MessageId}, HopsCount={HopsCount}",
      envelope.MessageId,
      envelope.Hops?.Count ?? 0);

    return envelope;
  }

  /// <summary>
  /// Reports perspective checkpoint completion directly (out-of-band).
  /// Calls complete_perspective_checkpoint_work SQL function directly without full work batch processing.
  /// Creates its own database connection to allow calling after the scoped DbContext is disposed.
  /// </summary>
  public async Task ReportPerspectiveCompletionAsync(
    PerspectiveCheckpointCompletion completion,
    CancellationToken cancellationToken = default) {
    // CRITICAL FIX: Use existing DbContext and commit transaction explicitly
    // The DbContext's current transaction scope must be committed for changes to be visible
    // to subsequent ProcessWorkBatchAsync calls that create new transactions
    _logger?.LogInformation(
      "[DIAGNOSTIC] ReportPerspectiveCompletionAsync called: stream={StreamId}, perspective={PerspectiveName}, lastEvent={LastEventId}, status={Status}",
      completion.StreamId, completion.PerspectiveName, completion.LastEventId, completion.Status);

    // CRITICAL: Skip if no events were processed (LastEventId = Guid.Empty)
    // This prevents FK constraint violation when event doesn't exist in wh_event_store
    if (completion.LastEventId == Guid.Empty) {
      _logger?.LogDebug(
        "[DIAGNOSTIC] Skipping checkpoint update for stream={StreamId}, perspective={PerspectiveName} - no events processed (LastEventId is Empty)",
        completion.StreamId, completion.PerspectiveName);
      return;
    }

    // Begin explicit transaction if one doesn't exist
    var transaction = _dbContext.Database.CurrentTransaction;
    var needsCommit = transaction == null;

    if (needsCommit) {
      transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    try {
      // Get schema from DbContext configuration for schema-qualified function call
      var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema() ?? DEFAULT_SCHEMA;
      var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, "SELECT {0}.complete_perspective_checkpoint_work({{0}}, {{1}}, {{2}}, {{3}}, {{4}}::text)", schema);

      await _dbContext.Database.ExecuteSqlRawAsync(
        sql,
        [completion.StreamId, completion.PerspectiveName, completion.LastEventId, (short)completion.Status, null!],
        cancellationToken);

      // Commit transaction IMMEDIATELY so changes are visible to next ProcessWorkBatchAsync
      if (needsCommit && transaction != null) {
        await transaction.CommitAsync(cancellationToken);
        _logger?.LogInformation(
          "[DIAGNOSTIC] Transaction committed for stream={StreamId}, perspective={PerspectiveName}",
          completion.StreamId, completion.PerspectiveName);
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

    _logger?.LogInformation(
      "[DIAGNOSTIC] complete_perspective_checkpoint_work completed for stream={StreamId}, perspective={PerspectiveName}",
      completion.StreamId, completion.PerspectiveName);

    // DIAGNOSTIC: Verify the checkpoint was actually updated
    // Get schema from OutboxRecord entity (all Whizbang tables share the same schema)
    var diagnosticSchema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema() ?? "public";
    var diagnosticSql = string.Format(System.Globalization.CultureInfo.InvariantCulture,
      "SELECT stream_id, perspective_name, status, last_event_id, error FROM {0}.wh_perspective_checkpoints WHERE stream_id = {{0}} AND perspective_name = {{1}}",
      diagnosticSchema);

    var checkpointState = await _dbContext.Database
      .SqlQueryRaw<CheckpointDiagnostic>(diagnosticSql, completion.StreamId, completion.PerspectiveName)
      .FirstOrDefaultAsync(cancellationToken);

    if (checkpointState != null) {
      _logger?.LogInformation(
        "[DIAGNOSTIC] After update - checkpoint state: stream={StreamId}, perspective={PerspectiveName}, status={Status}, lastEvent={LastEventId}, error={Error}",
        checkpointState.StreamId, checkpointState.PerspectiveName, checkpointState.Status, checkpointState.LastEventId, checkpointState.Error);
    } else {
      _logger?.LogWarning(
        "[DIAGNOSTIC] Checkpoint not found after update: stream={StreamId}, perspective={PerspectiveName}",
        completion.StreamId, completion.PerspectiveName);
    }
  }

  /// <summary>
  /// Reports perspective checkpoint failure directly (out-of-band).
  /// Calls complete_perspective_checkpoint_work SQL function directly without full work batch processing.
  /// Creates its own database connection to allow calling after the scoped DbContext is disposed.
  /// </summary>
  public async Task ReportPerspectiveFailureAsync(
    PerspectiveCheckpointFailure failure,
    CancellationToken cancellationToken = default) {
    // Use DbContext's ExecuteSqlRawAsync which properly manages the connection
    // This works with both traditional connection strings and NpgsqlDataSource

    // CRITICAL: Skip if no events were processed (LastEventId = Guid.Empty)
    // This prevents FK constraint violation when event doesn't exist in wh_event_store
    if (failure.LastEventId == Guid.Empty) {
      _logger?.LogDebug(
        "[DIAGNOSTIC] Skipping checkpoint update for failure on stream={StreamId}, perspective={PerspectiveName} - no events processed (LastEventId is Empty)",
        failure.StreamId, failure.PerspectiveName);
      return;
    }

    // Get schema from DbContext configuration for schema-qualified function call
    var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema() ?? DEFAULT_SCHEMA;
    var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, "SELECT {0}.complete_perspective_checkpoint_work({{0}}, {{1}}, {{2}}, {{3}}, {{4}}::text)", schema);

    await _dbContext.Database.ExecuteSqlRawAsync(
      sql,
      [failure.StreamId, failure.PerspectiveName, failure.LastEventId, (short)failure.Status, failure.Error],
      cancellationToken);
  }

  /// <summary>
  /// Gets the current checkpoint for a perspective stream.
  /// Returns null if no checkpoint exists yet.
  /// </summary>
  public async Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default) {

    // Get schema from OutboxRecord entity (all Whizbang tables share the same schema)
    var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema() ?? DEFAULT_SCHEMA;
    var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture,
      "SELECT stream_id, perspective_name, last_event_id, status FROM {0}.wh_perspective_checkpoints WHERE stream_id = {{0}} AND perspective_name = {{1}}",
      schema);

    var result = await _dbContext.Database
      .SqlQueryRaw<CheckpointQueryResult>(sql, streamId, perspectiveName)
      .FirstOrDefaultAsync(cancellationToken);

    if (result == null) {
      return null;
    }

    return new PerspectiveCheckpointInfo {
      StreamId = result.StreamId,
      PerspectiveName = result.PerspectiveName,
      LastEventId = result.LastEventId,
      Status = (PerspectiveProcessingStatus)result.Status
    };
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
        $"Expected format: 'MessageEnvelope`1[[MessageType, Assembly]], EnvelopeAssembly'");
    }

    var messageTypeName = envelopeTypeName.Substring(startIndex + 2, endIndex - startIndex - 2);

    if (string.IsNullOrWhiteSpace(messageTypeName)) {
      throw new InvalidOperationException(
        $"Failed to extract message type name from envelope type: '{envelopeTypeName}'");
    }

    return messageTypeName;
  }

  /// <summary>
  /// Handles PostgreSQL RAISE NOTICE messages by logging them at Debug level.
  /// Notices are only generated when WorkBatchFlags.DebugMode is set in the SQL function.
  /// </summary>
  private void _onNotice(object? sender, NpgsqlNoticeEventArgs args) {
    _logger?.LogDebug("PostgreSQL Notice [{Severity}]: {Message}",
      args.Notice.Severity, args.Notice.MessageText);
  }
}

/// <summary>
/// Internal DTO for mapping process_work_batch function results.
/// Matches the function's return type structure.
/// </summary>
internal class WorkBatchRow {
  [Column("instance_rank")]
  public int InstanceRank { get; set; }

  [Column("active_instance_count")]
  public int ActiveInstanceCount { get; set; }

  [Column("source")]
  public required string Source { get; set; }  // 'outbox', 'inbox', 'receptor', 'perspective'

  [Column("work_id")]
  public required Guid WorkId { get; set; }  // message_id or event_work_id or processing_id

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
  public int Status { get; set; }  // MessageProcessingStatus flags

  [Column("attempts")]
  public int Attempts { get; set; }

  [Column("is_newly_stored")]
  public bool IsNewlyStored { get; set; }

  [Column("is_orphaned")]
  public bool IsOrphaned { get; set; }

  [Column("error")]
  public string? Error { get; set; }  // Error message (NULL if no error)

  [Column("failure_reason")]
  public int? FailureReason { get; set; }  // MessageFailureReason enum value (NULL if no failure)

  [Column("perspective_name")]
  public string? PerspectiveName { get; set; }  // NULL for non-perspective work
}

/// <summary>
/// Diagnostic DTO for querying perspective checkpoint state.
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
/// DTO for querying perspective checkpoint info.
/// Used by GetPerspectiveCheckpointAsync.
/// </summary>
internal class CheckpointQueryResult {
  [Column("stream_id")]
  public Guid StreamId { get; set; }

  [Column("perspective_name")]
  public string PerspectiveName { get; set; } = string.Empty;

  [Column("status")]
  public short Status { get; set; }

  [Column("last_event_id")]
  public Guid? LastEventId { get; set; }
}
