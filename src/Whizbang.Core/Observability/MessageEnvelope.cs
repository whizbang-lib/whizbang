using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Wraps a message with metadata for routing, tracing, and debugging.
/// Carries context across network boundaries and through the entire execution pipeline.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload</typeparam>
/// <docs>fundamentals/persistence/observability</docs>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_RequiresAtLeastOneHopAsync</tests>
public class MessageEnvelope<TMessage> : IMessageEnvelope<TMessage> {
  /// <summary>
  /// Envelope schema version. Defaults to 1.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#envelope-versioning</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/MessageEnvelopeVersionTests.cs</tests>
  [JsonPropertyName("v")]
  public int Version { get; init; } = 1;

  /// <summary>
  /// Context describing how this message was dispatched (mode + source).
  /// Required for new envelopes. Deserialization of v1 envelopes uses <see cref="MessageDispatchContext.Default"/>.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#dispatch-context</docs>
  /// <tests>tests/Whizbang.Core.Tests/Observability/MessageEnvelopeVersionTests.cs</tests>
  [JsonPropertyName("dc")]
  public required MessageDispatchContext DispatchContext { get; init; }

  /// <summary>
  /// Unique identifier for this specific message.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  [JsonPropertyName("id")]
  public required MessageId MessageId { get; init; }

  /// <summary>
  /// The actual message payload (strongly-typed).
  /// Also implements IMessageEnvelope.Payload as object for heterogeneous collections.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  [JsonPropertyName("p")]
  public required TMessage Payload { get; set; }

  /// <summary>
  /// Explicit implementation of non-generic Payload property.
  /// Returns the same Payload instance, boxed if necessary.
  /// </summary>
  [JsonIgnore]
  object IMessageEnvelope.Payload => Payload!;

  /// <summary>
  /// Hops this message has taken through the system.
  /// Each hop records routing, scope delta, metadata, caller information, and policy decisions.
  /// Hops are additive-only (immutable once added).
  /// At least one hop is required (the originating hop).
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_RequiresAtLeastOneHopAsync</tests>
  [JsonPropertyName("h")]
  public required List<MessageHop> Hops { get; init; }

  /// <summary>
  /// Per-receptor invocation records captured after each receptor fires against this
  /// envelope. Parallel to <see cref="Hops"/>. Used by
  /// <see cref="Messaging.IReceptorDedupStore"/> to enforce "exactly once per receptor
  /// per message". Nullable: envelopes deserialized before this field existed roundtrip
  /// without it, and fresh envelopes only allocate the list when the first record is
  /// recorded.
  /// </summary>
  /// <remarks>
  /// NOT consulted by security, scope, source-service, or trace-context extraction —
  /// those walk <see cref="Hops"/> only.
  /// </remarks>
  /// <docs>fundamentals/receptors/exactly-once-firing</docs>
  [JsonPropertyName("rin")]
  public List<ReceptorInvocationRecord>? ReceptorInvocations { get; set; }

  /// <summary>
  /// Slice 26 — service_id of the service that originated this event in its event store.
  /// Set at outbox-publish time by joining to <c>wh_service_config</c> (or to
  /// <c>wh_event_store.origin_service_id</c> for 1:1 forwarded events). Downstream
  /// consumers persist this into <c>wh_inbox.source_service_id</c> and use it as
  /// part of the per-source cursor key.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("sid")]
  public Guid SourceServiceId { get; init; }

  /// <summary>
  /// Slice 26 — the commit_sequence value the source service stamped for this event.
  /// Monotonic only within the same <see cref="SourceServiceId"/>. Downstream consumers
  /// compare against their cached cursor per source to detect inversions.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("sseq")]
  public long SourceCommitSequence { get; init; }

  /// <summary>
  /// Slice 26 — optional causality reference. Populated when this event was emitted
  /// in response to another event (e.g., a handler reacted to an upstream event and
  /// emitted a new one). Forensic only; NOT consulted by cursor logic — projection
  /// authors must write idempotent, source-independent apply logic.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("cbid")]
  public Guid? CausedByServiceId { get; init; }

