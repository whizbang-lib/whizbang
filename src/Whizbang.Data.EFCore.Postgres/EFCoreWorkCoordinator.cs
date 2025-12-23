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
public class EFCoreWorkCoordinator<TDbContext>(
  TDbContext dbContext,
  JsonSerializerOptions jsonOptions,
  ILogger<EFCoreWorkCoordinator<TDbContext>>? logger = null
) : IWorkCoordinator
  where TDbContext : DbContext {
  private readonly TDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
  private readonly ILogger<EFCoreWorkCoordinator<TDbContext>>? _logger = logger;

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

    // Convert to JSONB parameters
    var outboxCompletionsJson = _serializeCompletions(outboxCompletions);
    var outboxFailuresJson = _serializeFailures(outboxFailures);
    var inboxCompletionsJson = _serializeCompletions(inboxCompletions);
    var inboxFailuresJson = _serializeFailures(inboxFailures);
    var receptorCompletionsJson = _serializeReceptorCompletions(receptorCompletions);
    var receptorFailuresJson = _serializeReceptorFailures(receptorFailures);
    var perspectiveCompletionsJson = _serializePerspectiveCompletions(perspectiveCompletions);
    var perspectiveFailuresJson = _serializePerspectiveFailures(perspectiveFailures);
    var newOutboxJson = _serializeNewOutboxMessages(newOutboxMessages);
    var newInboxJson = _serializeNewInboxMessages(newInboxMessages);
    var metadataJson = _serializeMetadata(metadata);
    var renewOutboxJson = _serializeLeaseRenewals(renewOutboxLeaseIds);
    var renewInboxJson = _serializeLeaseRenewals(renewInboxLeaseIds);

    var outboxCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(outboxCompletionsJson);
    outboxCompletionsParam.ParameterName = "p_outbox_completions";

    var outboxFailuresParam = PostgresJsonHelper.JsonStringToJsonb(outboxFailuresJson);
    outboxFailuresParam.ParameterName = "p_outbox_failures";

    var inboxCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(inboxCompletionsJson);
    inboxCompletionsParam.ParameterName = "p_inbox_completions";

    var inboxFailuresParam = PostgresJsonHelper.JsonStringToJsonb(inboxFailuresJson);
    inboxFailuresParam.ParameterName = "p_inbox_failures";

    var receptorCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(receptorCompletionsJson);
    receptorCompletionsParam.ParameterName = "p_receptor_completions";

    var receptorFailuresParam = PostgresJsonHelper.JsonStringToJsonb(receptorFailuresJson);
    receptorFailuresParam.ParameterName = "p_receptor_failures";

    var perspectiveCompletionsParam = PostgresJsonHelper.JsonStringToJsonb(perspectiveCompletionsJson);
    perspectiveCompletionsParam.ParameterName = "p_perspective_completions";

    var perspectiveFailuresParam = PostgresJsonHelper.JsonStringToJsonb(perspectiveFailuresJson);
    perspectiveFailuresParam.ParameterName = "p_perspective_failures";

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

    // Execute the process_work_batch function
    // Note: Type casts removed because NpgsqlParameter.NpgsqlDbType handles typing
    var sql = @"
      SELECT * FROM process_work_batch(
        @p_instance_id,
        @p_service_name,
        @p_host_name,
        @p_process_id,
        @p_metadata,
        @p_outbox_completions,
        @p_outbox_failures,
        @p_inbox_completions,
        @p_inbox_failures,
        @p_receptor_completions,
        @p_receptor_failures,
        @p_perspective_completions,
        @p_perspective_failures,
        @p_new_outbox_messages,
        @p_new_inbox_messages,
        @p_renew_outbox_lease_ids,
        @p_renew_inbox_lease_ids,
        @p_lease_seconds,
        @p_stale_threshold_seconds,
        @p_flags,
        @p_partition_count
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
        new Npgsql.NpgsqlParameter("p_instance_id", instanceId),
        new Npgsql.NpgsqlParameter("p_service_name", serviceName),
        new Npgsql.NpgsqlParameter("p_host_name", hostName),
        new Npgsql.NpgsqlParameter("p_process_id", processId),
        metadataParam,
        outboxCompletionsParam,
        outboxFailuresParam,
        inboxCompletionsParam,
        inboxFailuresParam,
        receptorCompletionsParam,
        receptorFailuresParam,
        perspectiveCompletionsParam,
        perspectiveFailuresParam,
        newOutboxParam,
        newInboxParam,
        renewOutboxParam,
        renewInboxParam,
        new Npgsql.NpgsqlParameter("p_lease_seconds", leaseSeconds),
        new Npgsql.NpgsqlParameter("p_stale_threshold_seconds", staleThresholdSeconds),
        new Npgsql.NpgsqlParameter("p_flags", (int)flags),
        new Npgsql.NpgsqlParameter("p_partition_count", partitionCount)
      )
      .ToListAsync(cancellationToken);

    // Process results and return work batch
    return _processResults(results);
  }

  /// <summary>
  /// Processes the query results and maps them to a WorkBatch
  /// </summary>
  private WorkBatch _processResults(List<WorkBatchRow> results) {

    // Map results to WorkBatch - deserialize envelopes from database
    var outboxWork = results
      .Where(r => r.Source == "outbox")
      .Select(r => {
        var envelope = _deserializeEnvelope(r.EnvelopeType, r.EnvelopeData);
        // Cast to IMessageEnvelope<JsonElement> - envelope is always deserialized as MessageEnvelope<JsonElement>
        var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
          ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.MessageId}");

        return new OutboxWork {
          MessageId = r.MessageId,
          Destination = r.Destination!,
          Envelope = jsonEnvelope,
          StreamId = r.StreamId,
          PartitionNumber = r.PartitionNumber,
          Attempts = r.Attempts,
          Status = (MessageProcessingStatus)r.Status,
          Flags = (WorkBatchFlags)r.Flags,
          SequenceOrder = r.SequenceOrder
        };
      })
      .ToList();

    var inboxWork = results
      .Where(r => r.Source == "inbox")
      .Select(r => {
        var envelope = _deserializeEnvelope(r.EnvelopeType, r.EnvelopeData);
        // Cast to IMessageEnvelope<JsonElement> - envelope is always deserialized as MessageEnvelope<JsonElement>
        var jsonEnvelope = envelope as IMessageEnvelope<JsonElement>
          ?? throw new InvalidOperationException($"Envelope must be IMessageEnvelope<JsonElement> for message {r.MessageId}");

        return new InboxWork {
          MessageId = r.MessageId,
          Envelope = jsonEnvelope,
          MessageType = r.EnvelopeType,  // Use envelope_type until event_type is added to WorkBatchRow
          StreamId = r.StreamId,
          PartitionNumber = r.PartitionNumber,
          Status = (MessageProcessingStatus)r.Status,
          Flags = (WorkBatchFlags)r.Flags,
          SequenceOrder = r.SequenceOrder
        };
      })
      .ToList();

    var perspectiveWork = results
      .Where(r => r.Source == "perspective")
      .Select(r => new PerspectiveWork {
        StreamId = r.StreamId ?? throw new InvalidOperationException($"Perspective work must have StreamId"),
        PerspectiveName = r.Destination ?? throw new InvalidOperationException($"Perspective work must have PerspectiveName in destination field"),
        LastProcessedEventId = r.LastEventId,  // From wh_perspective_checkpoints.last_event_id
        Status = (PerspectiveProcessingStatus)r.Status,
        PartitionNumber = r.PartitionNumber,
        Flags = (WorkBatchFlags)r.Flags
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
  /// Serializes receptor processing completions to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string _serializeReceptorCompletions(ReceptorProcessingCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <summary>
  /// Serializes receptor processing failures to JSON for database storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string _serializeReceptorFailures(ReceptorProcessingFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
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
  [Column("source")]
  public required string Source { get; set; }  // 'outbox' or 'inbox'

  [Column("msg_id")]
  public required Guid MessageId { get; set; }

  [Column("destination")]
  public string? Destination { get; set; }  // null for inbox

  [Column("envelope_type")]
  public required string EnvelopeType { get; set; }  // Assembly qualified name of envelope type

  [Column("envelope_data")]
  public required string EnvelopeData { get; set; }  // Complete serialized MessageEnvelope<T> as JSON

  [Column("stream_uuid")]
  public Guid? StreamId { get; set; }  // Stream ID for ordering

  [Column("partition_num")]
  public int? PartitionNumber { get; set; }  // Partition number

  [Column("attempts")]
  public int Attempts { get; set; }

  [Column("status")]
  public required int Status { get; set; }  // MessageProcessingStatus flags

  [Column("flags")]
  public required int Flags { get; set; }  // WorkBatchFlags

  [Column("sequence_order")]
  public required long SequenceOrder { get; set; }  // Epoch milliseconds for ordering

  [Column("last_event_id")]
  public Guid? LastEventId { get; set; }  // Last processed event ID (perspective work only)
}
