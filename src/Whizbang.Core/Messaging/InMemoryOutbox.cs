using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory implementation of IOutbox for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary.
/// NOT suitable for production use across multiple processes.
/// </summary>
public class InMemoryOutbox : IOutbox {
  private readonly ConcurrentDictionary<MessageId, OutboxRecord> _messages = new();

  /// <inheritdoc />
  public Task StoreAsync(MessageId messageId, string destination, byte[] payload, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(destination);
    ArgumentNullException.ThrowIfNull(payload);

    var record = new OutboxRecord(
      MessageId: messageId,
      Destination: destination,
      Payload: payload,
      CreatedAt: DateTimeOffset.UtcNow,
      PublishedAt: null
    );

    _messages.TryAdd(messageId, record);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) {
    var pending = _messages
      .Where(kvp => kvp.Value.PublishedAt == null)
      .Take(batchSize)
      .Select(kvp => new OutboxMessage(
        kvp.Value.MessageId,
        kvp.Value.Destination,
        kvp.Value.Payload,
        kvp.Value.CreatedAt
      ))
      .ToList();

    return Task.FromResult<IReadOnlyList<OutboxMessage>>(pending);
  }

  /// <inheritdoc />
  public Task MarkPublishedAsync(MessageId messageId, CancellationToken cancellationToken = default) {

    if (_messages.TryGetValue(messageId, out var record)) {
      var updated = record with { PublishedAt = DateTimeOffset.UtcNow };
      _messages.TryUpdate(messageId, updated, record);
    }

    return Task.CompletedTask;
  }

  private record OutboxRecord(
    MessageId MessageId,
    string Destination,
    byte[] Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt
  );
}