  /// <summary>
  /// Slice 26 — companion to <see cref="CausedByServiceId"/>. The upstream event's
  /// <see cref="SourceCommitSequence"/>.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [JsonPropertyName("cbseq")]
  public long? CausedByCommitSequence { get; init; }

  /// <summary>
  /// The <c>wh_event_store.commit_sequence</c> value stamped by THIS database's stamper when
  /// the event was written locally. Distinct from <see cref="SourceCommitSequence"/>, which
  /// belongs to the originating service; <c>LocalCommitSequence</c> is the consuming database's
  /// own monotonic stamp and is what the perspective runner's idempotency filter compares
  /// against <see cref="Lenses.PerspectiveMetadata.CommitSequence"/>. Populated by event-store
  /// reads (<c>ReadPolymorphicAsync</c>, <c>DeserializeStreamEvents</c>) when the row's
  /// <c>commit_sequence</c> column is non-null; null for in-memory stores or rows the
  /// stamper hasn't caught up to. NOT serialized — derived from the local row at read time.
  /// </summary>
  /// <docs>fundamentals/work-coordinator/commit-sequence</docs>
  [System.Text.Json.Serialization.JsonIgnore]
  public long? LocalCommitSequence { get; set; }

  /// <inheritdoc />
  [System.Text.Json.Serialization.JsonIgnore]
  public Messaging.EventFlags Flags { get; init; } = Messaging.EventFlags.None;

