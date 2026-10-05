// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Routing;

/// <summary>
/// Which gate evaluated the discard. Used as an OTel tag value.
/// </summary>
public enum MessageDiscardGate {
  /// <summary>Transport receive boundary — message just pulled off the broker.</summary>
  Receive,
  /// <summary>Inbox dispatch worker — row about to be handed to a receptor.</summary>
  Inbox,
  /// <summary>Outbox publish worker — row about to be sent to the transport.</summary>
  Outbox,
}

/// <summary>
/// Outcome of a discard-policy evaluation. <see cref="Reason"/> is <see cref="MessageDiscardReason.None"/>
/// when <see cref="ShouldDiscard"/> is <c>false</c>.
/// </summary>
public readonly record struct MessageDiscardDecision(
  bool ShouldDiscard,
  MessageDiscardReason Reason,
  string? Detail = null);

/// <summary>
/// Centralised "should this message be skipped?" decision, reused at the three gates
/// where Whizbang considers dropping a message — transport receive, inbox dispatch,
/// and outbox publish. Also owns the structured-log and OTel-counter side effects so
/// every gate emits consistent telemetry at consistent levels.
/// </summary>
/// <remarks>
/// Callers ask the policy to evaluate, act on the resulting <see cref="MessageDiscardDecision"/>
/// (broker ack, mark row Skipped, etc), then call <see cref="RecordDiscard"/> exactly
/// once. The policy decides the log level based on the reason — routine
/// <see cref="MessageDiscardReason.NoLocalConsumer"/> drops at <c>Debug</c>, so they
/// don't surface in production by default, while
/// <see cref="MessageDiscardReason.DomainNotOwned"/> is a <c>Warning</c>.
/// </remarks>
/// <docs>messaging/topic-filters</docs>
public interface IMessageDiscardPolicy {
  /// <summary>
  /// Decide whether a message just received from the transport should be skipped
  /// (broker-ack + in-memory drop) because this service has no consumer for the type.
  /// </summary>
  MessageDiscardDecision EvaluateReceive(string payloadClrType, string topic, string subscription);

  /// <summary>
  /// Decide whether an inbox row should be skipped (mark <c>Skipped</c>, advance bookmark)
  /// before dispatching to a handler.
  /// </summary>
  MessageDiscardDecision EvaluateInbox(string payloadClrType);

  /// <summary>
  /// Decide whether an outbox row should be skipped before publishing to the transport.
  /// The default implementation returns <c>ShouldDiscard = false</c> (publish proceeds)
  /// unless an <c>IEventCatalog</c> override explicitly says no service consumes the type.
  /// </summary>
  MessageDiscardDecision EvaluateOutbox(string payloadClrType);

  /// <summary>
  /// Record a discard — emits a structured log entry at the appropriate level (per
  /// <see cref="MessageDiscardReason"/>) and increments the <c>whizbang.message.skipped</c>
  /// OTel counter with consistent tags. Callers invoke this exactly once after acting
  /// on a positive <see cref="MessageDiscardDecision.ShouldDiscard"/>.
  /// </summary>
  void RecordDiscard(
    MessageDiscardGate gate,
    MessageDiscardDecision decision,
    string payloadClrType,
    IReadOnlyDictionary<string, object?>? additionalTags = null);
}

/// <summary>
/// Default policy: routes the "any consumer locally?" check through
/// <see cref="IReceptorRegistryQuery.HasAnyConsumer"/> for the receive and inbox gates.
/// Outbox gate is a safe-default no-op until a future <c>IEventCatalog</c> seam is
/// supplied.
/// </summary>
public sealed class MessageDiscardPolicy : IMessageDiscardPolicy {
  private readonly IReceptorRegistryQuery _registry;
  private readonly ILogger<MessageDiscardPolicy> _logger;
  private readonly PassiveCounter<long> _skippedCounter;
  private readonly IReadOnlySet<string> _absorbedNamespaces;
  private readonly IEventMarkerResolver _markerResolver;
  // The tag attributes this host has a hook for. A tagged event is work for this host only where
  // one of these is the tag it carries.
  private readonly HashSet<Type> _hookedTagAttributes;
  // Keyed by payload type name, rebuilt when a late module initializer registers more tags.
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _tagWork = new(StringComparer.Ordinal);
  private int _tagWorkGeneration = -1;

  // Reason+type pairs already surfaced at Information. Value is unused — this is a set.
  private readonly System.Collections.Concurrent.ConcurrentDictionary<(MessageDiscardReason, string), byte> _seenDiscards = new();

