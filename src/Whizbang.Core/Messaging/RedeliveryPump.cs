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
/// <docs>resilience/stream-integrity</docs>
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

  /// <summary>
  /// Byte budget per composite, measured over the raw stored bodies (event_data + metadata).
  /// A chunk flushes once it crosses this budget even below
  /// <see cref="MaxInnerEventsPerComposite"/> — large-bodied histories would otherwise build
  /// composites that exhaust memory during serialization or exceed the broker's message-size
  /// limit and never deliver. A single event larger than the budget still ships, alone.
  /// Default 192 KB (fits the smallest common broker tier with envelope overhead headroom).
  /// </summary>
  public int MaxBytesPerComposite { get; set; } = 192_000;

  /// <summary>
  /// Origin-side selection page size: the redelivery request receptor selects and publishes in
  /// pages of at most this many events instead of materializing the whole
  /// <see cref="MaxEventsPerRequest"/> cap at once. Bounds a repair's memory to one page of
  /// bodies regardless of the request's width. Default 500.
  /// </summary>
  public int SelectPageSize { get; set; } = 500;
}

/// <summary>
/// Stream-integrity R1a2: bundles selected re-delivery events (<see cref="RedeliveryEvent"/>, as
/// produced by <see cref="IWorkCoordinator.SelectRedeliveryEventsAsync"/> — ordered (stream,
/// version)) into per-stream <see cref="RedeliveryComposite"/>s and publishes them <b>wire-only</b>
/// directly through <see cref="ITransport"/>: no outbox row, no local dispatch — the origin
/// already holds these events, so its own pipeline has nothing to do with them.
/// </summary>
/// <remarks>
/// Identity is preserved end to end: inner payloads ride as the RAW stored wire JSON
/// (<see cref="IRawInnerComposite"/>) with their stored wire type names, and
/// <see cref="RedeliveryComposite.InnerEventIds"/> carries the original event ids that the
/// identity-preserving fan-out stamps onto the children at consumers. The origin never rehydrates
/// typed payloads — re-serializing them polymorphically was redundant work, an upcast fidelity
/// risk, and an AOT metadata cliff (a consumer payload shape unreachable through the polymorphic
/// resolver chain made the repair throw instead of ship). The optional target (see
/// <see cref="IMessageEnvelope.Target"/>) directs the bundle at one consumer; null broadcasts
/// (operator-initiated origin-wide repair).
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryPumpTests.cs</tests>
public sealed class RedeliveryPump {
  private readonly ITransport _transport;
  private readonly IEnvelopeSerializer _envelopeSerializer;
  private readonly IServiceInstanceProvider? _instanceProvider;
  private readonly RedeliveryPumpOptions _options;

  /// <summary>
  /// Creates the pump over the transport. The envelope serializer is the same seam the outbox uses
  /// for composites — and because the children ride as raw JSON, serializing the bundle needs type
  /// metadata only for <see cref="RedeliveryComposite"/> itself, never for any consumer payload.
  /// </summary>
  public RedeliveryPump(
      ITransport transport,
      IEnvelopeSerializer envelopeSerializer,
      IServiceInstanceProvider? instanceProvider = null,
      RedeliveryPumpOptions? options = null) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _envelopeSerializer = envelopeSerializer ?? throw new ArgumentNullException(nameof(envelopeSerializer));
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
  /// <param name="originServiceId">This origin's service id, stamped on the bundle so fanned-out
  /// children carry their ORIGINAL source identity (Phase B windowed accounting). Default empty =
  /// unknown (children inherit the bundle envelope's source identity).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task<int> PublishAsync(
      IReadOnlyList<RedeliveryEvent> events,
      string topic,
      string? target,
      Guid originServiceId = default,
      bool stateOnly = false,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(events);
    ArgumentException.ThrowIfNullOrWhiteSpace(topic);
    if (events.Count == 0) {
      return 0;
    }

    var published = 0;

    // Input contract: (stream, version)-ordered — group CONSECUTIVE runs per stream, chunked by
    // count AND by raw body bytes: a count-only bound lets large-bodied histories build
    // composites that exhaust memory during serialization or exceed the broker message-size
    // limit and never deliver.
    var chunk = new List<RedeliveryEvent>(Math.Min(events.Count, _options.MaxInnerEventsPerComposite));
    var chunkBytes = 0L;
    for (var i = 0; i < events.Count; i++) {
      chunk.Add(events[i]);
      chunkBytes += events[i].EventData.Length + (events[i].Metadata?.Length ?? 0);
      var boundary = i == events.Count - 1
        || events[i + 1].StreamId != events[i].StreamId
        || chunk.Count == _options.MaxInnerEventsPerComposite
        || chunkBytes >= _options.MaxBytesPerComposite;
      if (!boundary) {
        continue;
      }
      await _publishChunkAsync(chunk, topic, target, originServiceId, stateOnly, cancellationToken).ConfigureAwait(false);
      published++;
      chunk.Clear();
      chunkBytes = 0;
    }
    return published;
  }

  private async Task _publishChunkAsync(
      List<RedeliveryEvent> chunk,
      string topic,
      string? target,
      Guid originServiceId,
      bool stateOnly,
      CancellationToken cancellationToken) {
    // RAW carry: each child is the stored wire JSON verbatim + its stored wire type name. No typed
    // rehydration, no polymorphic serialization, no type knowledge required at the origin — the
    // payload bytes that were emitted are the payload bytes that repair.
    var innerPayloads = new List<System.Text.Json.JsonElement>(chunk.Count);
    foreach (var evt in chunk) {
      using var doc = System.Text.Json.JsonDocument.Parse(evt.EventData);
      innerPayloads.Add(doc.RootElement.Clone());
    }
    var composite = new RedeliveryComposite {
      StreamId = chunk[0].StreamId,
      InnerPayloads = innerPayloads,
      InnerTypeNames = [.. chunk.Select(c => c.EventType)],
      InnerEventIds = [.. chunk.Select(c => c.EventId)],
      // Phase B: the ORIGINAL origin identity rides the bundle — fanned-out children recount
      // inside their original commit-sequence window at the consumer.
      OriginServiceId = originServiceId,
      InnerCommitSequences = [.. chunk.Select(c => c.CommitSequence)],
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
      Target = target,
      // Phase S: backfill bundles build state only — children must not re-fire trigger receptors.
      StateOnly = stateOnly
    };

    // The outbox's composite seam: typed → MessageEnvelope<JsonElement> + payload-derived wire type.
    // Session-enabled subscriptions dead-letter sessionless deliveries; per-stream chunking makes
    // the chunk's stream id the natural session key — redelivered bundles keep stream FIFO.
    var serialized = _envelopeSerializer.SerializeEnvelope<RedeliveryComposite>(wireEnvelope);
    await _transport.PublishAsync(serialized.JsonEnvelope,
      Transports.ControlPlaneDestination.For(topic, chunk[0].StreamId, typeof(RedeliveryComposite)), serialized.EnvelopeType,
      cancellationToken: cancellationToken).ConfigureAwait(false);
  }
}
