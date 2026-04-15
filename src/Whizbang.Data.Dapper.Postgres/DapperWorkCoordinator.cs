using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
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
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WorkBatchOptions_SetCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_StaleInstances_CleanedUpAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ActiveInstances_NotCleanedAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewOutboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventTrue_StoresIsEventFlagAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NewInboxMessage_WithIsEventFalse_StoresIsEventFlagAsync</tests>
/// Dapper implementation of IWorkCoordinator for lease-based work coordination.
/// Uses the PostgreSQL process_work_batch function for atomic operations.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1845:Use span-based 'string.Concat'", Justification = "Debug logging with substring truncation - span-based operations not worth complexity for diagnostic output")]
public partial class DapperWorkCoordinator(
  string connectionString,
  JsonSerializerOptions jsonOptions,
  ILogger<DapperWorkCoordinator>? logger = null,
  int commandTimeoutSeconds = 5
) : IWorkCoordinator {
  private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<DapperWorkCoordinator>? _logger = logger;
  private readonly int _commandTimeoutSeconds = commandTimeoutSeconds;

  public async Task<WorkBatch> ProcessWorkBatchAsync(
    ProcessWorkBatchRequest request,
    CancellationToken cancellationToken = default
  ) {
    if (_logger is not null) {
      LogProcessingWorkBatch(_logger, request.InstanceId, request.ServiceName, request.HostName, request.ProcessId,
        request.OutboxCompletions.Length, request.OutboxFailures.Length,
        request.InboxCompletions.Length, request.InboxFailures.Length,
        request.NewOutboxMessages.Length, request.NewInboxMessages.Length, request.Flags);
    }

    await using var connection = new NpgsqlConnection(_connectionString);

    // Hook PostgreSQL RAISE DEBUG messages for debugging (before opening connection)
    // Notices are only generated when WorkBatchOptions.DebugMode is set in SQL function
    connection.Notice += _onNotice;

    await connection.OpenAsync(cancellationToken);

    var commandDefinition = _buildCommandDefinition(request, cancellationToken);
    var resultList = await _executeWorkBatchQueryAsync(connection, commandDefinition, request);

    return _categorizeResults(resultList);
  }

  /// <summary>
  /// Builds the CommandDefinition for the process_work_batch SQL function call.
  /// </summary>
  private CommandDefinition _buildCommandDefinition(
    ProcessWorkBatchRequest request,
    CancellationToken cancellationToken
  ) {
    var serializedData = _serializeWorkBatchData(request);
    var now = DateTimeOffset.UtcNow;

    const string sql = @"
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
        @p_stale_threshold_seconds::int,
        @p_sync_inquiries::jsonb,
        @p_max_perspective_streams::int
      )";

    var parameters = new {
      p_instance_id = request.InstanceId,
      p_service_name = request.ServiceName,
      p_host_name = request.HostName,
      p_process_id = request.ProcessId,
      p_metadata = serializedData.Metadata,
      p_now = now,
      p_lease_duration_seconds = request.LeaseSeconds,
      p_partition_count = request.PartitionCount,
      p_outbox_completions = serializedData.OutboxCompletions,
      p_inbox_completions = serializedData.InboxCompletions,
      p_perspective_event_completions = serializedData.PerspectiveEventCompletions,
      p_perspective_completions = serializedData.PerspectiveCompletions,  // Checkpoint-level completions
      p_outbox_failures = serializedData.OutboxFailures,
      p_inbox_failures = serializedData.InboxFailures,
      p_perspective_event_failures = "[]",  // Not used - perspective events managed internally
      p_perspective_failures = serializedData.PerspectiveFailures,  // Checkpoint-level failures
      p_new_outbox_messages = serializedData.NewOutboxMessages,
      p_new_inbox_messages = serializedData.NewInboxMessages,
      p_new_perspective_events = "[]",
      p_renew_outbox_lease_ids = serializedData.RenewOutboxLeaseIds,
      p_renew_inbox_lease_ids = serializedData.RenewInboxLeaseIds,
      p_renew_perspective_event_lease_ids = "[]",
      p_flags = (int)request.Flags,
      p_stale_threshold_seconds = request.StaleThresholdSeconds,
      p_sync_inquiries = serializedData.SyncInquiries,
      p_max_perspective_streams = request.MaxPerspectiveStreams
    };

    return new CommandDefinition(
      sql,
      parameters,
      commandTimeout: _commandTimeoutSeconds,
      cancellationToken: cancellationToken
    );
  }

  /// <summary>
  /// Executes the work batch query with structured exception handling and logging.
  /// </summary>
  private async Task<List<WorkBatchRow>> _executeWorkBatchQueryAsync(
    NpgsqlConnection connection,
    CommandDefinition commandDefinition,
    ProcessWorkBatchRequest request
  ) {
    try {
      var results = await connection.QueryAsync<WorkBatchRow>(commandDefinition);
      return [.. results];
    } catch (NpgsqlException ex) when (ex.InnerException is TimeoutException || ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)) {
      if (_logger is not null) {
        LogWorkBatchTimedOut(_logger, _commandTimeoutSeconds, request.InstanceId, request.ServiceName, ex);
      }
      throw;
    } catch (OperationCanceledException ex) {
      if (_logger is not null) {
        LogWorkBatchCancelled(_logger, request.InstanceId, request.ServiceName, ex);
      }
      throw;
    } catch (Exception ex) {
      if (_logger is not null) {
        LogWorkBatchFailed(_logger, request.InstanceId, request.ServiceName, ex);
      }
      throw;
    }
  }

  /// <summary>
  /// Categorizes work batch rows by source type into the appropriate work lists.
  /// </summary>
  private WorkBatch _categorizeResults(List<WorkBatchRow> resultList) {
    var outboxWork = new List<OutboxWork>();
    var inboxWork = new List<InboxWork>();
    var perspectiveWork = new List<PerspectiveWork>();
    var perspectiveStreamIds = new HashSet<Guid>();
    var syncInquiryResults = new List<SyncInquiryResult>();

    foreach (var r in resultList) {
      switch (r.source) {
        case "outbox":
          outboxWork.Add(_mapOutboxWork(r));
          break;
        case "inbox":
          inboxWork.Add(_mapInboxWork(r));
          break;
        case "perspective_stream":
          // Drain mode: SQL returns one row per distinct stream (no per-event detail)
          if (r.work_stream_id.HasValue) {
            perspectiveStreamIds.Add(r.work_stream_id.Value);
          }
          break;
        case "perspective":
          // Legacy mode: per-event rows with perspective_name
          perspectiveWork.Add(_mapPerspectiveWork(r));
          if (r.work_stream_id.HasValue) {
            perspectiveStreamIds.Add(r.work_stream_id.Value);
          }
          break;
        case "sync_result":
          syncInquiryResults.Add(_mapSyncInquiryResult(r));
          break;
      }
    }

    if (_logger is not null) {
      LogWorkBatchProcessed(_logger, outboxWork.Count, inboxWork.Count, perspectiveWork.Count, syncInquiryResults.Count);
    }

    return new WorkBatch {
      OutboxWork = outboxWork,
      InboxWork = inboxWork,
      PerspectiveWork = perspectiveWork,
      PerspectiveStreamIds = [.. perspectiveStreamIds],
      SyncInquiryResults = syncInquiryResults.Count > 0 ? syncInquiryResults : null
    };
  }

  /// <summary>
  /// Maps a WorkBatchRow with source "sync_result" to a SyncInquiryResult.
  /// </summary>
  private SyncInquiryResult _mapSyncInquiryResult(WorkBatchRow r) {
    return new SyncInquiryResult {
      InquiryId = r.work_id!.Value,
      StreamId = r.work_stream_id ?? Guid.Empty,
      PendingCount = r.partition_number ?? 0,
      ProcessedCount = r.status,
      PendingEventIds = _parsePendingEventIds(r.message_data),
      ProcessedEventIds = _parseProcessedEventIds(r.metadata)
    };
  }

  private OutboxWork _mapOutboxWork(WorkBatchRow r) {
    if (string.IsNullOrWhiteSpace(r.message_type) || string.IsNullOrWhiteSpace(r.message_data)) {
      throw new InvalidOperationException($"Outbox work {r.work_id} missing message_type or message_data");
    }

    var envelope = _deserializeEnvelope(r.message_type, r.message_data);
    var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.work_id}");

    var messageType = !string.IsNullOrWhiteSpace(r.message_type)
      ? r.message_type
      : _extractMessageTypeFromEnvelopeType(r.envelope_type!);

    return new OutboxWork {
      MessageId = r.work_id!.Value,
      Destination = r.destination!,
      Envelope = jsonEnvelope,
      EnvelopeType = r.envelope_type!,
      MessageType = messageType,
      StreamId = r.work_stream_id,
      PartitionNumber = r.partition_number,
      Attempts = r.attempts,
      Status = (MessageProcessingStatus)r.status,
      Flags = _buildFlags(r.is_newly_stored, r.is_orphaned)
    };
  }

  private InboxWork _mapInboxWork(WorkBatchRow r) {
    if (string.IsNullOrWhiteSpace(r.message_type) || string.IsNullOrWhiteSpace(r.message_data)) {
      throw new InvalidOperationException($"Inbox work {r.work_id} missing message_type or message_data");
    }

    var envelope = _deserializeEnvelope(r.message_type, r.message_data);
    var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
      ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.work_id}");

    return new InboxWork {
      MessageId = r.work_id!.Value,
      Envelope = jsonEnvelope,
      MessageType = r.message_type,
      StreamId = r.work_stream_id,
      PartitionNumber = r.partition_number,
      Attempts = r.attempts,
      Status = (MessageProcessingStatus)r.status,
      Flags = _buildFlags(r.is_newly_stored, r.is_orphaned)
    };
  }

  private static PerspectiveWork _mapPerspectiveWork(WorkBatchRow r) {
    return new PerspectiveWork {
      WorkId = r.work_id ?? Guid.Empty,  // NULL in stream assignment model (drain mode) — worker uses PerspectiveStreamIds instead
      StreamId = r.work_stream_id ?? throw new InvalidOperationException("Perspective work must have StreamId"),
      PerspectiveName = r.perspective_name ?? throw new InvalidOperationException("Perspective work must have PerspectiveName"),
      LastProcessedEventId = null,
      Status = (PerspectiveProcessingStatus)r.status,
      PartitionNumber = r.partition_number,
      Flags = _buildFlags(r.is_newly_stored, r.is_orphaned)
    };
  }

  private static WorkBatchOptions _buildFlags(bool isNewlyStored, bool isOrphaned) {
    var flags = WorkBatchOptions.None;
    if (isNewlyStored) {
      flags |= WorkBatchOptions.NewlyStored;
    }
    if (isOrphaned) {
      flags |= WorkBatchOptions.Orphaned;
    }
    return flags;
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
    if (messages.Length > 0 && _logger is not null) {
      var firstMessage = messages[0];
      var jsonPreview = json.Length > 500 ? json[..500] + "..." : json;
      LogSerializingOutboxMessage(_logger, firstMessage.MessageId, firstMessage.Destination, firstMessage.EnvelopeType, firstMessage.Envelope.Hops?.Count ?? 0);
      LogOutboxMessageJson(_logger, jsonPreview);
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

  private string _serializePerspectiveFailures(PerspectiveCursorFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCursorFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCursorFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  private string _serializeSyncInquiries(SyncInquiry[]? inquiries) {
    if (inquiries == null || inquiries.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(SyncInquiry[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for SyncInquiry[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(inquiries, typeInfo);
  }

  /// <summary>
  /// Deserializes envelope from database envelope_type and envelope_data columns.
  /// Envelopes are always deserialized as MessageEnvelope&lt;JsonElement&gt; to support covariant casting to IMessageEnvelope&lt;object&gt;.
  /// </summary>
  private IMessageEnvelope _deserializeEnvelope(string envelopeTypeName, string envelopeDataJson) {
    // Log the envelope data for debugging
    if (_logger is not null) {
      LogDeserializingEnvelope(_logger, envelopeTypeName, envelopeDataJson);
    }

    // Always deserialize as MessageEnvelope<JsonElement> to support covariance casting to IMessageEnvelope<object>
    // (JsonElement is a value type, but the envelope interface is covariant and can be cast to object)
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageEnvelope<JsonElement>. Ensure it is registered via JsonContextRegistry.");

    // Deserialize the complete envelope as MessageEnvelope<JsonElement>
    var envelope = JsonSerializer.Deserialize(envelopeDataJson, typeInfo) as IMessageEnvelope
      ?? throw new InvalidOperationException("Failed to deserialize envelope as MessageEnvelope<JsonElement>");

    // Log result for debugging
    if (_logger is not null) {
      LogDeserializedEnvelope(_logger, envelope.MessageId.Value, envelope.Hops?.Count ?? 0);
    }

    return envelope;
  }

  /// <summary>
  /// Reports perspective cursor completion directly (out-of-band).
  /// Calls complete_perspective_cursor_work SQL function directly without full work batch processing.
  /// </summary>
  /// <inheritdoc />
  public async Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await connection.ExecuteAsync("SELECT deregister_instance(@instanceId)", new { instanceId });
  }

  /// <inheritdoc />
  public async Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return await connection.QuerySingleAsync<WorkCoordinatorStatistics>(@"
      SELECT
        (SELECT COUNT(*) FROM wh_perspective_events WHERE processed_at IS NULL) as PendingPerspectiveEvents,
        (SELECT COUNT(*) FROM wh_outbox WHERE processed_at IS NULL) as PendingOutbox,
        (SELECT COUNT(*) FROM wh_inbox WHERE processed_at IS NULL) as PendingInbox,
        (SELECT COUNT(*) FROM wh_active_streams) as ActiveStreams");
  }

  /// <summary>
  /// Stores inbox messages directly via store_inbox_messages SQL function.
  /// Bypasses the full process_work_batch pipeline for maximum inbox throughput.
  /// </summary>
  public async Task StoreInboxMessagesAsync(
    InboxMessage[] messages,
    int partitionCount = 2,
    CancellationToken cancellationToken = default) {
    if (messages.Length == 0) {
      return;
    }

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var json = _serializeNewInboxMessages(messages);
    var now = DateTimeOffset.UtcNow;

    await connection.ExecuteAsync(
      "SELECT * FROM store_inbox_messages(@messages::jsonb, NULL::uuid, NULL::timestamptz, @now, @partitionCount)",
      new {
        messages = json,
        now,
        partitionCount
      });
  }

  public async Task ReportPerspectiveCompletionAsync(
    PerspectiveCursorCompletion completion,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var processedEventIdsJson = JsonSerializer.Serialize(
      completion.ProcessedEventIds,
      _jsonOptions.GetTypeInfo(typeof(Guid[])) ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]"));

    await connection.ExecuteAsync(
      "SELECT complete_perspective_cursor_work(@StreamId, @PerspectiveName, @LastEventId, @ProcessedEventIds::jsonb, @Status, @Error)",
      new {
        StreamId = completion.StreamId,
        PerspectiveName = completion.PerspectiveName,
        LastEventId = completion.LastEventId,
        ProcessedEventIds = processedEventIdsJson,
        Status = (short)completion.Status,
        Error = (string?)null
      });
  }

  /// <summary>
  /// Reports perspective cursor failure directly (out-of-band).
  /// Calls complete_perspective_cursor_work SQL function directly without full work batch processing.
  /// </summary>
  public async Task ReportPerspectiveFailureAsync(
    PerspectiveCursorFailure failure,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var processedEventIdsJson = JsonSerializer.Serialize(
      failure.ProcessedEventIds,
      _jsonOptions.GetTypeInfo(typeof(Guid[])) ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]"));

    await connection.ExecuteAsync(
      "SELECT complete_perspective_cursor_work(@StreamId, @PerspectiveName, @LastEventId, @ProcessedEventIds::jsonb, @Status, @Error)",
      new {
        StreamId = failure.StreamId,
        PerspectiveName = failure.PerspectiveName,
        LastEventId = failure.LastEventId,
        ProcessedEventIds = processedEventIdsJson,
        Status = (short)failure.Status,
        Error = failure.Error
      });
  }

  /// <summary>
  /// Completes perspective events by deleting the specified work items from wh_perspective_events.
  /// Called per-stream immediately after processing (drain mode — no buffering).
  /// </summary>
  public async Task<int> CompletePerspectiveEventsAsync(
    Guid[] workItemIds,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    return await connection.QuerySingleAsync<int>(
      "SELECT complete_perspective_events(@p_event_work_ids)",
      new { p_event_work_ids = workItemIds });
  }

  /// <summary>
  /// Batch-fetches events for multiple streams in a single call.
  /// Returns denormalized rows joining wh_perspective_events with wh_event_store.
  /// Only returns events leased to the requesting instance.
  /// </summary>
  public async Task<List<StreamEventData>> GetStreamEventsAsync(
    Guid instanceId,
    Guid[] streamIds,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var now = DateTimeOffset.UtcNow;
    var results = await connection.QueryAsync<StreamEventRow>(
      "SELECT * FROM get_stream_events(@p_instance_id, @p_stream_ids, @p_now)",
      new { p_instance_id = instanceId, p_stream_ids = streamIds, p_now = now });

    return [.. results.Select(r => new StreamEventData {
      StreamId = r.out_stream_id,
      EventId = r.out_event_id,
      EventType = r.out_event_type,
      EventData = r.out_event_data,
      Metadata = r.out_metadata,
      Scope = r.out_scope,
      EventWorkId = r.out_event_work_id
    })];
  }

  /// <summary>
  /// Gets the current checkpoint for a perspective stream.
  /// Returns null if no checkpoint exists yet.
  /// </summary>
  public async Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    var result = await connection.QueryFirstOrDefaultAsync<CursorQueryResult>(
      "SELECT stream_id, perspective_name, last_event_id, status, rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = @StreamId AND perspective_name = @PerspectiveName",
      new { StreamId = streamId, PerspectiveName = perspectiveName });

    if (result == null) {
      return null;
    }

    return new PerspectiveCursorInfo {
      StreamId = result.stream_id,
      PerspectiveName = result.perspective_name,
      LastEventId = result.last_event_id,
      Status = (PerspectiveProcessingStatus)result.status,
      RewindTriggerEventId = result.rewind_trigger_event_id
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
    if (_logger is not null) {
      LogPostgresNotice(_logger, args.Notice.Severity, args.Notice.MessageText);
    }
  }

  /// <summary>
  /// Serializes all work batch data into JSON format for database function call.
  /// </summary>
  private SerializedWorkBatchData _serializeWorkBatchData(ProcessWorkBatchRequest request) {
    return new SerializedWorkBatchData(
      OutboxCompletions: _serializeCompletions(request.OutboxCompletions),
      OutboxFailures: _serializeFailures(request.OutboxFailures),
      InboxCompletions: _serializeCompletions(request.InboxCompletions),
      InboxFailures: _serializeFailures(request.InboxFailures),
      PerspectiveEventCompletions: _serializePerspectiveEventCompletions(request.PerspectiveEventCompletions),
      PerspectiveCompletions: _serializePerspectiveCompletions(request.PerspectiveCompletions),
      PerspectiveFailures: _serializePerspectiveFailures(request.PerspectiveFailures),
      NewOutboxMessages: _serializeNewOutboxMessages(request.NewOutboxMessages),
      NewInboxMessages: _serializeNewInboxMessages(request.NewInboxMessages),
      Metadata: _serializeMetadata(request.Metadata),
      RenewOutboxLeaseIds: _serializeLeaseRenewals(request.RenewOutboxLeaseIds),
      RenewInboxLeaseIds: _serializeLeaseRenewals(request.RenewInboxLeaseIds),
      SyncInquiries: _serializeSyncInquiries(request.PerspectiveSyncInquiries)
    );
  }

  /// <summary>
  /// Parses pending event IDs from the JSON array encoded in message_data column.
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

  #region LoggerMessage Declarations

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Processing work batch for instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}): {OutboxCompletions} outbox completions, {OutboxFailures} outbox failures, {InboxCompletions} inbox completions, {InboxFailures} inbox failures, {NewOutbox} new outbox, {NewInbox} new inbox, Flags={Flags}"
  )]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage requires flat parameters matching the structured log template")]
  static partial void LogProcessingWorkBatch(ILogger logger, Guid instanceId, string serviceName, string hostName, int processId,
    int outboxCompletions, int outboxFailures, int inboxCompletions, int inboxFailures, int newOutbox, int newInbox, WorkBatchOptions flags);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Work batch processed: {OutboxWork} outbox work, {InboxWork} inbox work, {PerspectiveWork} perspective work, {SyncResults} sync results"
  )]
  static partial void LogWorkBatchProcessed(ILogger logger, int outboxWork, int inboxWork, int perspectiveWork, int syncResults);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Serializing outbox message: MessageId={MessageId}, Destination={Destination}, EnvelopeType={EnvelopeType}, HopsCount={HopsCount}"
  )]
  static partial void LogSerializingOutboxMessage(ILogger logger, Guid messageId, string? destination, string envelopeType, int hopsCount);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "First outbox message JSON: {Json}"
  )]
  static partial void LogOutboxMessageJson(ILogger logger, string json);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Deserializing envelope: Type={EnvelopeType}, JSON={EnvelopeJson}"
  )]
  static partial void LogDeserializingEnvelope(ILogger logger, string envelopeType, string envelopeJson);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "Deserialized envelope: MessageId={MessageId}, Hops={HopsCount}"
  )]
  static partial void LogDeserializedEnvelope(ILogger logger, Guid messageId, int hopsCount);

  [LoggerMessage(
    Level = LogLevel.Debug,
    Message = "PostgreSQL Notice [{Severity}]: {Message}"
  )]
  static partial void LogPostgresNotice(ILogger logger, string severity, string message);

  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "process_work_batch timed out after {TimeoutSeconds}s for instance {InstanceId} ({ServiceName})"
  )]
  static partial void LogWorkBatchTimedOut(ILogger logger, int timeoutSeconds, Guid instanceId, string serviceName, Exception ex);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "process_work_batch cancelled for instance {InstanceId} ({ServiceName})"
  )]
  static partial void LogWorkBatchCancelled(ILogger logger, Guid instanceId, string serviceName, Exception ex);

  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "process_work_batch failed for instance {InstanceId} ({ServiceName})"
  )]
  static partial void LogWorkBatchFailed(ILogger logger, Guid instanceId, string serviceName, Exception ex);

  #endregion
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
  public Guid? work_id { get; set; }  // NULL for stream-only perspective rows (drain mode)
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
/// Internal DTO for mapping get_stream_events function results.
/// Matches the function's return type structure with snake_case naming (PostgreSQL convention).
/// </summary>
internal class StreamEventRow {
  public Guid out_stream_id { get; set; }
  public Guid out_event_id { get; set; }
  public string out_event_type { get; set; } = string.Empty;
  public string out_event_data { get; set; } = string.Empty;
  public string? out_metadata { get; set; }
  public string? out_scope { get; set; }
  public Guid out_event_work_id { get; set; }
}

/// <summary>
/// DTO for querying perspective cursor info.
/// Uses snake_case to match PostgreSQL column names.
/// </summary>
internal class CursorQueryResult {
  public Guid stream_id { get; set; }
  public string perspective_name { get; set; } = string.Empty;
  public Guid? last_event_id { get; set; }
  public short status { get; set; }
  public Guid? rewind_trigger_event_id { get; set; }
}

/// <summary>
/// Holds all serialized JSON data for the database function call.
/// Extracted from ProcessWorkBatchAsync to reduce cognitive complexity.
/// </summary>
internal sealed record SerializedWorkBatchData(
  string OutboxCompletions,
  string OutboxFailures,
  string InboxCompletions,
  string InboxFailures,
  string PerspectiveEventCompletions,
  string PerspectiveCompletions,
  string PerspectiveFailures,
  string NewOutboxMessages,
  string NewInboxMessages,
  string Metadata,
  string RenewOutboxLeaseIds,
  string RenewInboxLeaseIds,
  string SyncInquiries
);

