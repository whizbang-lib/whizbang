using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Non-generic base interface for message envelopes.
/// Provides access to identity, payload (as object), hops, and metadata without requiring knowledge of the payload type.
/// Use this for heterogeneous collections of envelopes with different payload types.
/// Use <see cref="IMessageEnvelope{TMessage}"/> when you need strongly-typed access to the payload.
/// </summary>
/// <docs>fundamentals/persistence/observability</docs>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_RequiresAtLeastOneHopAsync</tests>
public interface IMessageEnvelope {
  /// <summary>
  /// Envelope schema version. Enables backward-compatible evolution of the envelope format.
  /// Version 1: original (MessageId, Payload, Hops).
  /// Version 2: added DispatchContext.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#envelope-versioning</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/MessageEnvelopeVersionTests.cs</tests>
  [JsonPropertyName("v")]
  int Version { get; }

  /// <summary>
  /// Context describing how this message was dispatched (mode + source).
  /// Used by ReceptorInvoker to prevent double-firing across lifecycle stages.
  /// Defaults to <see cref="MessageDispatchContext.Default"/> for v1 envelopes.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#dispatch-context</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/MessageEnvelopeVersionTests.cs</tests>
  [JsonPropertyName("dc")]
  MessageDispatchContext DispatchContext { get; }

  /// <summary>
  /// Unique identifier for this specific message.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  [JsonPropertyName("id")]
  MessageId MessageId { get; }

  /// <summary>
  /// The message payload as an object.
  /// For strongly-typed access, use <see cref="IMessageEnvelope{TMessage}.Payload"/>.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  [JsonPropertyName("p")]
  object Payload { get; }

  /// <summary>
  /// Hops this message has taken through the system.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_RequiresAtLeastOneHopAsync</tests>
  [JsonPropertyName("h")]
  List<MessageHop> Hops { get; }

  /// <summary>
  /// Per-instance routing flags carried in memory (not serialized). The load-bearing case is
  /// <see cref="Messaging.EventFlags.NoRebroadcast"/>: a fan-out child carries it so the outbox-enqueue
  /// boundary can drop any attempt to re-broadcast it. Default-implemented as
  /// <see cref="Messaging.EventFlags.None"/> so existing envelope implementers are unaffected.
  /// </summary>
  /// <docs>fundamentals/messaging/composite-events#no-rebroadcast</docs>
  [JsonIgnore]
  Messaging.EventFlags Flags => Messaging.EventFlags.None;

  /// <summary>
  /// Per-receptor invocation records. Parallel to <see cref="Hops"/>; used by
  /// <see cref="Messaging.IReceptorDedupStore"/> to enforce exactly-once-per-receptor-per-message.
  /// Null by default; allocated lazily via <see cref="GetOrCreateReceptorInvocations"/> on first append.
  /// NOT consulted by security, scope, source-service, or trace-context extraction.
  /// </summary>
  /// <remarks>
  /// Default implementation returns null, so existing non-tracking envelope types (e.g.,
  /// test doubles) continue to compile. Production envelopes (<see cref="MessageEnvelope{TMessage}"/>,
  /// <see cref="CascadeEnvelopeWrapper"/>) override with real backing.
  /// </remarks>
  /// <docs>fundamentals/receptors/exactly-once-firing</docs>
  [JsonPropertyName("rin")]
  List<ReceptorInvocationRecord>? ReceptorInvocations => null;

  /// <summary>
  /// Slice 26 — originating service's identity. Default implementation returns
  /// <see cref="Guid.Empty"/> so test doubles and legacy envelope implementations keep
  /// compiling. Production envelopes (<see cref="MessageEnvelope{TMessage}"/>) override
  /// via an init property.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("sid")]
  Guid SourceServiceId => Guid.Empty;

  /// <summary>
  /// Slice 26 — source service's <c>commit_sequence</c> stamp for this event. Default 0.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("sseq")]
  long SourceCommitSequence => 0;

