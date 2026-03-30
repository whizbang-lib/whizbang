using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Transports;

/// <summary>
/// Defines a transport abstraction for sending and receiving messages.
/// Transports can be in-process, over HTTP, message queues, or any other medium.
/// </summary>
/// <docs>messaging/transports/transports</docs>
public interface ITransport {
  /// <summary>
  /// Gets whether the transport has been successfully initialized.
  /// Transports must be initialized before they can send or receive messages.
  /// </summary>
  bool IsInitialized { get; }

  /// <summary>
  /// Initializes the transport and verifies connectivity.
  /// This method is idempotent - calling it multiple times is safe.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when initialization is successful</returns>
  /// <exception cref="InvalidOperationException">Thrown when transport cannot be initialized</exception>
  /// <exception cref="OperationCanceledException">Thrown when initialization is cancelled</exception>
  Task InitializeAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Gets the capabilities of this transport.
  /// Describes what patterns and semantics this transport supports.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_Capabilities_ReturnsTransportCapabilitiesAsync</tests>
  TransportCapabilities Capabilities { get; }

  /// <summary>
  /// Publishes a message to a destination (fire-and-forget).
  /// The message is sent asynchronously with no response expected.
  /// </summary>
  /// <param name="envelope">The message envelope to publish</param>
  /// <param name="destination">The destination to publish to</param>
  /// <param name="envelopeType">Optional assembly-qualified name of the envelope type. If provided, used instead of envelope.GetType() for serialization metadata.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is published</returns>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_PublishAsync_WithValidMessage_CompletesSuccessfullyAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_PublishAsync_WithCancellation_ThrowsOperationCanceledAsync</tests>
  Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Subscribes to messages from a destination.
  /// The handler will be invoked for each message received.
  /// </summary>
  /// <param name="handler">The handler to invoke for each message. Parameters: (envelope, envelopeType, cancellationToken). The envelopeType is the original assembly-qualified type name before serialization, or null if not available.</param>
  /// <param name="destination">The destination to subscribe to</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A subscription that can be used to control or cancel the subscription</returns>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_SubscribeAsync_RegistersHandler_ReturnsSubscriptionAsync</tests>
  Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
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
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_SendAsync_WithRequestResponse_ReturnsResponseEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_SendAsync_WithTimeout_ThrowsTimeoutExceptionAsync</tests>
  Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope requestEnvelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) where TRequest : notnull where TResponse : notnull;

  /// <summary>
  /// Publishes a batch of messages to the same destination in a single transport operation.
  /// Transports that support the BulkPublish capability should override this for efficiency.
  /// All items in the batch share the same TransportDestination address; per-item routing
  /// is specified via BulkPublishItem.RoutingKey.
  /// </summary>
  /// <param name="items">The batch of items to publish</param>
  /// <param name="destination">The shared destination address for all items</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Per-item results indicating success or failure</returns>
  /// <exception cref="NotSupportedException">Thrown when the transport does not support bulk publishing</exception>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_PublishBatchAsync_WithoutBulkPublishCapability_ThrowsNotSupportedExceptionAsync</tests>
  Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
    IReadOnlyList<BulkPublishItem> items,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) => throw new NotSupportedException(
    "Bulk publish is not supported by this transport. " +
    "Check Capabilities.HasFlag(TransportCapabilities.BulkPublish) before calling.");
}
