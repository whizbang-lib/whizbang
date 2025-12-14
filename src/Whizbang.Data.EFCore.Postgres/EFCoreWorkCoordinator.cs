using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    int maxPartitionsPerInstance = 100,
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
    var outboxCompletionsJson = SerializeCompletions(outboxCompletions);
    var outboxFailuresJson = SerializeFailures(outboxFailures);
    var inboxCompletionsJson = SerializeCompletions(inboxCompletions);
    var inboxFailuresJson = SerializeFailures(inboxFailures);
    var receptorCompletionsJson = SerializeReceptorCompletions(receptorCompletions);
    var receptorFailuresJson = SerializeReceptorFailures(receptorFailures);
    var perspectiveCompletionsJson = SerializePerspectiveCompletions(perspectiveCompletions);
    var perspectiveFailuresJson = SerializePerspectiveFailures(perspectiveFailures);
    var newOutboxJson = SerializeNewOutboxMessages(newOutboxMessages);
    var newInboxJson = SerializeNewInboxMessages(newInboxMessages);
    var metadataJson = SerializeMetadata(metadata);
    var renewOutboxJson = SerializeLeaseRenewals(renewOutboxLeaseIds);
    var renewInboxJson = SerializeLeaseRenewals(renewInboxLeaseIds);

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
        @p_partition_count,
        @p_max_partitions_per_instance
      )";

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
        new Npgsql.NpgsqlParameter("p_partition_count", partitionCount),
        new Npgsql.NpgsqlParameter("p_max_partitions_per_instance", maxPartitionsPerInstance)
      )
      .ToListAsync(cancellationToken);

    // Map results to WorkBatch - deserialize envelopes from database
    var outboxWork = results
      .Where(r => r.Source == "outbox")
      .Select(r => {
        var envelope = DeserializeEnvelope(r.EnvelopeType, r.EnvelopeData);
        // Cast to IMessageEnvelope<object> - envelope type is unknown at deserialization
        var typedEnvelope = envelope as IMessageEnvelope<object>
          ?? throw new InvalidOperationException($"Envelope must implement IMessageEnvelope<object> for message {r.MessageId}");

        return new OutboxWork {
          MessageId = r.MessageId,
          Destination = r.Destination!,
          Envelope = typedEnvelope,
          StreamId = r.StreamId,
          PartitionNumber = r.PartitionNumber,
          Attempts = r.Attempts,
          Status = (MessageProcessingStatus)r.Status,
          Flags = (WorkBatchFlags)r.Flags,
          SequenceOrder = r.SequenceOrder
        };
      })
      .ToList();  // OutboxWork is non-generic

    var inboxWork = results
      .Where(r => r.Source == "inbox")
      .Select(r => {
        var envelope = DeserializeEnvelope(r.EnvelopeType, r.EnvelopeData);
        // Cast to IMessageEnvelope<object> - envelope type is unknown at deserialization
        var typedEnvelope = envelope as IMessageEnvelope<object>
          ?? throw new InvalidOperationException($"Envelope must implement IMessageEnvelope<object> for message {r.MessageId}");

        return new InboxWork {
          MessageId = r.MessageId,
          Envelope = typedEnvelope,
          StreamId = r.StreamId,
          PartitionNumber = r.PartitionNumber,
          Status = (MessageProcessingStatus)r.Status,
          Flags = (WorkBatchFlags)r.Flags,
          SequenceOrder = r.SequenceOrder
        };
      })
      .ToList();  // InboxWork is non-generic

    // Only log when there's actual work to report
    if (outboxWork.Count > 0 || inboxWork.Count > 0) {
      _logger?.LogInformation(
        "Work batch processed: {OutboxWork} outbox work, {InboxWork} inbox work",
        outboxWork.Count,
        inboxWork.Count
      );
    }

    return new WorkBatch {
      OutboxWork = outboxWork,
      InboxWork = inboxWork
    };
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesOutboxMessages_MarksAsPublishedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_CompletesInboxMessages_MarksAsCompletedAsync</tests>
  private string SerializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsOutboxMessages_MarksAsFailedWithErrorAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailedMessageWithSpecialCharacters_EscapesJsonCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_FailsInboxMessages_MarksAsFailedAsync</tests>
  private string SerializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
  private string SerializeNewOutboxMessages(OutboxMessage[] messages) {
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

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_MixedOperations_HandlesAllCorrectlyAsync</tests>
  private string SerializeNewInboxMessages(InboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for InboxMessage[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_WithMetadata_StoresMetadataCorrectlyAsync</tests>
  private string SerializeMetadata(Dictionary<string, JsonElement>? metadata) {
    if (metadata == null || metadata.Count == 0) {
      return "{}";  // Return empty JSON object instead of null (matches NOT NULL constraint)
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(metadata, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string SerializeLeaseRenewals(Guid[] messageIds) {
    if (messageIds.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Guid[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messageIds, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string SerializeReceptorCompletions(ReceptorProcessingCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string SerializeReceptorFailures(ReceptorProcessingFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string SerializePerspectiveCompletions(PerspectiveCheckpointCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCheckpointCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCheckpointCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_NoWork_UpdatesHeartbeatAsync</tests>
  private string SerializePerspectiveFailures(PerspectiveCheckpointFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCheckpointFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCheckpointFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  /// <summary>
  /// Deserializes envelope from database envelope_type and envelope_data columns.
  /// </summary>
  /// <remarks>
  /// TODO (Phase 4): IL2057 - Type.GetType() is not AOT-compatible.
  /// Use generated type registry from core library source generators instead.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_ReturnedWork_HasCorrectPascalCaseColumnMappingAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_JsonbColumns_ReturnAsTextCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedOutboxMessages_ReturnsExpiredLeasesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorTests.cs:ProcessWorkBatchAsync_RecoversOrphanedInboxMessages_ReturnsExpiredLeasesAsync</tests>
  private IMessageEnvelope DeserializeEnvelope(string envelopeTypeName, string envelopeDataJson) {
    _logger?.LogDebug("Deserializing envelope: Type={EnvelopeType}, Data (first 500 chars)={EnvelopeData}",
      envelopeTypeName,
      envelopeDataJson.Length > 500 ? envelopeDataJson.Substring(0, 500) + "..." : envelopeDataJson);

    // Resolve the envelope type from stored type name
    var envelopeType = Type.GetType(envelopeTypeName)
      ?? throw new InvalidOperationException($"Could not resolve envelope type '{envelopeTypeName}'");

    // Get JsonTypeInfo for the envelope type
    var typeInfo = _jsonOptions.GetTypeInfo(envelopeType)
      ?? throw new InvalidOperationException($"No JsonTypeInfo found for envelope type '{envelopeTypeName}'. Ensure the envelope type is registered via JsonContextRegistry.");

    // Deserialize the complete envelope
    var envelope = JsonSerializer.Deserialize(envelopeDataJson, typeInfo) as IMessageEnvelope
      ?? throw new InvalidOperationException($"Failed to deserialize envelope of type '{envelopeTypeName}'");

    _logger?.LogDebug("Deserialized envelope: MessageId={MessageId}, HopsCount={HopsCount}",
      envelope.MessageId,
      envelope.Hops?.Count ?? 0);

    return envelope;
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
}
