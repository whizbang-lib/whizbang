using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory implementation of IEventStore for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary and sorted event storage.
/// NOT suitable for production use across multiple processes.
/// </summary>
public class InMemoryEventStore : IEventStore {
  private readonly ConcurrentDictionary<string, StreamData> _streams = new();

  /// <inheritdoc />
  public Task AppendAsync(string streamKey, IMessageEnvelope envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamKey);
    ArgumentNullException.ThrowIfNull(envelope);

    var stream = _streams.GetOrAdd(streamKey, _ => new StreamData());
    stream.Append(envelope);

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public async IAsyncEnumerable<IMessageEnvelope> ReadAsync(
    string streamKey,
    long fromSequence,
    [EnumeratorCancellation] CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(streamKey);

    if (!_streams.TryGetValue(streamKey, out var stream)) {
      yield break;
    }

    foreach (var envelope in stream.Read(fromSequence)) {
      cancellationToken.ThrowIfCancellationRequested();
      yield return envelope;
    }

    await Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task<long> GetLastSequenceAsync(string streamKey, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    if (!_streams.TryGetValue(streamKey, out var stream)) {
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