  /// <summary>
  /// Logical service identity this message is directed at (<see cref="IMessageEnvelope.Target"/>);
  /// <c>null</c> = broadcast. Serialized as <c>tgt</c> and omitted when null so undirected
  /// envelopes pay zero wire cost. Settable: the directed publisher (re-delivery pump, manifest
  /// responder) stamps it on an already-built envelope.
  /// </summary>
  /// <docs>fundamentals/messaging/directed-messages</docs>
  [JsonPropertyName("tgt")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? Target { get; set; }

  /// <summary>
  /// Stream-integrity Phase S: state-only delivery marker — build state, never fire trigger
  /// receptors. Omitted from the wire when false (the overwhelmingly common case).
  /// </summary>
  /// <docs>proposals/stream-integrity</docs>
  [JsonPropertyName("sto")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
  public bool StateOnly { get; set; }

  /// <summary>
  /// Parameterless constructor for object initializer syntax.
  /// </summary>
  public MessageEnvelope() {
  }

  /// <summary>
  /// Constructor for JSON deserialization with backward compatibility.
  /// V1 envelopes missing version/dispatchContext get safe defaults.
  /// </summary>
  [System.Text.Json.Serialization.JsonConstructor]
  [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
  public MessageEnvelope(
    MessageId messageId,
    TMessage payload,
    List<MessageHop> hops,
    int version = 1,
    MessageDispatchContext? dispatchContext = null) {
    MessageId = messageId;
    Payload = payload;
    Hops = hops;
    Version = version;
    // v1 envelopes (pre-DispatchContext) default to Outbox/Local — the original cascade behavior.
    // Explicit fallback tests: tests/Whizbang.Core.Tests/Observability/MessageEnvelopeVersionTests.cs
    DispatchContext = dispatchContext ?? new MessageDispatchContext {
      Mode = Dispatch.DispatchModes.Outbox,
      Source = Messaging.MessageSource.Local
    };
  }

  /// <summary>
  /// Adds a hop to the message's journey.
  /// Called automatically during message processing to track where the message has been.
  /// </summary>
  /// <param name="hop">The hop to add</param>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_AddHop_AddsHopToListAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_AddHop_MaintainsOrderedListAsync</tests>
  public void AddHop(MessageHop hop) {
    Hops.Add(hop);
  }

  /// <summary>
  /// Returns <see cref="ReceptorInvocations"/>, allocating and assigning an empty list
  /// if it is currently null. Used by <see cref="Messaging.EnvelopeReceptorDedupStore"/>.
  /// </summary>
  public List<ReceptorInvocationRecord> GetOrCreateReceptorInvocations() {
    return ReceptorInvocations ??= [];
  }

  /// <summary>
  /// Gets the current topic by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The topic from the most recent current hop, or null if no hops have a topic</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentTopic_ReturnsNull_WhenNoHopsHaveTopicAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentTopic_ReturnsMostRecentNonNullTopicAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentTopic_IgnoresCausationHopsAsync</tests>
  public string? GetCurrentTopic() {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return null;
    }

    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && !string.IsNullOrEmpty(Hops[i].Topic)) {
        return Hops[i].Topic;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the current stream key by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The stream key from the most recent current hop, or null if no hops have a stream key</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentStreamId_ReturnsNull_WhenNoHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentStreamId_ReturnsMostRecentNonNullStreamIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentStreamId_IgnoresCausationHopsAsync</tests>
  public string? GetCurrentStreamId() {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return null;
    }

    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && !string.IsNullOrEmpty(Hops[i].StreamId)) {
        return Hops[i].StreamId;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the current partition index by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The partition index from the most recent current hop, or null if no hops have a partition index</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentPartitionIndex_ReturnsNull_WhenNoHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentPartitionIndex_ReturnsMostRecentNonNullValueAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentPartitionIndex_IgnoresCausationHopsAsync</tests>
  public int? GetCurrentPartitionIndex() {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return null;
    }

    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && Hops[i].PartitionIndex.HasValue) {
        return Hops[i].PartitionIndex;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the current sequence number by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The sequence number from the most recent current hop, or null if no hops have a sequence number</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSequenceNumber_ReturnsNull_WhenNoHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSequenceNumber_ReturnsMostRecentNonNullValueAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSequenceNumber_IgnoresCausationHopsAsync</tests>
  public long? GetCurrentSequenceNumber() {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return null;
    }

    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && Hops[i].SequenceNumber.HasValue) {
        return Hops[i].SequenceNumber;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the current scope by walking forward through current message hops and merging deltas.
  /// Each hop's ScopeDelta is applied to build the full ScopeContext.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The merged ScopeContext from all current hops, or null if no hops have scope deltas</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/ScopeDeltaIntegrationTests.cs</tests>
  public ScopeContext? GetCurrentScope() {
    // Defensive: Handle null or empty hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return null;
    }

    // Walk forwards through current hops, applying each delta
    return Hops
      .Where(h => h.Type == HopType.Current)
      .Select(hop => hop.Scope)
      .Where(scope => scope != null)
      .Aggregate((ScopeContext?)null, (current, scope) => scope!.ApplyTo(current));
  }

  /// <summary>
  /// Gets the current security context by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The security context from the most recent current hop, or null if no hops have a security context</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_ReturnsNull_WhenNoHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_ReturnsMostRecentNonNullValueAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_IgnoresCausationHopsAsync</tests>
  [Obsolete("Use GetCurrentScope() instead. This method returns the old SecurityContext type.")]
  public SecurityContext? GetCurrentSecurityContext() {
    // Defensive: Handle null or empty hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return null;
    }

    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && Hops[i].Scope != null) {
        // Return a simple SecurityContext from the first hop's scope for backwards compatibility
        var scope = GetCurrentScope();
        if (scope?.Scope != null) {
          return new SecurityContext {
            UserId = scope.Scope.UserId,
            TenantId = scope.Scope.TenantId
          };
        }
        return null;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the message timestamp from the first hop.
  /// Every message originates somewhere, so the first hop's timestamp is the message timestamp.
  /// </summary>
  /// <returns>The timestamp of the first hop</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMessageTimestamp_ReturnsFirstHopTimestampAsync</tests>
  public DateTimeOffset GetMessageTimestamp() {
    return Hops?.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
  }

  /// <summary>
  /// Gets the correlation ID from the first hop.
  /// The first hop establishes the correlation context for the entire message flow.
  /// </summary>
  /// <returns>The correlation ID from the first hop, or null if not set</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  public CorrelationId? GetCorrelationId() {
    return Hops?.Count > 0 ? Hops[0].CorrelationId : null;
  }

  /// <summary>
  /// Gets the causation ID from the first hop.
  /// The first hop establishes what message caused this message to be created.
  /// </summary>
  /// <returns>The causation ID from the first hop, or null if not set</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  public MessageId? GetCausationId() {
    return Hops?.Count > 0 ? Hops[0].CausationId : null;
  }

