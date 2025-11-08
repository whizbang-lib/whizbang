using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Defines a transport abstraction for sending and receiving messages.
/// Transports can be in-process, over HTTP, message queues, or any other medium.
/// </summary>
public interface ITransport {
  /// <summary>
  /// Gets the capabilities of this transport.
  /// Describes what patterns and semantics this transport supports.
  /// </summary>
  TransportCapabilities Capabilities { get; }

  /// <summary>
  /// Publishes a message to a destination (fire-and-forget).
  /// The message is sent asynchronously with no response expected.
  /// </summary>
  /// <param name="envelope">The message envelope to publish</param>
  /// <param name="destination">The destination to publish to</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is published</returns>
  Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Subscribes to messages from a destination.
  /// The handler will be invoked for each message received.
  /// </summary>
  /// <param name="handler">The handler to invoke for each message</param>
  /// <param name="destination">The destination to subscribe to</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A subscription that can be used to control or cancel the subscription</returns>
  Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Sends a request message and waits for a response (request/response pattern).
  /// </summary>
  /// <typeparam name="TRequest">The request message type</typeparam>
  /// <typeparam name="TResponse">The expected response message type</typeparam>
  /// <param name="requestEnvelope">The request message envelope</param>
  /// <param name="destination">The destination to send the request to</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The response message envelope</returns>
  Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) where TRequest : notnull where TResponse : notnull;
}
