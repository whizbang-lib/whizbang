using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IWorkCoordinator for lease-based work coordination.
/// Uses the PostgreSQL process_work_batch function for atomic operations.
/// </summary>
/// <typeparam name="TDbContext">DbContext type containing outbox, inbox, and service instance tables</typeparam>
public class EFCoreWorkCoordinator<TDbContext>(
  TDbContext dbContext,
  ILogger<EFCoreWorkCoordinator<TDbContext>>? logger = null
) : IWorkCoordinator
  where TDbContext : DbContext {
  private readonly TDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
  private readonly ILogger<EFCoreWorkCoordinator<TDbContext>>? _logger = logger;

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

    // Convert to JSONB parameters
    var outboxCompletionsJson = SerializeCompletions(outboxCompletions);
    var outboxFailuresJson = SerializeFailures(outboxFailures);
    var inboxCompletionsJson = SerializeCompletions(inboxCompletions);
    var inboxFailuresJson = SerializeFailures(inboxFailures);
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

    var newOutboxParam = PostgresJsonHelper.JsonStringToJsonb(newOutboxJson);
    newOutboxParam.ParameterName = "p_new_outbox_messages";

    var newInboxParam = PostgresJsonHelper.JsonStringToJsonb(newInboxJson);
    newInboxParam.ParameterName = "p_new_inbox_messages";

    var metadataParam = metadataJson != null
      ? PostgresJsonHelper.JsonStringToJsonb(metadataJson)
      : PostgresJsonHelper.NullJsonb();
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

    // Map results to WorkBatch
    var outboxWork = results
      .Where(r => r.Source == "outbox")
      .Select(r => new OutboxWork {
        MessageId = r.MessageId,
        Destination = r.Destination!,
        MessageType = r.EventType,
        MessageData = r.EventData,
        Metadata = r.Metadata,
        Scope = r.Scope,
        StreamId = r.StreamId,
        PartitionNumber = r.PartitionNumber,
        Attempts = r.Attempts,
        Status = (MessageProcessingStatus)r.Status,
        Flags = (WorkBatchFlags)r.Flags,
        SequenceOrder = r.SequenceOrder
      })
      .ToList();

    var inboxWork = results
      .Where(r => r.Source == "inbox")
      .Select(r => new InboxWork {
        MessageId = r.MessageId,
        MessageType = r.EventType,
        MessageData = r.EventData,
        Metadata = r.Metadata,
        Scope = r.Scope,
        StreamId = r.StreamId,
        PartitionNumber = r.PartitionNumber,
        Status = (MessageProcessingStatus)r.Status,
        Flags = (WorkBatchFlags)r.Flags,
        SequenceOrder = r.SequenceOrder
      })
      .ToList();

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

  private static string SerializeCompletions(MessageCompletion[] completions) {
    if (completions.Length == 0) {
      return "[]";
    }

    // Simple JSON array serialization (AOT-safe)
    // Property names must match PostgreSQL function expectations (camelCase)
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
    // Property names must match PostgreSQL function expectations (camelCase)
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
    // Property names must match PostgreSQL function expectations (camelCase)
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
    // Property names must match PostgreSQL function expectations (camelCase)
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
/// Matches the function's return type structure.
/// </summary>
internal class WorkBatchRow {
  [Column("source")]
  public required string Source { get; set; }  // 'outbox' or 'inbox'

  [Column("msg_id")]
  public required Guid MessageId { get; set; }

  [Column("destination")]
  public string? Destination { get; set; }  // null for inbox

  [Column("event_type")]
  public required string EventType { get; set; }

  [Column("event_data")]
  public required string EventData { get; set; }  // JSON string

  [Column("metadata")]
  public required string Metadata { get; set; }  // JSON string

  [Column("scope")]
  public string? Scope { get; set; }  // JSON string (nullable)

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
