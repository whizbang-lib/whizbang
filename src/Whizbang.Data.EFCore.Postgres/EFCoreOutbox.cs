using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.Entities;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core implementation of IOutbox using PostgreSQL with JSONB columns.
/// Provides transactional messaging through the outbox pattern.
/// </summary>
public sealed class EFCoreOutbox<TDbContext> : IOutbox
  where TDbContext : DbContext {

  private readonly TDbContext _context;

  public EFCoreOutbox(TDbContext context) {
    _context = context ?? throw new ArgumentNullException(nameof(context));
  }

  public async Task PublishAsync(
      object message,
      CancellationToken cancellationToken = default) {

    if (message == null) {
      throw new ArgumentNullException(nameof(message));
    }

    var messageType = message.GetType().FullName
      ?? throw new InvalidOperationException($"Message type has no FullName: {message.GetType()}");

    // Serialize message to JSON
    var messageDataJson = JsonSerializer.Serialize(message);
    var messageData = JsonDocument.Parse(messageDataJson);

    // Create metadata (placeholder - should come from MessageEnvelope)
    var metadataJson = JsonSerializer.Serialize(new {
      Timestamp = DateTime.UtcNow,
      CorrelationId = Guid.NewGuid().ToString(),
      CausationId = Guid.NewGuid().ToString()
    });
    var metadata = JsonDocument.Parse(metadataJson);

    var record = new OutboxRecord {
      MessageId = Guid.NewGuid().ToString(), // Should come from MessageEnvelope
      MessageType = messageType,
      MessageData = messageData,
      Metadata = metadata,
      Scope = null,
      Status = "Pending",
      Attempts = 0,
      Error = null,
      CreatedAt = DateTime.UtcNow,
      PublishedAt = null,
      Topic = null, // Can be set based on message type routing
      PartitionKey = null
    };

    // Add to outbox (will be published by background worker)
    await _context.Set<OutboxRecord>().AddAsync(record, cancellationToken);
    await _context.SaveChangesAsync(cancellationToken);
  }

  /// <summary>
  /// Gets pending messages for batch processing by outbox publisher.
  /// This method is used by background workers to poll for unpublished messages.
  /// </summary>
  public async Task<IEnumerable<OutboxRecord>> GetPendingMessagesAsync(
      int batchSize = 100,
      CancellationToken cancellationToken = default) {

    return await _context.Set<OutboxRecord>()
      .Where(o => o.Status == "Pending" || (o.Status == "Failed" && o.Attempts < 3))
      .OrderBy(o => o.Id)
      .Take(batchSize)
      .ToListAsync(cancellationToken);
  }

  /// <summary>
  /// Marks a message as published after successful delivery.
  /// </summary>
  public async Task MarkAsPublishedAsync(
      long id,
      CancellationToken cancellationToken = default) {

    var record = await _context.Set<OutboxRecord>()
      .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    if (record != null) {
      record.Status = "Published";
      record.PublishedAt = DateTime.UtcNow;
      await _context.SaveChangesAsync(cancellationToken);
    }
  }

  /// <summary>
  /// Marks a message as failed after unsuccessful delivery attempt.
  /// </summary>
  public async Task MarkAsFailedAsync(
      long id,
      string error,
      CancellationToken cancellationToken = default) {

    var record = await _context.Set<OutboxRecord>()
      .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    if (record != null) {
      record.Status = "Failed";
      record.Error = error;
      record.Attempts++;
      await _context.SaveChangesAsync(cancellationToken);
    }
  }
}
