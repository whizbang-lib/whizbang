using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Dapper implementation of IWorkCoordinator for lease-based work coordination.
/// Uses the PostgreSQL process_work_batch function for atomic operations.
/// </summary>
public class DapperWorkCoordinator(
  string connectionString,
  ILogger<DapperWorkCoordinator>? logger = null
) : IWorkCoordinator {
  private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
  private readonly ILogger<DapperWorkCoordinator>? _logger = logger;

  public async Task<WorkBatch> ProcessWorkBatchAsync(
    Guid instanceId,
    string serviceName,
    string hostName,
    int processId,
    Dictionary<string, object>? metadata,
    MessageCompletion[] outboxCompletions,
    MessageFailure[] outboxFailures,
    MessageCompletion[] inboxCompletions,
    MessageFailure[] inboxFailures,
    NewOutboxMessage[] newOutboxMessages,
    NewInboxMessage[] newInboxMessages,
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

    // Map results to WorkBatch
    var outboxWork = resultList
      .Where(r => r.source == "outbox")
      .Select(r => new OutboxWork {
        MessageId = r.msg_id,
        Destination = r.destination!,
        MessageType = r.event_type,
        MessageData = r.event_data,
        Metadata = r.metadata,
        Scope = r.scope,
        StreamId = r.stream_uuid,
        PartitionNumber = r.partition_num,
        Attempts = r.attempts,
        Status = (MessageProcessingStatus)r.status,
        Flags = (WorkBatchFlags)r.flags,
        SequenceOrder = r.sequence_order
      })
      .ToList();

    var inboxWork = resultList
      .Where(r => r.source == "inbox")
      .Select(r => new InboxWork {
        MessageId = r.msg_id,
        MessageType = r.event_type,
        MessageData = r.event_data,
        Metadata = r.metadata,
        Scope = r.scope,
        StreamId = r.stream_uuid,
        PartitionNumber = r.partition_num,
        Status = (MessageProcessingStatus)r.status,
        Flags = (WorkBatchFlags)r.flags,
        SequenceOrder = r.sequence_order
      })
      .ToList();

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

  private static string SerializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    // Simple JSON array serialization (AOT-safe)
    var items = completions.Select(c =>
      $"{{\"messageId\":\"{c.MessageId}\",\"status\":{(int)c.Status}}}"
    );
    return $"[{string.Join(",", items)}]";
  }

  private static string SerializeFailures(MessageFailure[] failures) {
    if (failures.Length == 0) {
      return "[]";
    }

    // Simple JSON array serialization (AOT-safe)
    var items = failures.Select(f =>
      $"{{\"messageId\":\"{f.MessageId}\",\"completedStatus\":{(int)f.CompletedStatus},\"error\":\"{EscapeJson(f.Error)}\"}}"
    );
    return $"[{string.Join(",", items)}]";
  }

  private static string SerializeNewOutboxMessages(NewOutboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Simple JSON array serialization (AOT-safe)
    var items = messages.Select(m => {
      var streamId = m.StreamId.HasValue ? $"\"{m.StreamId.Value}\"" : "null";
      var scope = m.Scope != null ? m.Scope : "null";
      var isEvent = m.IsEvent.ToString().ToLowerInvariant();
      return $"{{\"messageId\":\"{m.MessageId}\",\"destination\":\"{EscapeJson(m.Destination)}\",\"eventType\":\"{EscapeJson(m.EventType)}\",\"eventData\":{m.EventData},\"metadata\":{m.Metadata},\"scope\":{scope},\"streamId\":{streamId},\"isEvent\":{isEvent}}}";
    });
    return $"[{string.Join(",", items)}]";
  }

  private static string SerializeNewInboxMessages(NewInboxMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Simple JSON array serialization (AOT-safe)
    var items = messages.Select(m => {
      var streamId = m.StreamId.HasValue ? $"\"{m.StreamId.Value}\"" : "null";
      var scope = m.Scope != null ? m.Scope : "null";
      var isEvent = m.IsEvent.ToString().ToLowerInvariant();
      return $"{{\"messageId\":\"{m.MessageId}\",\"handlerName\":\"{EscapeJson(m.HandlerName)}\",\"eventType\":\"{EscapeJson(m.EventType)}\",\"eventData\":{m.EventData},\"metadata\":{m.Metadata},\"scope\":{scope},\"streamId\":{streamId},\"isEvent\":{isEvent}}}";
    });
    return $"[{string.Join(",", items)}]";
  }

  private static string EscapeJson(string value) {
    return value
      .Replace("\\", "\\\\")
      .Replace("\"", "\\\"")
      .Replace("\n", "\\n")
      .Replace("\r", "\\r")
      .Replace("\t", "\\t");
  }

  private static string? SerializeMetadata(Dictionary<string, object>? metadata) {
    if (metadata == null || metadata.Count == 0) {
      return null;
    }

    // Simple JSON object serialization (AOT-safe)
    var items = metadata.Select(kvp => {
      var value = kvp.Value switch {
        string s => $"\"{EscapeJson(s)}\"",
        bool b => b.ToString().ToLowerInvariant(),
        null => "null",
        _ => kvp.Value.ToString()
      };
      return $"\"{EscapeJson(kvp.Key)}\":{value}";
    });
    return $"{{{string.Join(",", items)}}}";
  }

  private static string SerializeLeaseRenewals(Guid[] messageIds) {
    if (messageIds.Length == 0) {
      return "[]";
    }

    // Simple JSON array of UUID strings (AOT-safe)
    // PostgreSQL expects: ["uuid1", "uuid2", ...]
    var items = messageIds.Select(id => $"\"{id}\"");
    return $"[{string.Join(",", items)}]";
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
  public required string event_type { get; set; }
  public required string event_data { get; set; }  // JSON string
  public required string metadata { get; set; }  // JSON string
  public string? scope { get; set; }  // JSON string (nullable)
  public Guid? stream_uuid { get; set; }  // Stream ID for ordering (matches SQL column name)
  public int? partition_num { get; set; }  // Partition number (matches SQL column name)
  public required int attempts { get; set; }
  public required int status { get; set; }  // MessageProcessingStatus flags
  public required int flags { get; set; }  // WorkBatchFlags
  public required long sequence_order { get; set; }  // Epoch milliseconds for ordering
}