  /// <summary>
  /// Slice 26 — optional causality reference (forensic only, not enforced).
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("cbid")]
  Guid? CausedByServiceId => null;

  /// <summary>
  /// Slice 26 — companion to <see cref="CausedByServiceId"/>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("cbseq")]
  long? CausedByCommitSequence => null;

  /// <summary>
  /// Returns the <see cref="ReceptorInvocations"/> list, creating and assigning it on the
  /// envelope if it was null. Default implementation throws: an envelope type must opt in
  /// to tracking by overriding this member. Callers (notably
  /// <see cref="Messaging.EnvelopeReceptorDedupStore"/>) should only reach here for envelope
  /// types that genuinely support invocation tracking.
  /// </summary>
  List<ReceptorInvocationRecord> GetOrCreateReceptorInvocations() =>
    throw new System.NotSupportedException(
      $"Envelope type '{GetType().FullName}' does not support receptor invocation tracking. " +
      "Use MessageEnvelope<T> or implement IMessageEnvelope.GetOrCreateReceptorInvocations explicitly.");

  /// <summary>
  /// Adds a hop to the message's journey.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_AddHop_AddsHopToListAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_AddHop_MaintainsOrderedListAsync</tests>
  void AddHop(MessageHop hop);

  /// <summary>
  /// Gets the message timestamp (first hop's timestamp).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMessageTimestamp_ReturnsFirstHopTimestampAsync</tests>
  DateTimeOffset GetMessageTimestamp();

  /// <summary>
  /// Gets the correlation ID from the first hop.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  CorrelationId? GetCorrelationId();

  /// <summary>
  /// Gets the causation ID from the first hop.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  MessageId? GetCausationId();

  /// <summary>
  /// Gets a metadata value by key from the most recent Current hop.
  /// Searches backwards through hops to find the first HopType.Current hop
  /// that contains the specified key.
  /// </summary>
  /// <param name="key">The metadata key to retrieve</param>
  /// <returns>The JsonElement metadata value if found, otherwise null</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_ReturnsNull_WhenKeyNotFoundAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_ReturnsLatestValue_WhenKeyExistsInMultipleHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_IgnoresCausationHopsAsync</tests>
  JsonElement? GetMetadata(string key);

  /// <summary>
  /// Gets the current scope by walking forward through current message hops and merging deltas.
  /// Each hop's ScopeDelta is applied to build the full ScopeContext.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The merged ScopeContext from all current hops, or null if no hops have scope deltas</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/ScopeDeltaIntegrationTests.cs</tests>
  ScopeContext? GetCurrentScope();

  /// <summary>
  /// Gets the current security context by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The security context from the most recent current hop, or null if no hops have a security context</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_ReturnsNull_WhenNoHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_ReturnsMostRecentNonNullValueAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_IgnoresCausationHopsAsync</tests>
  [Obsolete("Use GetCurrentScope() instead. This method returns the old SecurityContext type.")]
  SecurityContext? GetCurrentSecurityContext();
  /// <summary>
  /// Logical service identity this message is directed at, or <c>null</c> for broadcast (the
  /// default). A targeted message is point-to-point by definition: non-target services discard it
  /// at the transport receive seam before deserialization or fan-out, and it is excluded from the
  /// broadcast-integrity universe. Direction is intended for control-plane, repair, and response
  /// traffic — domain facts should broadcast. Default interface implementation returns null so
  /// existing implementors are unaffected.
  /// </summary>
  /// <docs>fundamentals/messaging/directed-messages</docs>
  string? Target => null;

  /// <summary>
  /// Stream-integrity Phase S: state-only delivery — the payload builds STATE (event store +
  /// perspectives) but never fires trigger receptors at the consumer. Stamped on backfill bundles
  /// (history a subscription expansion needs must not re-run business reactions) and inherited by
  /// their fanned-out children. Default false = normal delivery semantics.
  /// </summary>
  /// <docs>proposals/stream-integrity</docs>
  [JsonPropertyName("sto")]
  bool StateOnly => false;

}

/// <summary>
/// Generic interface for message envelopes with strong typing.
/// Extends <see cref="IMessageEnvelope"/> to add strongly-typed access to the payload.
/// The 'out' modifier enables covariance for the payload type.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload (covariant)</typeparam>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
public interface IMessageEnvelope<out TMessage> : IMessageEnvelope {
  /// <summary>
  /// The message payload with strong type information.
  /// Hides the base interface's object Payload property to provide strong typing.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  [JsonPropertyName("p")]
  new TMessage Payload { get; }
}