  /// <summary>
  /// Gets a specific metadata value by walking backwards through current message hops until the key is found.
  /// Later hops override earlier hops for the same key.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <param name="key">The metadata key to retrieve</param>
  /// <returns>The JsonElement value from the most recent current hop that has this key, or null if not found</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_ReturnsNull_WhenKeyNotFoundAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_ReturnsLatestValue_WhenKeyExistsInMultipleHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_IgnoresCausationHopsAsync</tests>
  public JsonElement? GetMetadata(string key) {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return null;
    }

    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && Hops[i].Metadata != null && Hops[i].Metadata!.TryGetValue(key, out var value)) {
        return value;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets all metadata by stitching together metadata from all current message hops.
  /// Later hops override earlier hops for the same key (dictionary merge).
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>A dictionary containing all metadata with later hops taking precedence</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllMetadata_StitchesAllMetadataAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllMetadata_ReturnsEmpty_WhenNoHopsHaveMetadataAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllMetadata_IgnoresCausationHopsAsync</tests>
  public IReadOnlyDictionary<string, JsonElement> GetAllMetadata() {
    var result = new Dictionary<string, JsonElement>();

    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return result;
    }

    // Walk forwards through current hops only, later hops override earlier ones
    // S3267: Multi-statement loop body — LINQ would reduce readability
#pragma warning disable S3267
    foreach (var hop in Hops.Where(h => h.Type == HopType.Current)) {
      if (hop.Metadata != null) {
        foreach (var kvp in hop.Metadata) {
          result[kvp.Key] = kvp.Value;
        }
      }
    }
#pragma warning restore S3267

    return result;
  }

  /// <summary>
  /// Gets all policy decisions by stitching together decisions from all current message hops.
  /// Returns decisions in chronological order (first hop to last hop).
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>A list containing all policy decisions across all current hops</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_ReturnsEmpty_WhenNoHopsHaveTrailsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_ReturnsSingleHopDecisionsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_StitchesDecisionsAcrossMultipleHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_MaintainsChronologicalOrderAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_SkipsHopsWithoutTrailsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_IgnoresCausationHopsAsync</tests>
  public IReadOnlyList<PolicyDecision> GetAllPolicyDecisions() {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return [];
    }

    // Walk forwards through current hops only to maintain chronological order
    return [.. Hops
      .Where(h => h.Type == HopType.Current && h.Trail != null)
      .SelectMany(hop => hop.Trail!.Decisions)];
  }

  /// <summary>
  /// Gets all causation hops (hops from the parent/causation message).
  /// These provide distributed tracing context showing what led to this message.
  /// Returns hops in the order they appear in the list.
  /// </summary>
  /// <returns>A list containing all causation hops</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCausationHops_ReturnsEmpty_WhenNoCausationHopsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCausationHops_ReturnsOnlyCausationHopsAsync</tests>
  public IReadOnlyList<MessageHop> GetCausationHops() {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return [];
    }

    return [.. Hops.Where(h => h.Type == HopType.Causation)];
  }

  /// <summary>
  /// Gets all current message hops (excluding causation hops).
  /// Returns hops in the order they appear in the list.
  /// </summary>
  /// <returns>A list containing all current message hops</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentHops_ReturnsOnlyCurrentHopsAsync</tests>
  public IReadOnlyList<MessageHop> GetCurrentHops() {
    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return [];
    }

    return [.. Hops.Where(h => h.Type == HopType.Current)];
  }
}
