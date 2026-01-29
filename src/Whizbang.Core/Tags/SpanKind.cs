namespace Whizbang.Core.Tags;

/// <summary>
/// Defines the span kind for OpenTelemetry distributed tracing.
/// Maps to the OpenTelemetry SpanKind enumeration.
/// </summary>
/// <remarks>
/// SpanKind helps tracing systems understand the relationship between spans
/// and how to visualize them in trace diagrams.
/// </remarks>
/// <docs>core-concepts/message-tags#span-kind</docs>
public enum SpanKind {
  /// <summary>
  /// Default span kind for internal operations that don't cross service boundaries.
  /// Used for local function calls, database queries, and other internal work.
  /// </summary>
  Internal = 0,

  /// <summary>
  /// Server span kind for handling incoming requests.
  /// The span represents work done by a server processing a request from a client.
  /// </summary>
  Server = 1,

  /// <summary>
  /// Client span kind for outgoing requests.
  /// The span represents a client making a request to a remote service.
  /// </summary>
  Client = 2,

  /// <summary>
  /// Producer span kind for message publishing.
  /// The span represents a producer sending a message to a broker or queue.
  /// </summary>
  Producer = 3,

  /// <summary>
  /// Consumer span kind for message consumption.
  /// The span represents a consumer receiving and processing a message from a broker or queue.
  /// </summary>
  Consumer = 4
}