  // Ceiling on distinct pairs tracked. Far above any real contract surface, low enough that a
  // pathological type string cannot turn the throttle into the leak it exists to prevent.
  private const int MAX_TRACKED_DISCARD_KEYS = 1024;

#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>OTel meter name for the discard counter.</summary>
  public const string METER_NAME = "Whizbang.Core.Routing.MessageDiscard";

  /// <summary>OTel counter name. Keep stable — dashboards depend on it.</summary>
  public const string COUNTER_NAME = "whizbang.message.skipped";
#pragma warning restore CA1707

  /// <summary>Construct the default policy.</summary>
  /// <param name="registry">Consumer registry for the receive/inbox no-consumer gates.</param>
  /// <param name="logger">Diagnostic logger.</param>
  /// <param name="meter">OTel meter for the discard counter.</param>
  /// <param name="routingOptions">Routing options; supplies the <see cref="RoutingOptions.AbsorbedNamespaces"/>
  /// allow-list so unconsumed events on an absorbed namespace are kept (persisted) rather than dropped.
  /// Optional — when null, no namespace is absorbed and behavior is unchanged.</param>
  /// <param name="markerResolver">Catalog-backed event-marker lookup; lets the no-consumer gates
  /// recognize composite payload types (which never have receptors — their consumers are the inner
  /// events, addressable only after fan-out) and keep them. Optional — when null, composites are
  /// not exempted (legacy behavior).</param>
  /// <param name="coreOptions">Supplies the host's tag hooks, so an event whose only reason to exist
  /// here is a tag this host acts on is kept rather than dropped for having no receptor. Optional —
  /// when null, a tag is no reason to keep anything, which is the behavior that shipped.</param>
  public MessageDiscardPolicy(
      IReceptorRegistryQuery registry,
      ILogger<MessageDiscardPolicy> logger,
      Meter meter,
      IOptions<RoutingOptions> routingOptions,
      IEventMarkerResolver markerResolver,
      Configuration.WhizbangCoreOptions? coreOptions = null) {
    _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    ArgumentNullException.ThrowIfNull(meter);
    _markerResolver = markerResolver;
    _absorbedNamespaces = routingOptions.Value.AbsorbedNamespaces;
    _hookedTagAttributes = coreOptions?.Tags.HookRegistrations.Select(h => h.AttributeType).ToHashSet() ?? [];
    _skippedCounter = meter.CreatePassiveCounter<long>(
      COUNTER_NAME,
      unit: "{message}",
      description: "Count of messages intentionally skipped at a discard gate (receive | inbox | outbox).");
    // Issue #711: one series per gate exists at zero from construction (see PassiveCounter).
    foreach (var gate in Enum.GetValues<MessageDiscardGate>()) {
      _skippedCounter.Touch(new KeyValuePair<string, object?>("gate", _gateTag(gate)));
    }
  }

  /// <inheritdoc />
  public MessageDiscardDecision EvaluateReceive(string payloadClrType, string topic, string subscription) {
    if (string.IsNullOrEmpty(payloadClrType)) {
      return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
    }
    if (_isBodyClaimEnvelope(payloadClrType)) {
      return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
    }
    if (_isCompositeType(payloadClrType)) {
      return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
    }
    // Keep the message if a consumer exists OR its namespace is absorbed (persist-for-later, even with no
    // consumer). Absorbed events still reach the inbox → the unconditional event-store write captures them.
    if (_hasAnyConsumer(payloadClrType) || _isAbsorbedNamespace(payloadClrType)
        || _isTagWorkForThisHost(payloadClrType)) {
      return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
    }
    return new MessageDiscardDecision(
      ShouldDiscard: true,
      Reason: MessageDiscardReason.NoLocalConsumer,
      Detail: $"No local receptor or perspective consumes payload type '{payloadClrType}'");
  }

  /// <summary>
  /// True when the payload's event namespace is in <see cref="RoutingOptions.AbsorbedNamespaces"/>. The
  /// namespace is derived from the payload type name (transport-agnostic — the transport's <c>topic</c>
  /// argument can be a queue name like <c>{svc}-{ns}</c>, so it is not used here).
  /// </summary>
  private bool _isAbsorbedNamespace(string payloadClrType) {
    if (_absorbedNamespaces.Count == 0) {
      return false;
    }
    var ns = TypeNameFormatter.GetPayloadNamespace(payloadClrType);
    return ns is not null && _absorbedNamespaces.Contains(ns);
  }

