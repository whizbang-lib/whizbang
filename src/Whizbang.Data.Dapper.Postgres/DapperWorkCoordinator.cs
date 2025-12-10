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
/// Dapper implementation of IWorkCoordinator for lease-based work coordination.
/// Uses the PostgreSQL process_work_batch function for atomic operations.
/// </summary>
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

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    // Convert to JSON
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

    // Execute the process_work_batch function
    var sql = @"
      SELECT * FROM process_work_batch(
        @p_instance_id::uuid,
        @p_service_name::varchar,
        @p_host_name::varchar,
        @p_process_id::int,
        @p_metadata::jsonb,
        @p_outbox_completions::jsonb,
        @p_outbox_failures::jsonb,
        @p_inbox_completions::jsonb,
        @p_inbox_failures::jsonb,
        @p_receptor_completions::jsonb,
        @p_receptor_failures::jsonb,
        @p_perspective_completions::jsonb,
        @p_perspective_failures::jsonb,
        @p_new_outbox_messages::jsonb,
        @p_new_inbox_messages::jsonb,
        @p_renew_outbox_lease_ids::jsonb,
        @p_renew_inbox_lease_ids::jsonb,
        @p_lease_seconds::int,
        @p_stale_threshold_seconds::int,
        @p_flags::int,
        @p_partition_count::int,
        @p_max_partitions_per_instance::int
      )";

    var parameters = new {
      p_instance_id = instanceId,
      p_service_name = serviceName,
      p_host_name = hostName,
      p_process_id = processId,
      p_metadata = metadataJson,
      p_outbox_completions = outboxCompletionsJson,
      p_outbox_failures = outboxFailuresJson,
      p_inbox_completions = inboxCompletionsJson,
      p_inbox_failures = inboxFailuresJson,
      p_receptor_completions = receptorCompletionsJson,
      p_receptor_failures = receptorFailuresJson,
      p_perspective_completions = perspectiveCompletionsJson,
      p_perspective_failures = perspectiveFailuresJson,
      p_new_outbox_messages = newOutboxJson,
      p_new_inbox_messages = newInboxJson,
      p_renew_outbox_lease_ids = renewOutboxJson,
      p_renew_inbox_lease_ids = renewInboxJson,
      p_lease_seconds = leaseSeconds,
      p_stale_threshold_seconds = staleThresholdSeconds,
      p_flags = (int)flags,
      p_partition_count = partitionCount,
      p_max_partitions_per_instance = maxPartitionsPerInstance
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
        var envelope = DeserializeEnvelope(r.envelope_type, r.envelope_data);
        // Cast to IMessageEnvelope<object> - envelope type is unknown at deserialization
        var typedEnvelope = envelope as IMessageEnvelope<object>
          ?? throw new InvalidOperationException($"Envelope must implement IMessageEnvelope<object> for message {r.msg_id}");

        return new OutboxWork {
          MessageId = r.msg_id,
          Destination = r.destination!,
          Envelope = typedEnvelope,
          StreamId = r.stream_uuid,
          PartitionNumber = r.partition_num,
          Attempts = r.attempts,
          Status = (MessageProcessingStatus)r.status,
          Flags = (WorkBatchFlags)r.flags,
          SequenceOrder = r.sequence_order
        };
      })
      .ToList();  // OutboxWork is non-generic

    var inboxWork = resultList
      .Where(r => r.source == "inbox")
      .Select(r => {
        var envelope = DeserializeEnvelope(r.envelope_type, r.envelope_data);
        // Cast to IMessageEnvelope<object> - envelope type is unknown at deserialization
        var typedEnvelope = envelope as IMessageEnvelope<object>
          ?? throw new InvalidOperationException($"Envelope must implement IMessageEnvelope<object> for message {r.msg_id}");

        return new InboxWork {
          MessageId = r.msg_id,
          Envelope = typedEnvelope,
          StreamId = r.stream_uuid,
          PartitionNumber = r.partition_num,
          Status = (MessageProcessingStatus)r.status,
          Flags = (WorkBatchFlags)r.flags,
          SequenceOrder = r.sequence_order
        };
      })
      .ToList();  // InboxWork is non-generic

    _logger?.LogInformation(
      "Work batch processed: {OutboxWork} outbox work, {InboxWork} inbox work",
      outboxWork.Count,
      inboxWork.Count
    );

    return new WorkBatch {
      OutboxWork = outboxWork,
      InboxWork = inboxWork
    };
  }

  private string SerializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string SerializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for MessageFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

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
      _logger?.LogDebug("First outbox message JSON: {Json}", json.Length > 500 ? json.Substring(0, 500) + "..." : json);
    }

    return json;
  }

  private string SerializeNewInboxMessages(InboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(InboxMessage[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for InboxMessage[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messages, typeInfo);
  }

  private string SerializeMetadata(Dictionary<string, JsonElement>? metadata) {
    if (metadata == null || metadata.Count == 0) {
      return "{}";  // Return empty JSON object instead of null (matches NOT NULL constraint)
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Dictionary<string, JsonElement>))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Dictionary<string, JsonElement>. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(metadata, typeInfo);
  }

  private string SerializeLeaseRenewals(Guid[] messageIds) {
    if (messageIds.Length == 0) {
      return "[]";
    }

    // Use JsonSerializer with registered type info
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(Guid[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for Guid[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(messageIds, typeInfo);
  }

  private string SerializeReceptorCompletions(ReceptorProcessingCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

  private string SerializeReceptorFailures(ReceptorProcessingFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(ReceptorProcessingFailure[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for ReceptorProcessingFailure[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(failures, typeInfo);
  }

  private string SerializePerspectiveCompletions(PerspectiveCheckpointCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(PerspectiveCheckpointCompletion[]))
      ?? throw new InvalidOperationException("No JsonTypeInfo found for PerspectiveCheckpointCompletion[]. Ensure the type is registered in InfrastructureJsonContext.");
    return JsonSerializer.Serialize(completions, typeInfo);
  }

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
  private IMessageEnvelope DeserializeEnvelope(string envelopeTypeName, string envelopeDataJson) {
    // Log the envelope data for debugging
    _logger?.LogDebug("Deserializing envelope: Type={EnvelopeType}, JSON={EnvelopeJson}", envelopeTypeName, envelopeDataJson);

    // Resolve the envelope type from stored type name
    var envelopeType = Type.GetType(envelopeTypeName)
      ?? throw new InvalidOperationException($"Could not resolve envelope type '{envelopeTypeName}'");

    // Get JsonTypeInfo for the envelope type
    var typeInfo = _jsonOptions.GetTypeInfo(envelopeType)
      ?? throw new InvalidOperationException($"No JsonTypeInfo found for envelope type '{envelopeTypeName}'. Ensure the envelope type is registered via JsonContextRegistry.");

    // Deserialize the complete envelope
    var envelope = JsonSerializer.Deserialize(envelopeDataJson, typeInfo) as IMessageEnvelope
      ?? throw new InvalidOperationException($"Failed to deserialize envelope of type '{envelopeTypeName}'");

    // Log result for debugging
    _logger?.LogDebug("Deserialized envelope: MessageId={MessageId}, Hops={HopsCount}", envelope.MessageId, envelope.Hops.Count);

    return envelope;
  }
}

/// <summary>
/// Internal DTO for mapping process_work_batch function results.
/// Matches the function's return type structure with snake_case naming (PostgreSQL convention).
/// </summary>
internal class WorkBatchRow {
  public required string source { get; set; }  // 'outbox' or 'inbox'
  public required Guid msg_id { get; set; }
  public string? destination { get; set; }  // null for inbox
  public required string envelope_type { get; set; }  // Assembly qualified name of envelope type
  public required string envelope_data { get; set; }  // Complete serialized MessageEnvelope<T> as JSON
  public Guid? stream_uuid { get; set; }  // Stream ID for ordering (matches SQL column name)
  public int? partition_num { get; set; }  // Partition number (matches SQL column name)
  public required int attempts { get; set; }
  public required int status { get; set; }  // MessageProcessingStatus flags
  public required int flags { get; set; }  // WorkBatchFlags
  public required long sequence_order { get; set; }  // Epoch milliseconds for ordering
}

