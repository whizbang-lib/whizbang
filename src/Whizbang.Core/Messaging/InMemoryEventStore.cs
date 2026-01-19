using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// In-memory implementation of IEventStore for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary and sorted event storage.
/// NOT suitable for production use across multiple processes.
/// Stream ID is inferred from event's [AggregateId] property.
/// </summary>
public class InMemoryEventStore : IEventStore {
  private readonly ConcurrentDictionary<Guid, StreamData> _streams = new();

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_WithNullEnvelope_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    var stream = _streams.GetOrAdd(streamId, _ => new StreamData());
    stream.Append(envelope);

    // NOTE: Inline perspective invocation removed - perspectives are now processed via PerspectiveWorker
    // using checkpoint-based processing for better reliability and scalability.
    // See: Stage 4 of perspective worker refactoring (2025-12-18)

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_DifferentStreams_ShouldBeIndependentAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
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
  public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
    Guid streamId,
    Guid? fromEventId,
    [EnumeratorCancellation] CancellationToken cancellationToken = default
  ) {
    if (!_streams.TryGetValue(streamId, out var stream)) {
      yield break;
    }

    foreach (var envelope in stream.ReadByEventId(fromEventId)) {
      cancellationToken.ThrowIfCancellationRequested();
      // Cast to strongly-typed envelope
      if (envelope is MessageEnvelope<TMessage> typedEnvelope) {
        yield return typedEnvelope;
      }
    }

    await Task.CompletedTask;
  }

  /// <inheritdoc />
  public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
    Guid streamId,
    Guid? fromEventId,
    IReadOnlyList<Type> eventTypes,
    [EnumeratorCancellation] CancellationToken cancellationToken = default
  ) {
    if (!_streams.TryGetValue(streamId, out var stream)) {
      yield break;
    }

    // Build type lookup for validation
    var typeSet = new HashSet<Type>(eventTypes);

    foreach (var envelope in stream.ReadByEventId(fromEventId)) {
      cancellationToken.ThrowIfCancellationRequested();

      // Check if the payload is an IEvent
      if (envelope.Payload is IEvent eventPayload) {
        // Verify the payload type is in the allowed list
        var payloadType = eventPayload.GetType();
        if (!typeSet.Contains(payloadType)) {
          throw new InvalidOperationException(
            $"Event type '{payloadType.FullName}' in stream {streamId} is not in the provided event types list. " +
            $"Available types: {string.Join(", ", eventTypes.Select(t => t.FullName))}"
          );
        }

        // Create new envelope with IEvent payload
        var typedEnvelope = new MessageEnvelope<IEvent> {
          MessageId = envelope.MessageId,
          Payload = eventPayload,
          Hops = envelope.Hops
        };
        yield return typedEnvelope;
      }
    }

    await Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      CancellationToken cancellationToken = default) {

    if (!_streams.TryGetValue(streamId, out var stream)) {
      return Task.FromResult(new List<MessageEnvelope<TMessage>>());
    }

    var envelopes = stream.ReadBetween(afterEventId, upToEventId)
      .OfType<MessageEnvelope<TMessage>>()
      .ToList();

    return Task.FromResult(envelopes);
  }

  /// <inheritdoc />
  [SuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern", Justification = "InMemoryEventStore is for testing only, not production. Reflection is acceptable here.")]
  public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId,
      Guid? afterEventId,
      Guid upToEventId,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) {

    ArgumentNullException.ThrowIfNull(eventTypes);

    if (!_streams.TryGetValue(streamId, out var stream)) {
      return Task.FromResult(new List<MessageEnvelope<IEvent>>());
    }

    // Get all envelopes in range, regardless of type
    var allEnvelopes = stream.ReadBetween(afterEventId, upToEventId);

    // Extract IEvent payloads from envelopes
    var eventEnvelopes = new List<MessageEnvelope<IEvent>>();

    foreach (var envelope in allEnvelopes) {
      // InMemoryEventStore stores envelopes as objects
      // Extract the payload and check if it's an IEvent
      var payloadProperty = envelope.GetType().GetProperty("Payload");
      if (payloadProperty != null) {
        var payload = payloadProperty.GetValue(envelope);
        if (payload is IEvent eventPayload) {
          // Create a new envelope with IEvent payload
          var messageIdProperty = envelope.GetType().GetProperty("MessageId");
          var hopsProperty = envelope.GetType().GetProperty("Hops");

          var messageId = (MessageId?)messageIdProperty?.GetValue(envelope) ?? MessageId.New();
          var hops = (List<MessageHop>?)hopsProperty?.GetValue(envelope) ?? new List<MessageHop>();

          eventEnvelopes.Add(new MessageEnvelope<IEvent> {
            MessageId = messageId,
            Payload = eventPayload,
            Hops = hops
          });
        }
      }
    }

    return Task.FromResult(eventEnvelopes);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/EventStoreContractTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
    if (!_streams.TryGetValue(streamId, out var stream)) {
      return Task.FromResult(-1L);
    }

    return Task.FromResult(stream.GetLastSequence());
  }

  /// <summary>
  /// Thread-safe stream data container.
  /// </summary>
  private sealed class StreamData {
    private readonly Lock _lock = new();
    private readonly List<EventRecord> _events = [];
    private long _currentSequence = -1;

    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Interface required for polymorphic storage of envelopes with different TMessage types")]
    public void Append(IMessageEnvelope envelope) {
      lock (_lock) {
        _currentSequence++;
        _events.Add(new EventRecord(_currentSequence, envelope.MessageId.Value, envelope));
      }
    }

    public IEnumerable<IMessageEnvelope> Read(long fromSequence) {
      lock (_lock) {
        return [.. _events
          .Where(e => e.Version >= fromSequence)
          .OrderBy(e => e.Version)
          .Select(e => e.Envelope)];
      }
    }

    public IEnumerable<IMessageEnvelope> ReadByEventId(Guid? fromEventId) {
      lock (_lock) {
        if (fromEventId == null) {
          return [.. _events
            .OrderBy(e => e.EventId)
            .Select(e => e.Envelope)];
        }

        return [.. _events
          .Where(e => e.EventId.CompareTo(fromEventId.Value) > 0)
          .OrderBy(e => e.EventId)
          .Select(e => e.Envelope)];
      }
    }

    public IEnumerable<IMessageEnvelope> ReadBetween(Guid? afterEventId, Guid upToEventId) {
      lock (_lock) {
        var query = _events.Where(e => e.EventId.CompareTo(upToEventId) <= 0);

        if (afterEventId != null) {
          query = query.Where(e => e.EventId.CompareTo(afterEventId.Value) > 0);
        }

        return [.. query
          .OrderBy(e => e.EventId)
          .Select(e => e.Envelope)];
      }
    }

    public long GetLastSequence() {
      lock (_lock) {
        return _currentSequence;
      }
    }
  }

  private sealed record EventRecord(long Version, Guid EventId, IMessageEnvelope Envelope);
}