  /// <inheritdoc />
  public MessageDiscardDecision EvaluateInbox(string payloadClrType) {
    if (string.IsNullOrEmpty(payloadClrType)) {
      return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
    }
    if (_isBodyClaimEnvelope(payloadClrType)) {
      return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
    }
    if (_isCompositeType(payloadClrType)) {
      return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
    }
    return _hasAnyConsumer(payloadClrType) || _isTagWorkForThisHost(payloadClrType)
      ? new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None)
      : new MessageDiscardDecision(
          ShouldDiscard: true,
          Reason: MessageDiscardReason.RegistryChanged,
          Detail: $"Inbox row references payload type '{payloadClrType}' but no consumer is registered now");
  }

  /// <summary>
  /// Whether anything consumes the message, asked about its payload. An envelope-wrapped name is
  /// unwrapped first: the envelope is a transport wrapper nothing consumes, and judging it instead of
  /// its payload skipped every broker dead letter recovered under its envelope type (#934). Unwrapped
  /// here as well as in the default registry query, so a custom <see cref="IReceptorRegistryQuery"/>
  /// is asked the same question.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchSkipGateTests.cs:ShouldSkipInbox_EnvelopeWrappedRow_WhosePayloadHasAConsumer_IsKeptAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchSkipGateTests.cs:ShouldSkipInbox_EnvelopeWrappedRow_WhosePayloadHasNoConsumer_IsStillSkippedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchSkipGateTests.cs:EvaluateReceive_EnvelopeWrappedName_WhosePayloadHasAConsumer_IsKeptAsync</tests>
  private bool _hasAnyConsumer(string payloadClrType) =>
    _registry.HasAnyConsumer(EventTypeMatchingHelper.ExtractInnerPayloadTypeName(payloadClrType));

  /// <summary>
  /// True when this host has a tag hook for a tag the payload type carries, wherever that tag was
  /// declared.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A receptor and a perspective are not the only reasons a host wants an event. A host whose job
  /// is to send the notification a tag declares consumes the event by acting on the tag, and had
  /// nothing else to show for it: the gate asked the generated consumer registry, which lists what
  /// the generator scanned for this host, so a tag declared in a shared contracts assembly counted
  /// for nothing. The host dropped the event on arrival and the notification was never sent, with
  /// nothing logged above Debug.
  /// </para>
  /// <para>
  /// The tag registry is per-assembly and aggregated across every loaded one, so it answers for the
  /// contracts assembly too. Both halves are required: the type carries the tag, and this host has
  /// a hook for it. A tag no hook here acts on is somebody else's work and still drops.
  /// </para>
  /// <para>
  /// Asked per received message, so the answer is cached per payload type and thrown away when the
  /// registry grows -- a module initializer in a lazily loaded assembly can add tags after the
  /// first message has already been judged.
  /// </para>
  /// </remarks>
  private bool _isTagWorkForThisHost(string payloadClrType) {
    if (_hookedTagAttributes.Count == 0) {
      return false;
    }

    var generation = Tags.MessageTagRegistry.Count;
    if (Volatile.Read(ref _tagWorkGeneration) != generation) {
      _tagWork.Clear();
      Volatile.Write(ref _tagWorkGeneration, generation);
    }

    var payload = EventTypeMatchingHelper.ExtractInnerPayloadTypeName(payloadClrType);
    if (_tagWork.TryGetValue(payload, out var known)) {
      return known;
    }

    var hookedTypes = Tags.MessageTagRegistry.GetAllTags()
      .Where(tag => _hookedTagAttributes.Contains(tag.AttributeType))
      .Select(tag => tag.MessageType);
    var isWork = EventTypeMatchingHelper.IsEventType(payload, hookedTypes);

    // Bounded for the reason the discard throttle is: a pathological type string must not turn a
    // cache into a leak.
    if (_tagWork.Count < MAX_TRACKED_DISCARD_KEYS) {
      _tagWork[payload] = isWork;
    }
    return isWork;
  }

  /// <inheritdoc />
  public MessageDiscardDecision EvaluateOutbox(string payloadClrType) {
    // Safe default — without explicit catalog evidence that no service consumes the type,
    // never silently skip an outbound publish. A future IEventCatalog implementation can
    // be plugged in to enable real outbox-side filtering.
    return new MessageDiscardDecision(ShouldDiscard: false, MessageDiscardReason.None);
  }

  /// <summary>
  /// True when the wire payload type is a body-offload (claim-check) envelope carrying the internal
  /// <see cref="Whizbang.Core.Offloads.BodyClaimEnvelopePayload"/>. No service registers a consumer
  /// for the claim type, so the receive/inbox no-local-consumer gates would discard it — but the real
  /// message is rehydrated to its original type downstream, so a claim must never be dropped here.
  /// </summary>
  private static bool _isBodyClaimEnvelope(string payloadClrType) =>
    payloadClrType.Contains(nameof(Whizbang.Core.Offloads.BodyClaimEnvelopePayload), System.StringComparison.Ordinal);

  /// <summary>
  /// True when the type name (payload CLR name, or an envelope-wrapped wire name — callers pass
  /// both shapes) resolves through the catalog union to a type stamped
  /// <see cref="EventFlags.Composite"/>. A composite never has a receptor or perspective consumer;
  /// its consumers are the INNER events, addressable only after the dispatch-seam fan-out — so
  /// both no-consumer gates this policy backs must keep it. Null resolver = no exemption (legacy).
  /// </summary>
  private bool _isCompositeType(string payloadClrType) {
    var name = EnvelopeTypeNameHelper.ExtractInnerTypeName(payloadClrType) ?? payloadClrType;
    return CompositeInboxFanout.IsCompositeWireType(name, _markerResolver);
  }

  /// <inheritdoc />
  public void RecordDiscard(
      MessageDiscardGate gate,
      MessageDiscardDecision decision,
      string payloadClrType,
      IReadOnlyDictionary<string, object?>? additionalTags = null) {
    if (!decision.ShouldDiscard) { return; }

    var level = _levelFor(decision.Reason);

    // Demote repeats to Debug. The FIRST discard of a given reason+type is a real signal — rows
    // exist for a type nothing consumes now, which says something about deployment shape. Every
    // repeat after that says the same thing again, and on a bulk backlog "again" means hundreds
    // of lines per second: observed in production at ~735/s sustained, which drove the container
    // to its memory limit and killed it. The work was fine; the narration was not.
    //
    // The counter below is unconditional, so demoting the text costs no observability — the rate
    // and the payload type stay on the dashboard either way.
    if (level >= LogLevel.Information && !_isFirstSighting(decision.Reason, payloadClrType)) {
      level = LogLevel.Debug;
    }

    if (_logger.IsEnabled(level)) {
#pragma warning disable CA1848 // Diagnostic logging — level chosen at runtime by reason; LoggerMessage source-gen doesn't help here.
      _logger.Log(
        level,
        "Whizbang message skipped. Gate={Gate} Reason={Reason} PayloadType={PayloadType} Detail={Detail}",
        gate, decision.Reason, payloadClrType, decision.Detail);
#pragma warning restore CA1848
    }

    var tags = new TagList {
      { "gate", _gateTag(gate) },
      { "reason", decision.Reason.ToString() },
      { "payload_type", payloadClrType },
    };
    if (additionalTags is not null) {
      foreach (var (k, v) in additionalTags) { tags.Add(k, v); }
    }
    _skippedCounter.Add(1, tags);
  }

  /// <summary>
  /// True the first time this reason+type pair is seen, false forever after.
  /// </summary>
  /// <remarks>
  /// Bounded deliberately. The natural key space is the service's contract surface — small and
  /// fixed — but "naturally bounded" is an assumption about the caller, and this set lives for the
  /// process lifetime. A malformed or attacker-influenced type string would otherwise make an
  /// unbounded cache out of a component whose entire purpose here is to stop unbounded growth.
  /// Past the cap the set stops admitting new keys, so the worst case degrades to logging those
  /// types at Information rather than to consuming memory without limit.
  /// </remarks>
  private bool _isFirstSighting(MessageDiscardReason reason, string payloadClrType) {
    if (_seenDiscards.Count >= MAX_TRACKED_DISCARD_KEYS) {
      return false;
    }
    return _seenDiscards.TryAdd((reason, payloadClrType), 0);
  }

  private static LogLevel _levelFor(MessageDiscardReason reason) => reason switch {
    MessageDiscardReason.NoLocalConsumer => LogLevel.Debug,
    MessageDiscardReason.NoKnownConsumer => LogLevel.Debug,
    MessageDiscardReason.RegistryChanged => LogLevel.Information,
    MessageDiscardReason.DomainNotOwned => LogLevel.Warning,
    _ => LogLevel.Information,
  };

  private static string _gateTag(MessageDiscardGate gate) => gate switch {
    MessageDiscardGate.Receive => "receive",
    MessageDiscardGate.Inbox => "inbox",
    MessageDiscardGate.Outbox => "outbox",
    _ => gate.ToString(),
  };
}
