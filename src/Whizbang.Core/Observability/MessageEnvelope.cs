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
  /// Parameterless constructor for object initializer syntax.
  /// </summary>
  public MessageEnvelope() {
  }

  /// <summary>
  /// Constructor for JSON deserialization with all required properties.
  /// </summary>
  [System.Text.Json.Serialization.JsonConstructor]
  [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
  public MessageEnvelope(MessageId messageId, TMessage payload, List<MessageHop> hops) {
    MessageId = messageId;
    Payload = payload;
    Hops = hops;
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

    ScopeContext? result = null;

    // Walk forwards through current hops, applying each delta
    foreach (var hop in Hops.Where(h => h.Type == HopType.Current)) {
      if (hop.Scope != null) {
        result = hop.Scope.ApplyTo(result);
      }
    }

    return result;
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
    return Hops != null && Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
  }

  /// <summary>
  /// Gets the correlation ID from the first hop.
  /// The first hop establishes the correlation context for the entire message flow.
  /// </summary>
  /// <returns>The correlation ID from the first hop, or null if not set</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  public CorrelationId? GetCorrelationId() {
    return Hops != null && Hops.Count > 0 ? Hops[0].CorrelationId : null;
  }

  /// <summary>
  /// Gets the causation ID from the first hop.
  /// The first hop establishes what message caused this message to be created.
  /// </summary>
  /// <returns>The causation ID from the first hop, or null if not set</returns>
  /// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
  public MessageId? GetCausationId() {
    return Hops != null && Hops.Count > 0 ? Hops[0].CausationId : null;
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
    foreach (var hop in Hops.Where(h => h.Type == HopType.Current)) {
      if (hop.Metadata != null) {
        foreach (var kvp in hop.Metadata) {
          result[kvp.Key] = kvp.Value;
        }
      }
    }

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
    var result = new List<PolicyDecision>();

    // Defensive: Handle null or empty Hops gracefully
    if (Hops == null || Hops.Count == 0) {
      return result;
    }

    // Walk forwards through current hops only to maintain chronological order
    foreach (var hop in Hops.Where(h => h.Type == HopType.Current)) {
      if (hop.Trail != null) {
        result.AddRange(hop.Trail.Decisions);
      }
    }

    return result;
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
