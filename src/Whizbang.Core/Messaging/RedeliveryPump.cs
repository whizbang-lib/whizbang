// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// version)) into <see cref="RedeliveryComposite"/>s, one stream per bundle or, for a backfill or an answer to a
/// requester that accepts it, across streams by scope (#2092, #2268), and publishes them <b>wire-only</b>
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
/// <remarks>
/// Creates the pump over the transport. The envelope serializer is the same seam the outbox uses
/// for composites — and because the children ride as raw JSON, serializing the bundle needs type
/// metadata only for <see cref="RedeliveryComposite"/> itself, never for any consumer payload.
/// </remarks>
public sealed class RedeliveryPump(
    ITransport transport,
    IEnvelopeSerializer envelopeSerializer,
    IServiceInstanceProvider instanceProvider,
    ICompositeFactory compositeFactory,
    RedeliveryPumpOptions? options = null,
    TimeProvider? timeProvider = null) {
  private readonly ITransport _transport = ArgumentGuard.NotNull(transport);
  private readonly IEnvelopeSerializer _envelopeSerializer = ArgumentGuard.NotNull(envelopeSerializer);
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider;
  private readonly RedeliveryPumpOptions _options = options ?? new RedeliveryPumpOptions();
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
  private readonly ICompositeFactory _compositeFactory = compositeFactory;

  /// <summary>
  /// Whether the bundles this pump publishes answer an operator's request
  /// (<see cref="RequestRedeliveryCommand.OperatorRequested"/>), which the receiving service applies whatever its
  /// repair mode.
  /// </summary>
  public bool OperatorRequested { get; init; }

  /// <summary>
  /// How long each answering bundle lives at the broker (#2078): a redelivery answer is an integrity control message,
  /// and a stale one expires rather than accumulating on a stopped requester's subscription. Null (the default, used by
  /// non-integrity pumps such as the audit ledger rebuild) leaves the broker's own expiry.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
  public TimeSpan? AnswerTimeToLive { get; init; }

  /// <summary>
  /// When set, bundles span streams (#2092): events group by scope only, so a window of one type's history ships in as
  /// few bundles as <see cref="RedeliveryPumpOptions.MaxInnerEventsPerComposite"/> and
  /// <see cref="RedeliveryPumpOptions.MaxBytesPerComposite"/> allow instead of one per stream, each child names the
  /// stream it is stored on, and every bundle's own stream (its broker session) is this id, so a backfill's bundles
  /// and its window answers arrive in order. Null (the default) keeps one stream per bundle, which every receiver
  /// understands; only a windowed backfill, whose requester is new enough to ask for it, sets it.
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryPumpWindowTests.cs</tests>
  public Guid? BundleStreamId { get; init; }

  /// <summary>
  /// When set, an answer bundles across streams (#2268): events group by scope only, as a windowed backfill's do
  /// (<see cref="BundleStreamId"/>), so a request naming many streams a few events short ships in as few bundles as
  /// <see cref="RedeliveryPumpOptions.MaxInnerEventsPerComposite"/> and <see cref="RedeliveryPumpOptions.MaxBytesPerComposite"/>
  /// allow instead of one per stream. Each child names the stream it is stored on, and every bundle's own stream, its
  /// broker session, is <see cref="AnswerSessionFor"/> of the origin, topic, requester and scope, so one stream's events
  /// that span bundles, or pages of one selection, arrive in order. False (the default) keeps one stream per bundle for
  /// a requester that predates the shape (<see cref="RequestRedeliveryCommand.AcceptsStreamSpanningBundles"/>).
  /// <see cref="BundleStreamId"/>, when also set, names the session instead.
  /// </summary>
  /// <docs>resilience/stream-integrity#repair-answers-bundle-across-streams</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryPumpAnswerBundlingTests.cs</tests>
  public bool BundleAcrossStreams { get; init; }

  /// <summary>Namespace of <see cref="AnswerSessionFor"/>'s name-based ids.</summary>
  private static readonly Guid _answerSessionNamespace = NameBasedGuid.V5(Guid.Empty, "whizbang.redelivery-answer-session.v1");

  /// <summary>
  /// The broker session (and bundle stream) of a stream-spanning answer (#2268): a name-based id of the origin, the reply
  /// topic, the requester and the scope. Derived rather than minted, so every page of one selection and every instance
  /// of the origin agree, and one stream's bundles share it and keep their order on the receiver. One key per scope
  /// keeps tenants apart; one per origin and requester keeps unrelated answers apart. Answers to one requester for one
  /// tenant are serialized behind each other, which costs little: one bundle now carries what was up to hundreds of
  /// messages.
  /// </summary>
  /// <param name="originServiceId">The answering origin's service id.</param>
  /// <param name="topic">The reply topic.</param>
  /// <param name="target">The requester the answer is directed at, or null for a broadcast.</param>
  /// <param name="scope">The events' stored scope JSON, or null when unscoped.</param>
  /// <returns>The session key.</returns>
  /// <docs>resilience/stream-integrity#repair-answers-bundle-across-streams</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryPumpAnswerBundlingTests.cs:AnswerSessionFor_IsDerived_AndSeparatesOrigins_Requesters_Topics_AndScopesAsync</tests>
  public static Guid AnswerSessionFor(Guid originServiceId, string topic, string? target, string? scope) {
    ArgumentException.ThrowIfNullOrWhiteSpace(topic);
    // Target and scope are each prefixed by whether they are present, so "no requester" never collides with an empty one.
    return NameBasedGuid.V5(_answerSessionNamespace, string.Concat(
      originServiceId.ToString("N"), "\n", topic, "\n",
      target is null ? "-" : "+" + target, "\n",
      scope is null ? "-" : "+" + scope));
  }

  /// <summary>
  /// Publishes a backfill window's answer to <paramref name="target"/> on <paramref name="topic"/>, on the backfill's
  /// session so it follows the window's bundles (#2092), with the same time to live and send retry as a bundle.
  /// </summary>
  /// <param name="answer">The answer.</param>
  /// <param name="topic">The requester's reply topic.</param>
  /// <param name="target">The requester's service name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryPumpWindowTests.cs</tests>
  public async Task PublishWindowAnswerAsync(BackfillWindowAnswer answer, string topic, string target, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(answer);
    ArgumentException.ThrowIfNullOrWhiteSpace(topic);
    ArgumentException.ThrowIfNullOrWhiteSpace(target);
    var envelope = new MessageEnvelope<BackfillWindowAnswer> {
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = answer,
      Hops = [ControlPlaneHop.Create(typeof(BackfillWindowAnswer), _instanceProvider, _time.GetUtcNow())],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Target = target,
    };
    var serialized = _envelopeSerializer.SerializeEnvelope(envelope);
    await _sendWithRetryAsync(serialized, _destination(topic, answer.BackfillId, typeof(BackfillWindowAnswer)), cancellationToken)
      .ConfigureAwait(false);
  }


  /// <summary>
  /// <see cref="PublishAsync"/> for an answer to a redelivery request (#2143): every bundle echoes
  /// <paramref name="answeredWindow"/>, the window of origin commit sequences the selection covered, so the receiver
  /// marks as unchangeable only the buckets whose deficit the answer covered.
  /// </summary>
  /// <param name="events">Selection output, ordered (stream, version).</param>
  /// <param name="answeredWindow">The window the selection covered.</param>
  /// <param name="topic">Wire topic.</param>
  /// <param name="target">Directed-message target, or null to broadcast.</param>
  /// <param name="originServiceId">This origin's service id.</param>
  /// <param name="stateOnly">True for the backfill (state-only) semantics.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The number of composites published.</returns>
  /// <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryPumpWindowTests.cs:PublishAnswer_EchoesTheAnsweredWindowOnEveryBundle_AndPublishSaysNoneAsync</tests>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "PublishAsync's parameters plus the window the answer covered.")]
  public Task<int> PublishAnswerAsync(
      IReadOnlyList<RedeliveryEvent> events,
      RedeliveryAnswerWindow answeredWindow,
      string topic,
      string? target,
      Guid originServiceId = default,
      bool stateOnly = false,
      CancellationToken cancellationToken = default) =>
    _publishAsync(events, topic, target, originServiceId, stateOnly, answeredWindow, cancellationToken);

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
  public Task<int> PublishAsync(
      IReadOnlyList<RedeliveryEvent> events,
      string topic,
      string? target,
      Guid originServiceId = default,
      bool stateOnly = false,
      CancellationToken cancellationToken = default) =>
    _publishAsync(events, topic, target, originServiceId, stateOnly, answeredWindow: null, cancellationToken);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The two public publishes' parameters, the window optional.")]
  private async Task<int> _publishAsync(
      IReadOnlyList<RedeliveryEvent> events,
      string topic,
      string? target,
      Guid originServiceId,
      bool stateOnly,
      RedeliveryAnswerWindow? answeredWindow,
      CancellationToken cancellationToken) {
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
      // #2092: a windowed backfill bundles across streams (scope only); each child still names its stored stream.
      // #2268: an answer to a requester that files each child under its own stream bundles across streams too.
      GroupKey = BundleStreamId is null && !BundleAcrossStreams
        ? CompositeGroupKey.FromKey<RedeliveryEvent>(e => $"{e.StreamId}|{e.Scope}")
        : CompositeGroupKey.FromKey<RedeliveryEvent>(e => e.Scope ?? string.Empty),
      MaxConstituentsPerComposite = _options.MaxInnerEventsPerComposite,
      MaxBytesPerComposite = _options.MaxBytesPerComposite,
      ConstituentSizeBytes = e => e.EventData.Length + (e.Metadata?.Length ?? 0),
      BuildComposite = batch => _buildComposite(batch.Constituents, originServiceId, OperatorRequested,
        _bundleStreamFor(batch.Constituents[0].Scope, originServiceId, topic, target), answeredWindow),
    });

    var published = 0;
    foreach (var plan in plans) {
      // Scope-uniform by construction (see GroupKey), so any constituent answers for the bundle.
      var scope = _scopeDeltaFor(plan.Constituents[0].Scope);
      await _publishPlanAsync((RedeliveryComposite)plan.Composite, topic, target, stateOnly, scope, cancellationToken).ConfigureAwait(false);
      published++;
    }
    return published;
  }

  /// <summary>
  /// The stream (and broker session) a bundle of <paramref name="scope"/> rides: the backfill's, the answer's shared key
  /// when bundles span streams, or null for the one stream every child of a per-stream bundle shares.
  /// </summary>
  private Guid? _bundleStreamFor(string? scope, Guid originServiceId, string topic, string? target) =>
    BundleStreamId ?? (BundleAcrossStreams ? AnswerSessionFor(originServiceId, topic, target, scope) : null);

  /// <summary>
  /// One chunk's repair bundle: the stored payloads and type names raw, the original ids, origin and commit sequences,
  /// and the stream each event is stored on, which the receiver files it under (#1798).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:StoredStream_IsTheStreamTheProducerStoredItOnAsync</tests>
  private static RedeliveryComposite _buildComposite(
      IReadOnlyList<RedeliveryEvent> chunk, Guid originServiceId, bool operatorRequested, Guid? bundleStreamId,
      RedeliveryAnswerWindow? answeredWindow) {
    // RAW carry: each child is the stored wire JSON verbatim + its stored wire type name. No typed
    // rehydration, no polymorphic serialization, no type knowledge required at the origin — the
    // payload bytes that were emitted are the payload bytes that repair.
    var innerPayloads = new List<System.Text.Json.JsonElement>(chunk.Count);
    foreach (var evt in chunk) {
      using var doc = System.Text.Json.JsonDocument.Parse(evt.EventData);
      MessageParseDiagnostics.Record(evt.EventId, MessageParseSource.Redelivery, MessageParseKind.Payload);
      innerPayloads.Add(doc.RootElement.Clone());
    }
    return new RedeliveryComposite {
      StreamId = bundleStreamId ?? chunk[0].StreamId,
      InnerPayloads = innerPayloads,
      InnerTypeNames = [.. chunk.Select(c => c.EventType)],
      InnerEventIds = [.. chunk.Select(c => c.EventId)],
      // The stream the origin stored each event on, which the receiver files it under (#1798).
      InnerStreamIds = [.. chunk.Select(c => c.StreamId)],
      // Phase B: the ORIGINAL origin identity rides the bundle — fanned-out children recount
      // inside their original commit-sequence window at the consumer.
      OriginServiceId = originServiceId,
      InnerCommitSequences = [.. chunk.Select(c => c.CommitSequence)],
      OperatorRequested = operatorRequested,
      // #2143: the window this answer covers, so the receiver marks only the buckets whose deficit it covered.
      AnswersWindow = answeredWindow is not null,
      AnsweredFromCommitSequence = answeredWindow?.FromCommitSequence,
      AnsweredToCommitSequence = answeredWindow?.ToCommitSequence,
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
      Priority = Whizbang.Core.Priority.WorkPriority.BACKGROUND,
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = composite,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = _instanceProvider.ToInfoOrUnknown(),
          // Names the bundle's stream as every dispatched message names its own, so a receiver that predates the
          // per-child streams still files the repaired events under it rather than under this envelope's id (#1798).
          Metadata = StreamIdentity.HopMetadata(composite.StreamId),
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
    // Session-enabled subscriptions dead-letter sessionless deliveries. The bundle's own stream is the session key: the
    // one stream of a per-stream bundle, or the key every bundle of a stream-spanning answer or backfill shares, so a
    // stream's bundles keep FIFO either way (#2268).
    var serialized = _envelopeSerializer.SerializeEnvelope<RedeliveryComposite>(wireEnvelope);
    await _sendWithRetryAsync(serialized, _destination(topic, composite.StreamId, typeof(RedeliveryComposite)), cancellationToken)
      .ConfigureAwait(false);
  }

  /// <summary>A control-plane destination on <paramref name="sessionStreamId"/>'s session, with the answer time to live
  /// when one is set.</summary>
  private TransportDestination _destination(string topic, Guid sessionStreamId, Type payloadType) {
    var destination = Transports.ControlPlaneDestination.For(topic, sessionStreamId, payloadType);
    if (AnswerTimeToLive is { } lifetime) {
      destination = Transports.ControlMessageTtl.Stamp(destination, lifetime);
    }
    return destination;
  }

  /// <summary>
  /// A broker under throttle fails sends transiently; without a bounded retry ONE failed send aborts the whole serve,
  /// every later chunk dies with it and the requester's attempt burns for nothing. Cancellation is a shutdown signal,
  /// never retried.
  /// </summary>
  private async Task _sendWithRetryAsync(SerializedEnvelope serialized, TransportDestination destination, CancellationToken cancellationToken) {
    var attempts = Math.Max(1, _options.PublishRetryAttempts);
    var attempt = 1;
    while (true) {
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
        attempt++;
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
