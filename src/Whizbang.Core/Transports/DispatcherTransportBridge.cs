using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_WithMessage_DeliversToRemoteDestinationAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_AutomaticallySerializesMessageAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SendToTransportAsync_WithRequestResponse_ReturnsTypedResponseAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SubscribeFromTransportAsync_RoutesIncomingMessagesToDispatcherAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SubscribeFromTransportAsync_DeserializesAndInvokesLocalReceptorAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_PreservesCorrelationIdAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_CreatesEnvelopeWithHopAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SendToTransportAsync_WithExplicitContext_PreservesCorrelationIdAsync</tests>
/// Bridges IDispatcher with ITransport, handling serialization and routing between local and remote messaging.
/// This keeps IDispatcher pure (no transport concerns) while enabling distributed messaging scenarios.
///
/// Responsibilities:
/// - Publishes local messages to remote transport destinations
/// - Subscribes to transport and routes incoming messages to local dispatcher
/// - Handles automatic serialization/deserialization
/// - Creates message envelopes with hops for observability
/// - Supports request/response pattern across transports
/// </summary>
/// <remarks>
/// Creates a new bridge connecting a dispatcher with a transport.
/// </remarks>
/// <param name="dispatcher">The local dispatcher for message handling</param>
/// <param name="transport">The transport for remote messaging</param>
/// <param name="serializer">The serializer for message envelopes</param>
/// <param name="instanceProvider">Optional instance provider for service identity</param>
public class DispatcherTransportBridge(
  IDispatcher dispatcher,
  ITransport transport,
  IMessageSerializer serializer,
  IServiceInstanceProvider instanceProvider
  ) {
  private readonly IDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));

  /// <summary>
  /// Publishes a message to a remote transport destination.
  /// Creates an envelope with hop, serializes, and publishes to transport.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="message">The message to publish</param>
  /// <param name="destination">The remote destination</param>
  /// <param name="context">Optional message context (creates new if null)</param>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_WithMessage_DeliversToRemoteDestinationAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_AutomaticallySerializesMessageAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_PreservesCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:PublishToTransportAsync_CreatesEnvelopeWithHopAsync</tests>
  public async Task PublishToTransportAsync<TMessage>(
    TMessage message,
    TransportDestination destination,
    IMessageContext? context = null
  ) {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(destination);

    // Create or use provided context
    context ??= MessageContext.New();

    // Create envelope with hop for observability
    var envelope = _createEnvelope(message, context);

    // Publish to transport
    await _transport.PublishAsync(envelope, destination, envelopeType: null, CancellationToken.None);
  }

  /// <summary>
  /// Sends a request to a remote destination and waits for a typed response.
  /// Uses transport's request/response pattern.
  /// </summary>
  /// <typeparam name="TRequest">The request message type</typeparam>
  /// <typeparam name="TResponse">The response message type</typeparam>
  /// <param name="request">The request message</param>
  /// <param name="destination">The remote destination</param>
  /// <param name="context">Optional message context (creates new if null)</param>
  /// <returns>The typed response from the remote service</returns>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SendToTransportAsync_WithRequestResponse_ReturnsTypedResponseAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SendToTransportAsync_WithExplicitContext_PreservesCorrelationIdAsync</tests>
  public async Task<TResponse> SendToTransportAsync<TRequest, TResponse>(
    TRequest request,
    TransportDestination destination,
    IMessageContext? context = null
  ) where TRequest : notnull where TResponse : notnull {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(destination);

    // Create or use provided context
    context ??= MessageContext.New();

    // Create request envelope
    var requestEnvelope = _createEnvelope(request, context);

    // Use transport's request/response pattern
    var responseEnvelope = await _transport.SendAsync<TRequest, TResponse>(
      requestEnvelope,
      destination,
      CancellationToken.None
    );

    // Extract payload from response envelope
    var typedResponse = (MessageEnvelope<TResponse>)responseEnvelope;
    return typedResponse.Payload;
  }

  /// <summary>
  /// Subscribes to a transport destination and routes incoming messages to the local dispatcher.
  /// Handles deserialization and local dispatch automatically.
  /// </summary>
  /// <typeparam name="TMessage">The message type to subscribe to</typeparam>
  /// <param name="destination">The transport destination to subscribe to</param>
  /// <returns>Subscription that can be disposed to stop routing</returns>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SubscribeFromTransportAsync_RoutesIncomingMessagesToDispatcherAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/DispatcherTransportBridgeTests.cs:SubscribeFromTransportAsync_DeserializesAndInvokesLocalReceptorAsync</tests>
  public async Task<ISubscription> SubscribeFromTransportAsync<TMessage>(
    TransportDestination destination
  ) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(destination);

    // Subscribe to transport and route to dispatcher
    return await _transport.SubscribeAsync(
      handler: async (envelope, ct) => {
        // Extract message from envelope
        var typedEnvelope = (MessageEnvelope<TMessage>)envelope;
        var message = typedEnvelope.Payload;

        // Route to local dispatcher
        // We await to ensure the message is fully processed before acknowledging
        await _dispatcher.SendAsync(message);
      },
      destination: destination,
      cancellationToken: CancellationToken.None
    );
  }

  /// <summary>
  /// Creates a MessageEnvelope with initial hop containing context information.
  /// </summary>
  private MessageEnvelope<TMessage> _createEnvelope<TMessage>(
    TMessage message,
    IMessageContext context
  ) {
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message!,
      Hops = []
    };

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = context.CorrelationId,
      CausationId = context.CausationId
    };

    envelope.AddHop(hop);
    return envelope;
  }
}
