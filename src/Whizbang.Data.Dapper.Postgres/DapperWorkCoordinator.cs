using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;


/// <summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedWithErrorAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_StoresAndReturnsImmediatelyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_StoresWithDeduplicationAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithStreamId_AssignsPartitionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithStreamId_AssignsPartitionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithEventOutbox_PersistsToEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithEventInbox_PersistsToEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_EventVersionConflict_HandlesOptimisticConcurrencyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MultipleEventsInStream_IncrementsVersionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NonEvent_DoesNotPersistToEventStoreAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ConsistentHashing_SameStreamSamePartitionAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_PartitionAssignment_WithinRangeAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_LoadBalancing_DistributesAcrossInstancesAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_InstanceFailover_RedistributesPartitionsAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StatusFlags_AccumulateCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_PartialCompletion_TracksCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WorkBatchFlags_SetCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StaleInstances_CleanedUpAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ActiveInstances_NotCleanedAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
/// Dapper implementation of IWorkCoordinator for lease-based work coordination.
/// Uses the PostgreSQL process_work_batch function for atomic operations.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Work coordinator diagnostic logging - I/O bound database operations where LoggerMessage overhead isn't justified")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1845:Use span-based 'string.Concat'", Justification = "Debug logging with substring truncation - span-based operations not worth complexity for diagnostic output")]
public class DapperWorkCoordinator(
  string connectionString,
  JsonSerializerOptions jsonOptions,
  ILogger<DapperWorkCoordinator>? logger = null
) : IWorkCoordinator {
  private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<DapperWorkCoordinator>? _logger = logger;

  public async Task<WorkBatch> ProcessWorkBatchAsync(
    Guid instanceId,
    string serviceName,
    string hostName,
    int processId,
    Dictionary<string, JsonElement>? metadata,
    MessageCompletion[] outboxCompletions,
    MessageFailure[] outboxFailures,
    MessageCompletion[] inboxCompletions,
    MessageFailure[] inboxFailures,
    ReceptorProcessingCompletion[] receptorCompletions,
    ReceptorProcessingFailure[] receptorFailures,
    PerspectiveCheckpointCompletion[] perspectiveCompletions,
    PerspectiveCheckpointFailure[] perspectiveFailures,
    OutboxMessage[] newOutboxMessages,
    InboxMessage[] newInboxMessages,
    Guid[] renewOutboxLeaseIds,
    Guid[] renewInboxLeaseIds,
    WorkBatchFlags flags = WorkBatchFlags.None,
    int partitionCount = 10_000,
    int leaseSeconds = 300,
    int staleThresholdSeconds = 600,
    CancellationToken cancellationToken = default
  ) {
    _logger?.LogDebug(
      "Processing work batch for instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}): {OutboxCompletions} outbox completions, {OutboxFailures} outbox failures, {InboxCompletions} inbox completions, {InboxFailures} inbox failures, {NewOutbox} new outbox, {NewInbox} new inbox, Flags={Flags}",
      instanceId,
      serviceName,
      hostName,
      processId,
      outboxCompletions.Length,
      outboxFailures.Length,
      inboxCompletions.Length,
      inboxFailures.Length,
      newOutboxMessages.Length,
      newInboxMessages.Length,
      flags
    );

    await using var connection = new NpgsqlConnection(_connectionString);

    // Hook PostgreSQL RAISE NOTICE messages for debugging (before opening connection)
    // Notices are only generated when WorkBatchFlags.DebugMode is set in SQL function
    connection.Notice += _onNotice;

    await connection.OpenAsync(cancellationToken);

    // Convert to JSON
    var outboxCompletionsJson = _serializeCompletions(outboxCompletions);
    var outboxFailuresJson = _serializeFailures(outboxFailures);
    var inboxCompletionsJson = _serializeCompletions(inboxCompletions);
    var inboxFailuresJson = _serializeFailures(inboxFailures);
    var perspectiveCompletionsJson = _serializePerspectiveCompletions(perspectiveCompletions);
    var perspectiveFailuresJson = _serializePerspectiveFailures(perspectiveFailures);
    var newOutboxJson = _serializeNewOutboxMessages(newOutboxMessages);
    var newInboxJson = _serializeNewInboxMessages(newInboxMessages);
    var metadataJson = _serializeMetadata(metadata);
    var renewOutboxJson = _serializeLeaseRenewals(renewOutboxLeaseIds);
    var renewInboxJson = _serializeLeaseRenewals(renewInboxLeaseIds);

    // Execute the process_work_batch function (new signature after decomposition)
    var now = DateTimeOffset.UtcNow;
    var sql = @"
      SELECT * FROM process_work_batch(
        @p_instance_id::uuid,
        @p_service_name::varchar,
        @p_host_name::varchar,
        @p_process_id::int,
        @p_metadata::jsonb,
        @p_now::timestamptz,
        @p_lease_duration_seconds::int,
        @p_partition_count::int,
        @p_outbox_completions::jsonb,
        @p_inbox_completions::jsonb,
        @p_perspective_event_completions::jsonb,
        @p_perspective_completions::jsonb,
        @p_outbox_failures::jsonb,
        @p_inbox_failures::jsonb,
        @p_perspective_event_failures::jsonb,
        @p_perspective_failures::jsonb,
        @p_new_outbox_messages::jsonb,
        @p_new_inbox_messages::jsonb,
        @p_new_perspective_events::jsonb,
        @p_renew_outbox_lease_ids::jsonb,
        @p_renew_inbox_lease_ids::jsonb,
        @p_renew_perspective_event_lease_ids::jsonb,
        @p_flags::int,
        @p_stale_threshold_seconds::int
      )";

    var parameters = new {
      p_instance_id = instanceId,
      p_service_name = serviceName,
      p_host_name = hostName,
      p_process_id = processId,
      p_metadata = metadataJson,
      p_now = now,
      p_lease_duration_seconds = leaseSeconds,
      p_partition_count = partitionCount,
      p_outbox_completions = outboxCompletionsJson,
      p_inbox_completions = inboxCompletionsJson,
      p_perspective_event_completions = "[]",  // Not used - perspective events managed internally
      p_perspective_completions = perspectiveCompletionsJson,  // Checkpoint-level completions
      p_outbox_failures = outboxFailuresJson,
      p_inbox_failures = inboxFailuresJson,
      p_perspective_event_failures = "[]",  // Not used - perspective events managed internally
      p_perspective_failures = perspectiveFailuresJson,  // Checkpoint-level failures
      p_new_outbox_messages = newOutboxJson,
      p_new_inbox_messages = newInboxJson,
      p_new_perspective_events = "[]",
      p_renew_outbox_lease_ids = renewOutboxJson,
      p_renew_inbox_lease_ids = renewInboxJson,
      p_renew_perspective_event_lease_ids = "[]",
      p_flags = (int)flags,
      p_stale_threshold_seconds = staleThresholdSeconds
    };

    var commandDefinition = new CommandDefinition(
      sql,
      parameters,
      cancellationToken: cancellationToken
    );

    var results = await connection.QueryAsync<WorkBatchRow>(commandDefinition);
    var resultList = results.ToList();

    // Map results to WorkBatch - deserialize envelopes from database
    var outboxWork = resultList
      .Where(r => r.source == "outbox")
      .Select(r => {
        if (string.IsNullOrWhiteSpace(r.message_type) || string.IsNullOrWhiteSpace(r.message_data)) {
          throw new InvalidOperationException($"Outbox work {r.work_id} missing message_type or message_data");
        }

        var envelope = _deserializeEnvelope(r.message_type, r.message_data);
        // Cast to IMessageEnvelope<JsonElement> - envelope is always deserialized as MessageEnvelope<JsonElement>
        var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
          ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.work_id}");

        var flags = WorkBatchFlags.None;
        if (r.is_newly_stored) {
          flags |= WorkBatchFlags.NewlyStored;
        }

        if (r.is_orphaned) {
          flags |= WorkBatchFlags.Orphaned;
        }

        // Extract message type - prefer direct column, fall back to parsing EnvelopeType if null (backward compat)
        var messageType = !string.IsNullOrWhiteSpace(r.message_type)
          ? r.message_type
          : _extractMessageTypeFromEnvelopeType(r.envelope_type!);

        return new OutboxWork {
          MessageId = r.work_id,
          Destination = r.destination!,
          Envelope = jsonEnvelope,
          EnvelopeType = r.envelope_type!,
          MessageType = messageType,
          StreamId = r.work_stream_id,
          PartitionNumber = r.partition_number,
          Attempts = r.attempts,
          Status = (MessageProcessingStatus)r.status,
          Flags = flags
        };
      })
      .ToList();

    var inboxWork = resultList
      .Where(r => r.source == "inbox")
      .Select(r => {
        if (string.IsNullOrWhiteSpace(r.message_type) || string.IsNullOrWhiteSpace(r.message_data)) {
          throw new InvalidOperationException($"Inbox work {r.work_id} missing message_type or message_data");
        }

        var envelope = _deserializeEnvelope(r.message_type, r.message_data);
        // Cast to IMessageEnvelope<JsonElement> - envelope is always deserialized as MessageEnvelope<JsonElement>
        var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
          ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.work_id}");

        var flags = WorkBatchFlags.None;
        if (r.is_newly_stored) {
          flags |= WorkBatchFlags.NewlyStored;
        }

        if (r.is_orphaned) {
          flags |= WorkBatchFlags.Orphaned;
        }

        return new InboxWork {
          MessageId = r.work_id,
          Envelope = jsonEnvelope,
          MessageType = r.message_type,
          StreamId = r.work_stream_id,
          PartitionNumber = r.partition_number,
          Status = (MessageProcessingStatus)r.status,
          Flags = flags
        };
      })
      .ToList();

    var perspectiveWork = resultList
      .Where(r => r.source == "perspective")
      .Select(r => {
        var flags = WorkBatchFlags.None;
        if (r.is_newly_stored) {
          flags |= WorkBatchFlags.NewlyStored;
        }

        if (r.is_orphaned) {
          flags |= WorkBatchFlags.Orphaned;
        }

        return new PerspectiveWork {
          StreamId = r.work_stream_id ?? throw new InvalidOperationException($"Perspective work must have StreamId"),
          PerspectiveName = r.perspective_name ?? throw new InvalidOperationException($"Perspective work must have PerspectiveName"),
          LastProcessedEventId = null,
          Status = (PerspectiveProcessingStatus)r.status,
          PartitionNumber = r.partition_number,
          Flags = flags
        };
      })
      .ToList();

    _logger?.LogInformation(
      "Work batch processed: {OutboxWork} outbox work, {InboxWork} inbox work, {PerspectiveWork} perspective work",
      outboxWork.Count,
      inboxWork.Count,
      perspectiveWork.Count
    );

    return new WorkBatch {
      OutboxWork = outboxWork,
      InboxWork = inboxWork,
      PerspectiveWork = perspectiveWork
    };
  }

  private string _serializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string _serializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

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
      _logger?.LogDebug("First outbox message JSON: {Json}", json.Length > 500 ? json.Substring(0, 500) + "..." : json);
    }

    return json;
  }

  private string _serializeNewInboxMessages(InboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for InboxMessage[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  private string _serializeMetadata(Dictionary<string, JsonElement>? metadata) {
    if (metadata == null || metadata.Count == 0) {
      return "{}";  // Return empty JSON object instead of null (matches NOT NULL constraint)
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(metadata, typeInfo);
  }

  private string _serializeLeaseRenewals(Guid[] messageIds) {
    if (messageIds.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Guid[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messageIds, typeInfo);
  }

  private string _serializeReceptorCompletions(ReceptorProcessingCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string _serializeReceptorFailures(ReceptorProcessingFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  private string _serializePerspectiveCompletions(PerspectiveCheckpointCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCheckpointCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCheckpointCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

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
  /// Envelopes are always deserialized as MessageEnvelope&lt;JsonElement&gt; to support covariant casting to IMessageEnvelope&lt;object&gt;.
  /// </summary>
  private IMessageEnvelope _deserializeEnvelope(string envelopeTypeName, string envelopeDataJson) {
    // Log the envelope data for debugging
    _logger?.LogDebug("Deserializing envelope: Type={EnvelopeType}, JSON={EnvelopeJson}", envelopeTypeName, envelopeDataJson);

    // Always deserialize as MessageEnvelope<JsonElement> to support covariance casting to IMessageEnvelope<object>
    // (JsonElement is a value type, but the envelope interface is covariant and can be cast to object)
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageEnvelope<JsonElement>. Ensure it is registered via JsonContextRegistry.");

    // Deserialize the complete envelope as MessageEnvelope<JsonElement>
    var envelope = JsonSerializer.Deserialize(envelopeDataJson, typeInfo) as IMessageEnvelope
      ?? throw new InvalidOperationException($"Failed to deserialize envelope as MessageEnvelope<JsonElement>");

    // Log result for debugging
    _logger?.LogDebug("Deserialized envelope: MessageId={MessageId}, Hops={HopsCount}", envelope.MessageId, envelope.Hops.Count);

    return envelope;
  }

  /// <summary>
  /// Reports perspective checkpoint completion directly (out-of-band).
  /// Calls complete_perspective_checkpoint_work SQL function directly without full work batch processing.
  /// </summary>
  public async Task ReportPerspectiveCompletionAsync(
    PerspectiveCheckpointCompletion completion,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    await connection.ExecuteAsync(
      "SELECT complete_perspective_checkpoint_work(@StreamId, @PerspectiveName, @LastEventId, @Status, @Error)",
      new {
        StreamId = completion.StreamId,
        PerspectiveName = completion.PerspectiveName,
        LastEventId = completion.LastEventId,
        Status = (short)completion.Status,
        Error = (string?)null
      });
  }

  /// <summary>
  /// Reports perspective checkpoint failure directly (out-of-band).
  /// Calls complete_perspective_checkpoint_work SQL function directly without full work batch processing.
  /// </summary>
  public async Task ReportPerspectiveFailureAsync(
    PerspectiveCheckpointFailure failure,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    await connection.ExecuteAsync(
      "SELECT complete_perspective_checkpoint_work(@StreamId, @PerspectiveName, @LastEventId, @Status, @Error)",
      new {
        StreamId = failure.StreamId,
        PerspectiveName = failure.PerspectiveName,
        LastEventId = failure.LastEventId,
        Status = (short)failure.Status,
        Error = failure.Error
      });
  }

  /// <summary>
  /// Gets the current checkpoint for a perspective stream.
  /// Returns null if no checkpoint exists yet.
  /// </summary>
  public async Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var result = await connection.QueryFirstOrDefaultAsync<CheckpointQueryResult>(
      "SELECT stream_id, perspective_name, last_event_id, status FROM wh_perspective_checkpoints WHERE stream_id = @StreamId AND perspective_name = @PerspectiveName",
      new { StreamId = streamId, PerspectiveName = perspectiveName });

    if (result == null) {
      return null;
    }

    return new PerspectiveCheckpointInfo {
      StreamId = result.stream_id,
      PerspectiveName = result.perspective_name,
      LastEventId = result.last_event_id,
      Status = (PerspectiveProcessingStatus)result.status
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
/// Matches the function's return type structure with snake_case naming (PostgreSQL convention).
/// Updated for decomposed process_work_batch (migrations 009-029).
/// </summary>
internal class WorkBatchRow {
  public int instance_rank { get; set; }
  public int active_instance_count { get; set; }
  public required string source { get; set; }  // 'outbox', 'inbox', 'receptor', 'perspective'
  public required Guid work_id { get; set; }
  public Guid? work_stream_id { get; set; }
  public int? partition_number { get; set; }  // Partition assignment for load balancing
  public string? destination { get; set; }  // Topic name (outbox) or handler name (inbox)
  public string? message_type { get; set; }  // Assembly qualified name or perspective name
  public string? envelope_type { get; set; }  // Assembly qualified name of envelope type (for outbox only)
  public string? message_data { get; set; }  // Complete serialized MessageEnvelope<T> as JSON
  public string? metadata { get; set; }
  public int status { get; set; }  // MessageProcessingStatus flags
  public int attempts { get; set; }
  public bool is_newly_stored { get; set; }
  public bool is_orphaned { get; set; }
  public string? perspective_name { get; set; }
}

/// <summary>
/// DTO for querying perspective checkpoint info.
/// Uses snake_case to match PostgreSQL column names.
/// </summary>
internal class CheckpointQueryResult {
  public Guid stream_id { get; set; }
  public string perspective_name { get; set; } = string.Empty;
  public Guid? last_event_id { get; set; }
  public short status { get; set; }
}

