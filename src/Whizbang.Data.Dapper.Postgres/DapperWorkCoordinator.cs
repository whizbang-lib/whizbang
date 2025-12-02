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
    Guid[] outboxCompletedIds,
    FailedMessage[] outboxFailedMessages,
    Guid[] inboxCompletedIds,
    FailedMessage[] inboxFailedMessages,
    int leaseSeconds = 300,
    CancellationToken cancellationToken = default
  ) {
    _logger?.LogDebug(
      "Processing work batch for instance {InstanceId}: {OutboxCompleted} outbox completed, {OutboxFailed} outbox failed, {InboxCompleted} inbox completed, {InboxFailed} inbox failed",
      instanceId,
      outboxCompletedIds.Length,
      outboxFailedMessages.Length,
      inboxCompletedIds.Length,
      inboxFailedMessages.Length
    );

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    // Convert failed messages to JSON
    var outboxFailedJson = SerializeFailedMessages(outboxFailedMessages);
    var inboxFailedJson = SerializeFailedMessages(inboxFailedMessages);

    // Execute the process_work_batch function
    var sql = @"
      SELECT * FROM process_work_batch(
        @p_instance_id::uuid,
        @p_outbox_completed_ids::uuid[],
        @p_outbox_failed_messages::jsonb,
        @p_inbox_completed_ids::uuid[],
        @p_inbox_failed_messages::jsonb,
        @p_lease_seconds::int
      )";

    var parameters = new {
      p_instance_id = instanceId,
      p_outbox_completed_ids = outboxCompletedIds,
      p_outbox_failed_messages = outboxFailedJson,
      p_inbox_completed_ids = inboxCompletedIds,
      p_inbox_failed_messages = inboxFailedJson,
      p_lease_seconds = leaseSeconds
    };

    var commandDefinition = new CommandDefinition(
      sql,
      parameters,
      cancellationToken: cancellationToken
    );

    var results = await connection.QueryAsync<WorkBatchRow>(commandDefinition);
    var resultList = results.ToList();

    // Map results to WorkBatch
    var orphanedOutbox = resultList
      .Where(r => r.source == "outbox")
      .Select(r => new OrphanedOutboxMessage {
        MessageId = r.message_id,
        Destination = r.destination!,
        MessageType = r.event_type,
        MessageData = r.event_data,
        Metadata = r.metadata,
        Scope = r.scope,
        Attempts = r.attempts
      })
      .ToList();

    var orphanedInbox = resultList
      .Where(r => r.source == "inbox")
      .Select(r => new OrphanedInboxMessage {
        MessageId = r.message_id,
        MessageType = r.event_type,
        MessageData = r.event_data,
        Metadata = r.metadata,
        Scope = r.scope
      })
      .ToList();

    _logger?.LogInformation(
      "Work batch processed: {OrphanedOutbox} orphaned outbox, {OrphanedInbox} orphaned inbox",
      orphanedOutbox.Count,
      orphanedInbox.Count
    );

    return new WorkBatch {
      OrphanedOutbox = orphanedOutbox,
      OrphanedInbox = orphanedInbox
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
}

/// <summary>
/// Internal DTO for mapping process_work_batch function results.
/// Matches the function's return type structure with snake_case naming (PostgreSQL convention).
/// </summary>
internal class WorkBatchRow {
  public required string source { get; set; }  // 'outbox' or 'inbox'
  public required Guid message_id { get; set; }
  public string? destination { get; set; }  // null for inbox
  public required string event_type { get; set; }
  public required string event_data { get; set; }  // JSON string
  public required string metadata { get; set; }  // JSON string
  public string? scope { get; set; }  // JSON string (nullable)
  public required int attempts { get; set; }
}
