using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Data;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory implementation of IOutbox for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary.
/// NOT suitable for production use across multiple processes.
/// Uses JSONB adapter for envelope serialization (matching PostgreSQL implementation).
/// </summary>
public class InMemoryOutbox(IJsonbPersistenceAdapter<IMessageEnvelope> envelopeAdapter) : IOutbox {
  private readonly ConcurrentDictionary<MessageId, OutboxRecord> _messages = new();
  private readonly IJsonbPersistenceAdapter<IMessageEnvelope> _envelopeAdapter = envelopeAdapter ?? throw new ArgumentNullException(nameof(envelopeAdapter));

  /// <inheritdoc />
  public Task StoreAsync<TMessage>(MessageEnvelope<TMessage> envelope, string destination, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    // Convert envelope to JSONB format
    var jsonbModel = _envelopeAdapter.ToJsonb(envelope);
    var eventType = typeof(TMessage).FullName ?? throw new InvalidOperationException("Event type has no FullName");

    var record = new OutboxRecord(
      MessageId: envelope.MessageId,
      Destination: destination,
      EventType: eventType,
      EventData: jsonbModel.DataJson,
      Metadata: jsonbModel.MetadataJson,
      Scope: jsonbModel.ScopeJson,
      CreatedAt: DateTimeOffset.UtcNow,
      PublishedAt: null
    );

    _messages.TryAdd(envelope.MessageId, record);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StoreAsync(IMessageEnvelope envelope, string destination, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);
    ArgumentNullException.ThrowIfNull(destination);

    // Convert envelope to JSONB format (works with IMessageEnvelope)
    var jsonbModel = _envelopeAdapter.ToJsonb(envelope);
    var eventType = envelope.GetType().GenericTypeArguments[0].FullName
      ?? throw new InvalidOperationException("Event type has no FullName");

    var record = new OutboxRecord(
      MessageId: envelope.MessageId,
      Destination: destination,
      EventType: eventType,
      EventData: jsonbModel.DataJson,
      Metadata: jsonbModel.MetadataJson,
      Scope: jsonbModel.ScopeJson,
      CreatedAt: DateTimeOffset.UtcNow,
      PublishedAt: null
    );

    _messages.TryAdd(envelope.MessageId, record);
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
        kvp.Value.EventType,
        kvp.Value.EventData,
        kvp.Value.Metadata,
        kvp.Value.Scope,
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
    string EventType,
    string EventData,
    string Metadata,
    string? Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt
  );
}
