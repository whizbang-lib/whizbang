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

    // Convert arrays to PostgreSQL-compatible parameters
    var outboxCompletedIdsParam = PostgresArrayHelper.ToUuidArray(outboxCompletedIds);
    outboxCompletedIdsParam.ParameterName = "p_outbox_completed_ids";

    var inboxCompletedIdsParam = PostgresArrayHelper.ToUuidArray(inboxCompletedIds);
    inboxCompletedIdsParam.ParameterName = "p_inbox_completed_ids";

    // Convert failed messages and metadata to JSONB
    var outboxFailedJson = SerializeFailedMessages(outboxFailedMessages);
    var inboxFailedJson = SerializeFailedMessages(inboxFailedMessages);
    var metadataJson = SerializeMetadata(metadata);

    var outboxFailedParam = PostgresJsonHelper.JsonStringToJsonb(outboxFailedJson);
    outboxFailedParam.ParameterName = "p_outbox_failed_messages";

    var inboxFailedParam = PostgresJsonHelper.JsonStringToJsonb(inboxFailedJson);
    inboxFailedParam.ParameterName = "p_inbox_failed_messages";

    var metadataParam = metadataJson != null
      ? PostgresJsonHelper.JsonStringToJsonb(metadataJson)
      : PostgresJsonHelper.NullJsonb();
    metadataParam.ParameterName = "p_metadata";

    // Execute the process_work_batch function
    // Note: Type casts removed because NpgsqlParameter.NpgsqlDbType handles typing
    var sql = @"
      SELECT * FROM process_work_batch(
        @p_instance_id,
        @p_service_name,
        @p_host_name,
        @p_process_id,
        @p_metadata,
        @p_outbox_completed_ids,
        @p_outbox_failed_messages,
        @p_inbox_completed_ids,
        @p_inbox_failed_messages,
        @p_lease_seconds,
        @p_stale_threshold_seconds
      )";

    var results = await _dbContext.Database
      .SqlQueryRaw<WorkBatchRow>(
        sql,
        new Npgsql.NpgsqlParameter("p_instance_id", instanceId),
        new Npgsql.NpgsqlParameter("p_service_name", serviceName),
        new Npgsql.NpgsqlParameter("p_host_name", hostName),
        new Npgsql.NpgsqlParameter("p_process_id", processId),
        metadataParam,
        outboxCompletedIdsParam,
        outboxFailedParam,
        inboxCompletedIdsParam,
        inboxFailedParam,
        new Npgsql.NpgsqlParameter("p_lease_seconds", leaseSeconds),
        new Npgsql.NpgsqlParameter("p_stale_threshold_seconds", staleThresholdSeconds)
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
        Attempts = r.Attempts
      })
      .ToList();

    var inboxWork = results
      .Where(r => r.Source == "inbox")
      .Select(r => new InboxWork {
        MessageId = r.MessageId,
        MessageType = r.EventType,
        MessageData = r.EventData,
        Metadata = r.Metadata,
        Scope = r.Scope
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
