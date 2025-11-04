using System.Collections.Concurrent;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// In-memory implementation of ITraceStore for testing and development.
/// Thread-safe using ConcurrentDictionary.
/// NOT suitable for production use (no persistence, limited by memory).
/// </summary>
public class InMemoryTraceStore : ITraceStore {
  private readonly ConcurrentDictionary<MessageId, IMessageEnvelope> _traces = new();

  public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
    if (envelope == null) {
      throw new ArgumentNullException(nameof(envelope));
    }

    _traces.TryAdd(envelope.MessageId, envelope);
    return Task.CompletedTask;
  }

  public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) {
    _traces.TryGetValue(messageId, out var envelope);
    return Task.FromResult(envelope);
  }

  public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) {
    var results = _traces.Values
      .Where(e => {
        var corrId = e.GetCorrelationId();
        return corrId != null && corrId.Equals(correlationId);
      })
      .OrderBy(e => e.GetMessageTimestamp())
      .ToList();

    return Task.FromResult(results);
  }

  public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) {
    if (!_traces.TryGetValue(messageId, out var envelope)) {
      return Task.FromResult(new List<IMessageEnvelope>());
    }

    var chain = new HashSet<MessageId>();
    var results = new List<IMessageEnvelope>();

    // Add the message itself
    chain.Add(envelope.MessageId);
    results.Add(envelope);

    // Walk up the causation chain (parents)
    var currentCausationId = envelope.GetCausationId();
    while (currentCausationId is { Value: var guidValue } && guidValue != Guid.Empty) {
      // currentCausationId is the MessageId of the parent message
      if (chain.Contains(currentCausationId.Value)) {
        break; // Circular reference protection
      }

      if (_traces.TryGetValue(currentCausationId.Value, out var parent)) {
        chain.Add(currentCausationId.Value); // Add the parent's MessageId to the chain
        results.Add(parent);
        currentCausationId = parent.GetCausationId();
      } else {
        break;
      }
    }

    // Walk down the causation chain (children)
    var children = _traces.Values
      .Where(e => {
        var causationId = e.GetCausationId();
        return causationId != null &&
               causationId.Value != Guid.Empty &&
               causationId.Equals(messageId) &&
               !chain.Contains(e.MessageId);
      })
      .ToList();

    foreach (var child in children) {
      AddChildrenRecursive(child, chain, results);
    }

    // Sort by timestamp
    results.Sort((a, b) => {
      var aTime = a.Hops.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue;
      var bTime = b.Hops.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue;
      return aTime.CompareTo(bTime);
    });

    return Task.FromResult(results);
  }

  private void AddChildrenRecursive(IMessageEnvelope message, HashSet<MessageId> chain, List<IMessageEnvelope> results) {
    if (chain.Contains(message.MessageId)) {
      return; // Circular reference protection
    }

    chain.Add(message.MessageId);
    results.Add(message);

    // Find children of this message
    var children = _traces.Values
      .Where(e => {
        var causationId = e.GetCausationId();
        return causationId != null &&
               causationId.Value != Guid.Empty &&
               causationId.Equals(message.MessageId) &&
               !chain.Contains(e.MessageId);
      })
      .ToList();

    foreach (var child in children) {
      AddChildrenRecursive(child, chain, results);
    }
  }

  public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) {
    var results = _traces.Values
      .Where(e => {
        var timestamp = e.Hops.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue;
        return timestamp >= from && timestamp <= to;
      })
      .OrderBy(e => e.Hops.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue)
      .ToList();

    return Task.FromResult(results);
  }
}
