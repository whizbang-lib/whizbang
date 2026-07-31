using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>Tuning for the re-delivery pump.</summary>
/// <docs>proposals/stream-integrity</docs>
public sealed class RedeliveryPumpOptions {
  /// <summary>
  /// Chunk bound: a stream's repair slice larger than this is split into multiple composites.
  /// Interacts with <see cref="CompositeEventBase.MaxInnerEventsAllowed"/> (which defends the
  /// receiver); this bounds the sender. Default 500.
  /// </summary>
  public int MaxInnerEventsPerComposite { get; set; } = 500;

  /// <summary>
  /// Hard per-request event cap the origin enforces on <see cref="RequestRedeliveryCommand"/> —
  /// a requester's <see cref="RequestRedeliveryCommand.MaxEvents"/> is clamped to this, never
  /// raised above it. Default 10,000 (matches <see cref="RedeliveryRequest.MaxEvents"/>).
  /// </summary>
  public int MaxEventsPerRequest { get; set; } = 10_000;
}

/// <summary>
/// Stream-integrity R1a2: bundles selected re-delivery events (<see cref="RedeliveryEvent"/>, as
/// produced by <see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/> — ordered (stream,
/// version)) into per-stream <see cref="RedeliveryComposite"/>s and publishes them <b>wire-only</b>
/// directly through <see cref="ITransport"/>: no outbox row, no local dispatch — the origin
/// already holds these events, so its own pipeline has nothing to do with them.
/// </summary>
/// <remarks>
/// Identity is preserved end to end: inner payloads are rehydrated from the stored envelopes via
/// the event store's AOT deserialization path, and <see cref="RedeliveryComposite.InnerEventIds"/>
/// carries the original event ids that the identity-preserving fan-out stamps onto the children at
/// consumers. The optional target (see <see cref="IMessageEnvelope.Target"/>)
/// directs the bundle at one consumer; null broadcasts (operator-initiated origin-wide repair).
/// A deserialization miss is a THROW, not a skip — the origin owns its own types, so a miss is a
/// bug, and silently shrinking a repair bundle would report a repair that never fully happened.
/// </remarks>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryPumpTests.cs</tests>
public sealed class RedeliveryPump {
  private static readonly string _envelopeType =
    $"Whizbang.Core.Messaging.MessageEnvelope`1[[{typeof(RedeliveryComposite).AssemblyQualifiedName}]], Whizbang.Core";

  private readonly ITransport _transport;
  private readonly IEventStore _eventStore;
  private readonly IEventTypeProvider _eventTypeProvider;
  private readonly IServiceInstanceProvider? _instanceProvider;
  private readonly RedeliveryPumpOptions _options;

  /// <summary>Creates the pump over the transport + the event store's AOT deserialization path.</summary>
  public RedeliveryPump(
      ITransport transport,
      IEventStore eventStore,
      IEventTypeProvider eventTypeProvider,
      IServiceInstanceProvider? instanceProvider = null,
      RedeliveryPumpOptions? options = null) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    _eventTypeProvider = eventTypeProvider ?? throw new ArgumentNullException(nameof(eventTypeProvider));
    _instanceProvider = instanceProvider;
    _options = options ?? new RedeliveryPumpOptions();
  }

  /// <summary>
  /// Publishes the given (stream, version)-ordered selection as per-stream re-delivery composites.
  /// Returns the number of composites published.
  /// </summary>
  /// <param name="events">Selection output — MUST be ordered (stream, version), as the coordinator returns it.</param>
  /// <param name="topic">Wire topic (the same topic the original events published to).</param>
  /// <param name="target">Directed-message target (logical service identity), or null to broadcast.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task<int> PublishAsync(
      IReadOnlyList<RedeliveryEvent> events,
      string topic,
      string? target,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(events);
    ArgumentException.ThrowIfNullOrWhiteSpace(topic);
    if (events.Count == 0) {
      return 0;
    }

    var destination = new TransportDestination(topic);
    var eventTypes = _eventTypeProvider.GetEventTypes();
    var published = 0;

    // Input contract: (stream, version)-ordered — group CONSECUTIVE runs per stream, chunked.
    var chunk = new List<RedeliveryEvent>(Math.Min(events.Count, _options.MaxInnerEventsPerComposite));
    for (var i = 0; i < events.Count; i++) {
      chunk.Add(events[i]);
      var boundary = i == events.Count - 1
        || events[i + 1].StreamId != events[i].StreamId
        || chunk.Count == _options.MaxInnerEventsPerComposite;
      if (!boundary) {
        continue;
      }
      await _publishChunkAsync(chunk, destination, target, eventTypes, cancellationToken).ConfigureAwait(false);
      published++;
      chunk.Clear();
    }
    return published;
  }

  private async Task _publishChunkAsync(
      List<RedeliveryEvent> chunk,
      TransportDestination destination,
      string? target,
      IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken) {
    var raws = new List<StreamEventData>(chunk.Count);
    foreach (var evt in chunk) {
      raws.Add(new StreamEventData {
        StreamId = evt.StreamId,
        EventId = evt.EventId,
        EventType = evt.EventType,
        EventData = evt.EventData,
        Metadata = evt.Metadata,
        Scope = evt.Scope,
        EventWorkId = Guid.Empty,
        PerspectiveName = null
      });
    }

    var envelopes = _eventStore.DeserializeStreamEvents(raws, eventTypes);
    if (envelopes.Count != chunk.Count) {
      // The origin owns its own types — a deserialization miss is a bug, and silently shrinking a
      // repair bundle would report a repair that never fully happened.
      throw new InvalidOperationException(
        $"Redelivery deserialization mismatch for stream {chunk[0].StreamId}: {chunk.Count} selected events " +
        $"yielded {envelopes.Count} envelopes. The repair bundle was NOT published.");
    }

    var inner = new List<IMessage>(envelopes.Count);
    foreach (var envelope in envelopes) {
      inner.Add(envelope.Payload);
    }
    var composite = new RedeliveryComposite {
      StreamId = chunk[0].StreamId,
      Inner = inner,
      InnerEventIds = [.. chunk.Select(c => c.EventId)],
    };

    var wireEnvelope = new MessageEnvelope<RedeliveryComposite> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = composite,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = _instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = target
    };

    await _transport.PublishAsync(wireEnvelope, destination, _envelopeType, cancellationToken: cancellationToken)
      .ConfigureAwait(false);
  }
}
