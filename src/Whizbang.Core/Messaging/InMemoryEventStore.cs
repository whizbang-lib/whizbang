using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory implementation of IEventStore for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary and sorted event storage.
/// NOT suitable for production use across multiple processes.
/// Stream ID is inferred from event's [AggregateId] property.
/// </summary>
public class InMemoryEventStore : IEventStore {
  private readonly ConcurrentDictionary<Guid, StreamData> _streams = new();
  private readonly IPolicyEngine _policyEngine;

  public InMemoryEventStore(IPolicyEngine policyEngine) {
    _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
  }

  /// <inheritdoc />
  public Task AppendAsync(Guid streamId, IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    var stream = _streams.GetOrAdd(streamId, _ => new StreamData());
    stream.Append(envelope);

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    long fromSequence,
    [EnumeratorCancellation] CancellationToken cancellationToken = default
  ) {
    if (!_streams.TryGetValue(streamId, out var stream)) {
      yield break;
    }

    foreach (var envelope in stream.Read(fromSequence)) {
      cancellationToken.ThrowIfCancellationRequested();
      // Cast to strongly-typed envelope
      if (envelope is MessageEnvelope<TMessage> typedEnvelope) {
        yield return typedEnvelope;
      }
    }

    await Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
    if (!_streams.TryGetValue(streamId, out var stream)) {
      return Task.FromResult(-1L);
    }

    return Task.FromResult(stream.GetLastSequence());
  }

  /// <summary>
  /// Thread-safe stream data container.
  /// </summary>
  private class StreamData {
    private readonly object _lock = new();
    private readonly List<EventRecord> _events = new();
    private long _currentSequence = -1;

    public void Append(IMessageEnvelope envelope) {
      lock (_lock) {
        _currentSequence++;
        _events.Add(new EventRecord(_currentSequence, envelope));
      }
    }

    public IEnumerable<IMessageEnvelope> Read(long fromSequence) {
      lock (_lock) {
        return _events
          .Where(e => e.Sequence >= fromSequence)
          .OrderBy(e => e.Sequence)
          .Select(e => e.Envelope)
          .ToList();
      }
    }

    public long GetLastSequence() {
      lock (_lock) {
        return _currentSequence;
      }
    }
  }

  private record EventRecord(long Sequence, IMessageEnvelope Envelope);
}
