using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory implementation of IInbox for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary.
/// NOT suitable for production use across multiple processes.
/// Mirrors InMemoryOutbox pattern.
/// </summary>
public class InMemoryInbox : IInbox {
  private readonly ConcurrentDictionary<MessageId, InboxRecord> _messages = new();

  /// <inheritdoc />
  public Task StoreAsync(IMessageEnvelope envelope, string handlerName, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(handlerName);

    // Get event type from payload
    var payload = envelope.GetPayload();
    var eventType = payload.GetType().FullName ?? throw new InvalidOperationException("Event type has no FullName");

    // For in-memory, we'll store as strings (simulating JSONB)
    var record = new InboxRecord(
      MessageId: envelope.MessageId,
      HandlerName: handlerName,
      EventType: eventType,
      EventData: System.Text.Json.JsonSerializer.Serialize(payload),
      Metadata: "{}",  // Simplified for in-memory
      Scope: null,
      ReceivedAt: DateTimeOffset.UtcNow,
      ProcessedAt: null
    );

    _messages.TryAdd(envelope.MessageId, record);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<InboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) {
    var pending = _messages.Values
      .Where(r => r.ProcessedAt == null)
      .OrderBy(r => r.ReceivedAt)
      .Take(batchSize)
      .Select(r => new InboxMessage(
        r.MessageId,
        r.HandlerName,
        r.EventType,
        r.EventData,
        r.Metadata,
        r.Scope,
        r.ReceivedAt
      ))
      .ToList();

    return Task.FromResult<IReadOnlyList<InboxMessage>>(pending);
  }

  /// <inheritdoc />
  public Task MarkProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    if (_messages.TryGetValue(messageId, out var record)) {
      var updated = record with { ProcessedAt = DateTimeOffset.UtcNow };
      _messages.TryUpdate(messageId, updated, record);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task<bool> HasProcessedAsync(MessageId messageId, CancellationToken cancellationToken = default) {
    if (_messages.TryGetValue(messageId, out var record)) {
      return Task.FromResult(record.ProcessedAt != null);
    }
    return Task.FromResult(false);
  }

  /// <inheritdoc />
  public Task CleanupExpiredAsync(TimeSpan retention, CancellationToken cancellationToken = default) {
    var cutoff = DateTimeOffset.UtcNow - retention;
    var expiredKeys = _messages
      .Where(kvp => kvp.Value.ProcessedAt != null && kvp.Value.ProcessedAt < cutoff)
      .Select(kvp => kvp.Key)
      .ToList();

    foreach (var key in expiredKeys) {
      _messages.TryRemove(key, out _);
    }

    return Task.CompletedTask;
  }

  private record InboxRecord(
    MessageId MessageId,
    string HandlerName,
    string EventType,
    string EventData,
    string Metadata,
    string? Scope,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt
  );
}
