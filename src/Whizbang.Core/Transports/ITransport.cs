using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Transports;

/// <summary>
/// A deserialized transport message ready for batch processing.
/// Value type to avoid heap allocations when batching many messages.
/// </summary>
/// <param name="Envelope">The deserialized message envelope.</param>
/// <param name="EnvelopeType">The assembly-qualified envelope type name, or null if not available.</param>
/// <docs>messaging/transports/transports#transport-message</docs>
/// <tests>tests/Whizbang.Transports.Tests/SubscribeBatchTests.cs</tests>
public readonly record struct TransportMessage(
  IMessageEnvelope Envelope,
  string? EnvelopeType
);

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
  /// <exception cref="OperationCanceledException">Thrown when initialization is canceled</exception>
  Task InitializeAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Reports whether the transport can currently reach its broker — a cheap, non-throwing connectivity
  /// signal for the managed-resource health model (<c>ConnectivityHealthSource</c>). The default returns
  /// <see cref="IsInitialized"/>; a transport holding a live connection handle overrides it to detect a
  /// connection that dropped <em>after</em> initialization (RabbitMQ <c>IConnection.IsOpen</c>, Service
  /// Bus <c>!ServiceBusClient.IsClosed</c>). Called only while the system is running, so it need not
  /// re-check initialization.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> if the broker is reachable.</returns>
  /// <docs>resilience/managed-resource-health</docs>
  ValueTask<bool> CheckConnectivityAsync(CancellationToken cancellationToken = default)
    => ValueTask.FromResult(IsInitialized);

  /// <summary>
  /// Gets the capabilities of this transport.
  /// Describes what patterns and semantics this transport supports.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_Capabilities_ReturnsTransportCapabilitiesAsync</tests>
  TransportCapabilities Capabilities { get; }

  /// <summary>
  /// Maximum per-message size (envelope + body + headers) the transport will
  /// accept on the wire, in bytes. <c>null</c> means there is no enforced
  /// limit relevant to Whizbang offload-strategy decisions (e.g., in-process
  /// transports, or transports whose ceiling is high enough that no Whizbang
  /// outbox message could reasonably exceed it).
  /// </summary>
  /// <remarks>
  /// Size-aware strategies — composite events, body offload — read this value
  /// pre-flight in <c>OutboxPublishWorker</c> to decide whether to send a
  /// large message inline or route it through a body-store (claim-check
  /// pattern). Offload thresholds are set BELOW this value to leave room for
  /// envelope headers and broker metadata.
  /// </remarks>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_MaxMessageSizeBytes_InProcessTransport_ReturnsNullAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQTransportTests.cs:MaxMessageSizeBytes_ReturnsNull_NoEnforcedLimitAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:MaxMessageSizeBytes_Returns256KB_StandardTierCeilingAsync</tests>
  /// <docs>messaging/transports/transports#max-message-size</docs>
  long? MaxMessageSizeBytes => null;

  /// <summary>
  /// Publishes a message to a destination (fire-and-forget).
  /// The message is sent asynchronously with no response expected.
  /// </summary>
  /// <param name="envelope">The message envelope to publish</param>
  /// <param name="destination">The destination to publish to</param>
  /// <param name="envelopeType">Optional assembly-qualified name of the envelope type. If provided, used instead of envelope.GetType() for serialization metadata.</param>
  /// <param name="preSerializedBytes">Optional pre-serialized envelope bytes. When set, wire transports MUST use these bytes on the wire and SKIP their internal serialization. Set by upstream callers (e.g., <c>TransportPublishStrategy</c>) that already serialized once for size measurement / post-serialize hook chain. In-process transports may ignore this hint since they don't touch bytes.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the message is published</returns>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_PublishAsync_WithValidMessage_CompletesSuccessfullyAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/ITransportTests.cs:ITransport_PublishAsync_WithCancellation_ThrowsOperationCanceledAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQTransportTests.cs:PublishAsync_WithPreSerializedBytes_UsesHintNotSerializerAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusTransportUnitTests.cs:PublishAsync_WithPreSerializedBytes_UsesHintNotSerializerAsync</tests>
  Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    string? envelopeType = null,
    ReadOnlyMemory<byte>? preSerializedBytes = null,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Subscribes to messages from a destination with transport-level batch collection.
  /// The transport collects incoming messages into batches and calls the batch handler
  /// when a batch is ready (size reached, sliding window timeout, or hard max timeout).
  /// </summary>
  /// <param name="batchHandler">Handler invoked with a batch of deserialized messages. Called once per batch, not per message.</param>
  /// <param name="destination">The destination to subscribe to</param>
  /// <param name="batchOptions">Configuration for batch size, sliding window, and hard max timers</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A subscription that can be used to control or cancel the subscription</returns>
  /// <tests>tests/Whizbang.Transports.Tests/SubscribeBatchTests.cs</tests>
  /// <docs>messaging/transports/transports#batch-subscribe</docs>
  Task<ISubscription> SubscribeBatchAsync(
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportDestination destination,
    TransportBatchOptions batchOptions,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Opens a long-lived push subscription on the broker's dead-letter queue/subqueue
  /// for the supplied destination. The transport invokes <paramref name="handler"/>
  /// for each DLQ message as it arrives, eliminating the polling latency the
  /// <see cref="Whizbang.Core.Workers.TransportDeadLetterDrainWorker"/> would otherwise
  /// incur.
  /// </summary>
  /// <remarks>
  /// Default implementation throws <see cref="NotSupportedException"/> so transports
  /// without push DLQ semantics (or that haven't been updated yet) stay on the
  /// polling fallback. The worker MUST handle that exception gracefully.
  /// </remarks>
  /// <param name="handler">Invoked per DLQ message; the worker maps it to recovery action.</param>
  /// <param name="destination">DLQ destination (broker-specific resolution — e.g. "$DeadLetterQueue" subqueue on ASB).</param>
  /// <param name="cancellationToken">Caller cancellation.</param>
  /// <returns>Subscription handle; dispose to detach.</returns>
  /// <docs>messaging/transports/dlq-push-subscription</docs>
  Task<ISubscription> SubscribeToDeadLetterAsync(
    Func<TransportMessage, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default
  ) => throw new NotSupportedException(
    $"Transport '{GetType().Name}' does not implement push DLQ subscription. The TransportDeadLetterDrainWorker will fall back to polling on IntervalMinutes.");

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
