using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
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

  /// <summary>
  /// Attempts per composite send before the serve surfaces the failure (1 = no retry). A broker
  /// under throttle fails sends transiently; without a retry ONE failed send aborts the whole
  /// serve — every later chunk dies with it and the requester's attempt burns for nothing.
  /// Default 5.
  /// </summary>
  public int PublishRetryAttempts { get; set; } = 5;

  /// <summary>
  /// Base delay before a send retry; attempt n waits base × 2^(n-1), capped at 30 s. Default
  /// 2000 ms (the common broker throttle guidance is "wait a couple of seconds and try again").
  /// </summary>
  public int PublishRetryBaseDelayMs { get; set; } = 2000;
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
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly RedeliveryPumpOptions _options;
  private readonly TimeProvider _time;
  private readonly ICompositeFactory _compositeFactory;

  /// <summary>
  /// Creates the pump over the transport. The envelope serializer is the same seam the outbox uses
  /// for composites — and because the children ride as raw JSON, serializing the bundle needs type
  /// metadata only for <see cref="RedeliveryComposite"/> itself, never for any consumer payload.
  /// </summary>
  public RedeliveryPump(
      ITransport transport,
      IEnvelopeSerializer envelopeSerializer,
      IServiceInstanceProvider instanceProvider,
      RedeliveryPumpOptions? options = null,
      TimeProvider? timeProvider = null,
      ICompositeFactory? compositeFactory = null) {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _envelopeSerializer = envelopeSerializer ?? throw new ArgumentNullException(nameof(envelopeSerializer));
    _instanceProvider = instanceProvider;
    _options = options ?? new RedeliveryPumpOptions();
    _time = timeProvider ?? TimeProvider.System;
    _compositeFactory = compositeFactory ?? new CompositeFactory();
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

    // Input contract: (stream, version)-ordered — the mint groups per stream (identical to
    // consecutive-run splitting for ordered input), chunked by count AND by raw body bytes: a
    // count-only bound lets large-bodied histories build composites that exhaust memory during
    // serialization or exceed the broker message-size limit and never deliver. The per-stream
    // split is an ordering concern layered on routing (all chunks share the topic): the stream id
    // rides as the broker session key, so redelivered bundles keep stream FIFO.
    var plans = _compositeFactory.Create(new CompositeMintRequest<RedeliveryEvent> {
      Constituents = events,
      // Grouped by stream AND scope. A composite carries ONE hop scope, so a bundle spanning two
      // scopes could only be stamped with one of them — repairing one tenant's event under another
      // tenant's authority. A stream is single-tenant in practice, which makes this a no-op split
      // that costs nothing and forecloses the mis-attribution when it is not.
      GroupKey = CompositeGroupKey.FromKey<RedeliveryEvent>(e => $"{e.StreamId}|{e.Scope}"),
      MaxConstituentsPerComposite = _options.MaxInnerEventsPerComposite,
      MaxBytesPerComposite = _options.MaxBytesPerComposite,
      ConstituentSizeBytes = e => e.EventData.Length + (e.Metadata?.Length ?? 0),
      BuildComposite = batch => _buildComposite(batch.Constituents, originServiceId),
    });

    var published = 0;
    foreach (var plan in plans) {
      // Scope-uniform by construction (see GroupKey), so any constituent answers for the bundle.
      var scope = _scopeDeltaFor(plan.Constituents.Count > 0 ? plan.Constituents[0].Scope : null);
      await _publishPlanAsync((RedeliveryComposite)plan.Composite, topic, target, stateOnly, scope, cancellationToken).ConfigureAwait(false);
      published++;
    }
    return published;
  }

  private static RedeliveryComposite _buildComposite(IReadOnlyList<RedeliveryEvent> chunk, Guid originServiceId) {
    // RAW carry: each child is the stored wire JSON verbatim + its stored wire type name. No typed
    // rehydration, no polymorphic serialization, no type knowledge required at the origin — the
    // payload bytes that were emitted are the payload bytes that repair.
    var innerPayloads = new List<System.Text.Json.JsonElement>(chunk.Count);
    foreach (var evt in chunk) {
      using var doc = System.Text.Json.JsonDocument.Parse(evt.EventData);
      innerPayloads.Add(doc.RootElement.Clone());
    }
    return new RedeliveryComposite {
      StreamId = chunk[0].StreamId,
      InnerPayloads = innerPayloads,
      InnerTypeNames = [.. chunk.Select(c => c.EventType)],
      InnerEventIds = [.. chunk.Select(c => c.EventId)],
      // Phase B: the ORIGINAL origin identity rides the bundle — fanned-out children recount
      // inside their original commit-sequence window at the consumer.
      OriginServiceId = originServiceId,
      InnerCommitSequences = [.. chunk.Select(c => c.CommitSequence)],
    };
  }

  private async Task _publishPlanAsync(
      RedeliveryComposite composite,
      string topic,
      string? target,
      bool stateOnly,
      ScopeDelta? scope,
      CancellationToken cancellationToken) {
    var wireEnvelope = new MessageEnvelope<RedeliveryComposite> {
      MessageId = new MessageId(TrackedGuid.NewMedo()),
      Payload = composite,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = _instanceProvider.ToInfo() ?? ServiceInstanceInfo.Unknown,
          // The original events' scope, carried forward. Fan-out at the consumer derives every
          // child's scope from this hop, and the children are then WRITTEN to the event store with
          // whatever it holds. Publishing unscoped therefore does not merely inconvenience the
          // consumer -- it persists copies that no later read can repair, and a perspective
          // requiring a security context rejects them until they park.
          Scope = scope
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
    var destination = Transports.ControlPlaneDestination.For(topic, composite.StreamId, typeof(RedeliveryComposite));

    // A broker under throttle fails sends transiently; without a bounded retry ONE failed send
    // aborts the whole serve — every later chunk dies with it and the requester's attempt burns
    // for nothing. Cancellation is a shutdown signal, never retried.
    var attempts = Math.Max(1, _options.PublishRetryAttempts);
    for (var attempt = 1; ; attempt++) {
      try {
        await _transport.PublishAsync(serialized.JsonEnvelope, destination, serialized.EnvelopeType,
          cancellationToken: cancellationToken).ConfigureAwait(false);
        return;
      } catch (OperationCanceledException) {
        throw;
      } catch when (attempt < attempts) {
        var delayMs = Math.Min((long)_options.PublishRetryBaseDelayMs << (attempt - 1), 30_000);
        if (delayMs > 0) {
          await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _time, cancellationToken).ConfigureAwait(false);
        }
      }
    }
  }
  /// <summary>
  /// Builds the hop's <see cref="ScopeDelta"/> from an event's stored scope JSON.
  /// </summary>
  /// <remarks>
  /// Reads the wire short keys the scope column stores. Not caught: the column is <c>jsonb</c>, so
  /// the database has already guaranteed well-formed JSON, and a parse failure here means the
  /// stored bytes are corrupt -- which should surface rather than be quietly downgraded to
  /// "unscoped", the one outcome indistinguishable from an event that legitimately had no scope.
  /// </remarks>
  /// <param name="scopeJson">Stored scope JSON, or null when the event carried no scope.</param>
  /// <returns>The delta, or null when nothing was stored.</returns>
  private static ScopeDelta? _scopeDeltaFor(string? scopeJson) {
    if (string.IsNullOrEmpty(scopeJson)) {
      return null;
    }

    using var doc = System.Text.Json.JsonDocument.Parse(scopeJson);
    // A jsonb column can hold the JSON null LITERAL, which arrives as the four-character string
    // "null" -- not empty, so the guard above does not catch it.
    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) {
      return null;
    }

    var scope = new PerspectiveScope {
      TenantId = _scopeValue(doc.RootElement, "t"),
      UserId = _scopeValue(doc.RootElement, "u"),
      CustomerId = _scopeValue(doc.RootElement, "c"),
      OrganizationId = _scopeValue(doc.RootElement, "o"),
    };

    // Returns null when every field is empty, so an empty object never becomes a hollow authority.
    return ScopeDelta.FromPerspectiveScope(scope);
  }

  private static string? _scopeValue(System.Text.Json.JsonElement element, string key) =>
    element.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
      ? value.GetString()
      : null;
}
