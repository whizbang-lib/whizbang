using System.Runtime.CompilerServices;
using System.Text.Json;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Wraps a message with metadata for routing, tracing, and debugging.
/// Carries context across network boundaries and through the entire execution pipeline.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload</typeparam>
public class MessageEnvelope<TMessage> : IMessageEnvelope<TMessage> {
  /// <summary>
  /// Unique identifier for this specific message.
  /// </summary>
  public required MessageId MessageId { get; init; }

  /// <summary>
  /// The actual message payload (strongly-typed).
  /// Also implements IMessageEnvelope.Payload as object for heterogeneous collections.
  /// </summary>
  public required TMessage Payload { get; init; }

  /// <summary>
  /// Explicit implementation of non-generic Payload property.
  /// Returns the same Payload instance, boxed if necessary.
  /// </summary>
  object IMessageEnvelope.Payload => Payload!;

  /// <summary>
  /// Hops this message has taken through the system.
  /// Each hop records routing, security context, metadata, caller information, and policy decisions.
  /// Hops are additive-only (immutable once added).
  /// At least one hop is required (the originating hop).
  /// </summary>
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
  public void AddHop(MessageHop hop) {
    Hops.Add(hop);
  }

  /// <summary>
  /// Gets the current topic by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The topic from the most recent current hop, or null if no hops have a topic</returns>
  public string? GetCurrentTopic() {
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
  public string? GetCurrentStreamKey() {
    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && !string.IsNullOrEmpty(Hops[i].StreamKey)) {
        return Hops[i].StreamKey;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the current partition index by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The partition index from the most recent current hop, or null if no hops have a partition index</returns>
  public int? GetCurrentPartitionIndex() {
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
  public long? GetCurrentSequenceNumber() {
    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && Hops[i].SequenceNumber.HasValue) {
        return Hops[i].SequenceNumber;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the current security context by walking backwards through current message hops until a non-null value is found.
  /// Filters to only HopType.Current hops (ignores causation hops).
  /// </summary>
  /// <returns>The security context from the most recent current hop, or null if no hops have a security context</returns>
  public SecurityContext? GetCurrentSecurityContext() {
    for (int i = Hops.Count - 1; i >= 0; i--) {
      if (Hops[i].Type == HopType.Current && Hops[i].SecurityContext != null) {
        return Hops[i].SecurityContext;
      }
    }
    return null;
  }

  /// <summary>
  /// Gets the message timestamp from the first hop.
  /// Every message originates somewhere, so the first hop's timestamp is the message timestamp.
  /// </summary>
  /// <returns>The timestamp of the first hop</returns>
  public DateTimeOffset GetMessageTimestamp() {
    return Hops != null && Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
  }

  /// <summary>
  /// Gets the correlation ID from the first hop.
  /// The first hop establishes the correlation context for the entire message flow.
  /// </summary>
  /// <returns>The correlation ID from the first hop, or null if not set</returns>
  public CorrelationId? GetCorrelationId() {
    return Hops != null && Hops.Count > 0 ? Hops[0].CorrelationId : null;
  }

  /// <summary>
  /// Gets the causation ID from the first hop.
  /// The first hop establishes what message caused this message to be created.
  /// </summary>
  /// <returns>The causation ID from the first hop, or null if not set</returns>
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
  public JsonElement? GetMetadata(string key) {
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
  public IReadOnlyDictionary<string, JsonElement> GetAllMetadata() {
    var result = new Dictionary<string, JsonElement>();

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
  public IReadOnlyList<PolicyDecision> GetAllPolicyDecisions() {
    var result = new List<PolicyDecision>();

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
  public IReadOnlyList<MessageHop> GetCausationHops() {
    return [.. Hops.Where(h => h.Type == HopType.Causation)];
  }

  /// <summary>
  /// Gets all current message hops (excluding causation hops).
  /// Returns hops in the order they appear in the list.
  /// </summary>
  /// <returns>A list containing all current message hops</returns>
  public IReadOnlyList<MessageHop> GetCurrentHops() {
    return [.. Hops.Where(h => h.Type == HopType.Current)];
  }
}
