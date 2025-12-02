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

    // Convert arrays to PostgreSQL-compatible parameters
    var outboxCompletedIdsParam = PostgresArrayHelper.ToUuidArray(outboxCompletedIds);
    var inboxCompletedIdsParam = PostgresArrayHelper.ToUuidArray(inboxCompletedIds);

    // Convert failed messages to JSONB
    var outboxFailedJson = SerializeFailedMessages(outboxFailedMessages);
    var inboxFailedJson = SerializeFailedMessages(inboxFailedMessages);

    var outboxFailedParam = PostgresJsonHelper.JsonStringToJsonb(outboxFailedJson);
    var inboxFailedParam = PostgresJsonHelper.JsonStringToJsonb(inboxFailedJson);

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

    var results = await _dbContext.Database
      .SqlQueryRaw<WorkBatchRow>(
        sql,
        new Npgsql.NpgsqlParameter("p_instance_id", instanceId),
        outboxCompletedIdsParam,
        outboxFailedParam,
        inboxCompletedIdsParam,
        inboxFailedParam,
        new Npgsql.NpgsqlParameter("p_lease_seconds", leaseSeconds)
      )
      .ToListAsync(cancellationToken);

    // Map results to WorkBatch
    var orphanedOutbox = results
      .Where(r => r.Source == "outbox")
      .Select(r => new OrphanedOutboxMessage {
        MessageId = r.MessageId,
        Destination = r.Destination!,
        MessageType = r.EventType,
        MessageData = r.EventData,
        Metadata = r.Metadata,
        Scope = r.Scope,
        Attempts = r.Attempts
      })
      .ToList();

    var orphanedInbox = results
      .Where(r => r.Source == "inbox")
      .Select(r => new OrphanedInboxMessage {
        MessageId = r.MessageId,
        MessageType = r.EventType,
        MessageData = r.EventData,
        Metadata = r.Metadata,
        Scope = r.Scope
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
/// Matches the function's return type structure.
/// </summary>
internal class WorkBatchRow {
  public required string Source { get; set; }  // 'outbox' or 'inbox'
  public required Guid MessageId { get; set; }
  public string? Destination { get; set; }  // null for inbox
  public required string EventType { get; set; }
  public required string EventData { get; set; }  // JSON string
  public required string Metadata { get; set; }  // JSON string
  public string? Scope { get; set; }  // JSON string (nullable)
  public required int Attempts { get; set; }
}
