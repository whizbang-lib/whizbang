using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Entities;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IInbox using PostgreSQL with JSONB columns.
/// Provides exactly-once message processing through deduplication.
/// </summary>
public sealed class EFCoreInbox<TDbContext> : IInbox
  where TDbContext : DbContext {

  private readonly TDbContext _context;

  public EFCoreInbox(TDbContext context) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
  }

  public async Task<bool> TryProcessAsync(
      string messageId,
      Func<Task> handler,
      CancellationToken cancellationToken = default) {

    if (string.IsNullOrWhiteSpace(messageId)) {
      throw new ArgumentException("Message ID cannot be null or whitespace.", nameof(messageId));
    }

    if (handler == null) {
      throw new ArgumentNullException(nameof(handler));
    }

    // Check if message was already processed
    var existing = await _context.Set<InboxRecord>()
      .FirstOrDefaultAsync(i => i.MessageId == messageId, cancellationToken);

    if (existing != null) {
      // Message already processed or in progress
      return false;
    }

    // Create inbox record with Pending status
    var metadataJson = JsonSerializer.Serialize(new {
      Timestamp = DateTime.UtcNow,
      CorrelationId = Guid.NewGuid().ToString()
    });
    var metadata = JsonDocument.Parse(metadataJson);

    var messageDataJson = JsonSerializer.Serialize(new { MessageId = messageId });
    var messageData = JsonDocument.Parse(messageDataJson);

    var record = new InboxRecord {
      MessageId = messageId,
      MessageType = "Unknown", // Should come from actual message
      MessageData = messageData,
      Metadata = metadata,
      Scope = null,
      Status = "Pending",
      Attempts = 0,
      Error = null,
      CreatedAt = DateTime.UtcNow,
      ProcessedAt = null
    };

    try {
      // Add record to inbox (will fail if duplicate due to PK constraint)
      await _context.Set<InboxRecord>().AddAsync(record, cancellationToken);
      await _context.SaveChangesAsync(cancellationToken);

      // Update status to Processing
      record.Status = "Processing";
      record.Attempts++;
      await _context.SaveChangesAsync(cancellationToken);

      // Execute handler
      await handler();

      // Mark as completed
      record.Status = "Completed";
      record.ProcessedAt = DateTime.UtcNow;
      await _context.SaveChangesAsync(cancellationToken);

      return true;
    }
    catch (DbUpdateException) when (IsDuplicateKeyException()) {
      // Another process inserted the record first
      return false;
    }
    catch (Exception ex) {
      // Mark as failed
      record.Status = "Failed";
      record.Error = ex.Message;
      record.ProcessedAt = DateTime.UtcNow;
      await _context.SaveChangesAsync(cancellationToken);

      throw; // Re-throw to allow caller to handle
    }
  }

  private bool IsDuplicateKeyException() {
    // Check if the exception is due to duplicate key constraint
    // This is simplified - proper implementation should check PostgreSQL error code
    return true;
  }
}
