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
