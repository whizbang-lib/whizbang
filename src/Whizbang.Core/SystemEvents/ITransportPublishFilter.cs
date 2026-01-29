namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Filters messages for transport publishing based on system event rules.
/// </summary>
/// <remarks>
/// <para>
/// This filter determines whether messages should be published to or received from
/// the message transport (e.g., Service Bus). It implements the <c>LocalOnly</c>
/// pattern for system events.
/// </para>
/// <para>
/// Key behaviors:
/// - Domain events (non-<see cref="ISystemEvent"/>) always flow through transport
/// - System events respect the <see cref="SystemEventOptions.LocalOnly"/> setting
/// - When <c>LocalOnly = true</c> (default), system events stay within the current service
/// - When <c>LocalOnly = false</c> (via <c>Broadcast()</c>), system events are transported
/// </para>
/// </remarks>
/// <docs>core-concepts/system-events#transport-filtering</docs>
public interface ITransportPublishFilter {
  /// <summary>
  /// Determines if the given message should be published to transport.
  /// </summary>
  /// <param name="message">The message to check.</param>
  /// <returns>
  /// <c>true</c> if the message should be published to transport;
  /// <c>false</c> if the message should stay local.
  /// </returns>
  /// <remarks>
  /// Domain events always return <c>true</c>.
  /// System events return <c>!LocalOnly</c> from options.
  /// </remarks>
  bool ShouldPublishToTransport(object message);

  /// <summary>
  /// Determines if messages of the given type should be received from transport.
  /// </summary>
  /// <param name="messageType">The type of message to check.</param>
  /// <returns>
  /// <c>true</c> if messages of this type should be received from transport;
  /// <c>false</c> if the service should ignore this message type from transport.
  /// </returns>
  /// <remarks>
  /// Domain events always return <c>true</c>.
  /// System events return <c>!LocalOnly</c> from options.
  /// </remarks>
  bool ShouldReceiveFromTransport(Type messageType);
}
