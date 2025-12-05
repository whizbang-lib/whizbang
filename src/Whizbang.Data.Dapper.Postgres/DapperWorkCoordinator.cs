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
    Guid[] outboxCompletedIds,
    FailedMessage[] outboxFailedMessages,
    Guid[] inboxCompletedIds,
    FailedMessage[] inboxFailedMessages,
    int leaseSeconds = 300,
    int staleThresholdSeconds = 600,
    CancellationToken cancellationToken = default
  ) {
    _logger?.LogDebug(
      "Processing work batch for instance {InstanceId} ({ServiceName}@{HostName}:{ProcessId}): {OutboxCompleted} outbox completed, {OutboxFailed} outbox failed, {InboxCompleted} inbox completed, {InboxFailed} inbox failed",
      instanceId,
      serviceName,
      hostName,
      processId,
      outboxCompletedIds.Length,
      outboxFailedMessages.Length,
      inboxCompletedIds.Length,
      inboxFailedMessages.Length
    );

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    // Convert failed messages and metadata to JSON
    var outboxFailedJson = SerializeFailedMessages(outboxFailedMessages);
    var inboxFailedJson = SerializeFailedMessages(inboxFailedMessages);
    var metadataJson = SerializeMetadata(metadata);

    // Execute the process_work_batch function
    var sql = @"
      SELECT * FROM process_work_batch(
        @p_instance_id::uuid,
        @p_service_name::varchar,
        @p_host_name::varchar,
        @p_process_id::int,
        @p_metadata::jsonb,
        @p_outbox_completed_ids::uuid[],
        @p_outbox_failed_messages::jsonb,
        @p_inbox_completed_ids::uuid[],
        @p_inbox_failed_messages::jsonb,
        @p_lease_seconds::int,
        @p_stale_threshold_seconds::int
      )";

    var parameters = new {
      p_instance_id = instanceId,
      p_service_name = serviceName,
      p_host_name = hostName,
      p_process_id = processId,
      p_metadata = metadataJson,
      p_outbox_completed_ids = outboxCompletedIds,
      p_outbox_failed_messages = outboxFailedJson,
      p_inbox_completed_ids = inboxCompletedIds,
      p_inbox_failed_messages = inboxFailedJson,
      p_lease_seconds = leaseSeconds,
      p_stale_threshold_seconds = staleThresholdSeconds
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
        Attempts = r.attempts
      })
      .ToList();

    var inboxWork = resultList
      .Where(r => r.source == "inbox")
      .Select(r => new InboxWork {
        MessageId = r.msg_id,
        MessageType = r.event_type,
        MessageData = r.event_data,
        Metadata = r.metadata,
        Scope = r.scope
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

  private static string SerializeFailedMessages(FailedMessage[] messages) {
    if (messages.Length == 0) {
      return "[]";
    }

    // Simple JSON array serialization (AOT-safe)
    var items = messages.Select(m =>
      $"{{\"MessageId\":\"{m.MessageId}\",\"Error\":\"{EscapeJson(m.Error)}\"}}"
    );
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
  public required int attempts { get; set; }
}
