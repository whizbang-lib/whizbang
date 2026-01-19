using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL implementation of IMessageQueue with atomic enqueue-and-lease.
/// Provides distributed exactly-once processing via transactional queue + processed_messages table.
/// </summary>
/// <tests>No tests found</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Message queue diagnostic logging - I/O bound database operations where LoggerMessage overhead isn't justified")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name correctly represents a message queue implementation of IMessageQueue interface")]
public class DapperPostgresMessageQueue(
  IDbConnectionFactory connectionFactory,
  ILogger<DapperPostgresMessageQueue> logger) : IMessageQueue {
  private readonly IDbConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  private readonly ILogger<DapperPostgresMessageQueue> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <summary>
  /// Atomically checks if a message has been processed, enqueues it if not, and immediately leases it for processing.
  /// </summary>
  /// <tests>No tests found</tests>
  public async Task<bool> EnqueueAndLeaseAsync(
      QueuedMessage message,
      string instanceId,
      TimeSpan leaseDuration,
      CancellationToken cancellationToken = default) {

    using var conn = await _connectionFactory.CreateConnectionAsync(cancellationToken);

    var leaseExpiration = DateTimeOffset.UtcNow.Add(leaseDuration);

    // Atomic transaction: check processed_messages, insert into queue with lease
    using var txn = conn.BeginTransaction();

    try {
      // 1. Check if already processed (idempotency)
      var alreadyProcessed = await conn.ExecuteScalarAsync<bool>(@"
        SELECT EXISTS(
          SELECT 1 FROM whizbang_processed_messages
          WHERE message_id = @MessageId
        )",
        new { message.MessageId },
        txn
      );

      if (alreadyProcessed) {
        _logger.LogDebug("Message {MessageId} already processed, skipping", message.MessageId);
        txn.Commit();
        return false;  // Already processed
      }

      // 2. Insert into queue with immediate lease
      await conn.ExecuteAsync(@"
        INSERT INTO whizbang_message_queue (
          message_id,
          event_type,
          event_data,
          metadata,
          received_at,
          leased_by,
          lease_expires_at
        ) VALUES (
          @MessageId,
          @EventType,
          @EventData::jsonb,
          @Metadata::jsonb,
          NOW(),
          @InstanceId,
          @LeaseExpiration
        )
        ON CONFLICT (message_id) DO NOTHING",
        new {
          message.MessageId,
          message.EventType,
          message.EventData,
          message.Metadata,
          InstanceId = instanceId,
          LeaseExpiration = leaseExpiration
        },
        txn
      );

      txn.Commit();

      _logger.LogDebug(
        "Enqueued and leased message {MessageId} for instance {InstanceId} until {LeaseExpiration}",
        message.MessageId,
        instanceId,
        leaseExpiration
      );

      return true;  // Newly enqueued and leased
    } catch {
      txn.Rollback();
      throw;
    }
  }

  /// <summary>
  /// Marks a message as processed and removes it from the queue atomically.
  /// </summary>
  /// <tests>No tests found</tests>
  public async Task CompleteAsync(
      Guid messageId,
      string instanceId,
      string handlerName,
      CancellationToken cancellationToken = default) {

    using var conn = await _connectionFactory.CreateConnectionAsync(cancellationToken);

    // Atomic transaction: insert into processed_messages, delete from queue
    using var txn = conn.BeginTransaction();

    try {
      // 1. Mark as processed (idempotency table)
      await conn.ExecuteAsync(@"
        INSERT INTO whizbang_processed_messages (
          message_id,
          handler_name,
          processed_at,
          processed_by,
          event_type
        )
        SELECT
          @MessageId,
          @HandlerName,
          NOW(),
          @InstanceId,
          event_type
        FROM whizbang_message_queue
        WHERE message_id = @MessageId
        ON CONFLICT (message_id) DO NOTHING",
        new { MessageId = messageId, HandlerName = handlerName, InstanceId = instanceId },
        txn
      );

      // 2. Delete from queue
      await conn.ExecuteAsync(@"
        DELETE FROM whizbang_message_queue
        WHERE message_id = @MessageId
          AND leased_by = @InstanceId",
        new { MessageId = messageId, InstanceId = instanceId },
        txn
      );

      txn.Commit();

      _logger.LogDebug("Completed processing of message {MessageId}", messageId);
    } catch {
      txn.Rollback();
      throw;
    }
  }

  /// <summary>
  /// Atomically leases orphaned messages (unleased or with expired leases) for crash recovery.
  /// </summary>
  /// <tests>No tests found</tests>
  public async Task<System.Collections.Generic.IReadOnlyList<QueuedMessage>> LeaseOrphanedMessagesAsync(
      string instanceId,
      int maxCount,
      TimeSpan leaseDuration,
      CancellationToken cancellationToken = default) {

    using var conn = await _connectionFactory.CreateConnectionAsync(cancellationToken);

    var leaseExpiration = DateTimeOffset.UtcNow.Add(leaseDuration);

    // Atomic lease acquisition of orphaned messages using FOR UPDATE SKIP LOCKED
    var messages = await conn.QueryAsync<QueuedMessage>(@"
      UPDATE whizbang_message_queue
      SET
        leased_by = @InstanceId,
        lease_expires_at = @LeaseExpiration
      WHERE message_id IN (
        SELECT message_id
        FROM whizbang_message_queue
        WHERE leased_by IS NULL
           OR lease_expires_at < @Now
        ORDER BY received_at ASC
        LIMIT @MaxCount
        FOR UPDATE SKIP LOCKED
      )
      RETURNING
        message_id AS MessageId,
        event_type AS EventType,
        event_data::text AS EventData,
        metadata::text AS Metadata",
      new {
        InstanceId = instanceId,
        LeaseExpiration = leaseExpiration,
        Now = DateTimeOffset.UtcNow,
        MaxCount = maxCount
      }
    );

    var messagesList = messages.ToList();

    if (messagesList.Count > 0) {
      _logger.LogWarning(
        "Leased {Count} orphaned messages for instance {InstanceId} (crash recovery)",
        messagesList.Count,
        instanceId
      );
    }

    return messagesList;
  }
}
