using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Transports.Tests.Generated;

namespace Whizbang.Transports.Tests;

/// <summary>
/// Tests for TransportManager publishing functionality.
/// These tests ensure proper message publishing across transports.
/// </summary>
[Category("Transports")]
public class TransportManagerPublishingTests {
  [Test]
  public async Task PublishToTargetsAsync_WithSingleTarget_ShouldPublishAsync() {
    // Arrange
    var manager = new TransportManager(new Whizbang.Core.Observability.ServiceInstanceProvider());
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var message = new TestMessage { Content = "test", Value = 42 };
    var targets = new List<PublishTarget> {
      new() {
        TransportType = TransportType.InProcess,
        Destination = "test-destination"
      }
    };

    // Track published messages via completion signal (no polling / delays)
    var publishedEnvelopes = new List<IMessageEnvelope>();
    var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    await transport.SubscribeBatchAsync(
      async (batch, ct) => {
        foreach (var msg in batch) {
          publishedEnvelopes.Add(msg.Envelope);
        }
        received.TrySetResult(true);
      },
      new TransportDestination("test-destination"),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 },
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets);
    await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // Assert
    await Assert.That(publishedEnvelopes).Count().IsEqualTo(1);
    var envelope = publishedEnvelopes[0] as MessageEnvelope<TestMessage>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Payload.Content).IsEqualTo("test");
    await Assert.That(envelope.Payload.Value).IsEqualTo(42);
  }

  [Test]
  public async Task PublishToTargetsAsync_WithMultipleTargets_ShouldPublishToAllAsync() {
    // Arrange
    var manager = new TransportManager(new Whizbang.Core.Observability.ServiceInstanceProvider());
    var transport1 = new InProcessTransport();
    var transport2 = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport1);
    manager.AddTransport(TransportType.Kafka, transport2); // Using InProcess as mock

    var message = new TestMessage { Content = "multi", Value = 99 };
    var targets = new List<PublishTarget> {
      new() {
        TransportType = TransportType.InProcess,
        Destination = "dest1"
      },
      new() {
        TransportType = TransportType.Kafka,
        Destination = "dest2"
      }
    };

    // Track published messages via completion signals (no polling / delays)
    var publishedToDest1 = new List<IMessageEnvelope>();
    var publishedToDest2 = new List<IMessageEnvelope>();
    var receivedDest1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receivedDest2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    await transport1.SubscribeBatchAsync(
      async (batch, ct) => {
        foreach (var msg in batch) {
          publishedToDest1.Add(msg.Envelope);
        }
        receivedDest1.TrySetResult(true);
      },
      new TransportDestination("dest1"),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 },
      CancellationToken.None
    );

    await transport2.SubscribeBatchAsync(
      async (batch, ct) => {
        foreach (var msg in batch) {
          publishedToDest2.Add(msg.Envelope);
        }
        receivedDest2.TrySetResult(true);
      },
      new TransportDestination("dest2"),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 },
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets);
    await Task.WhenAll(receivedDest1.Task, receivedDest2.Task).WaitAsync(TimeSpan.FromSeconds(10));

    // Assert
    await Assert.That(publishedToDest1).Count().IsEqualTo(1);
    await Assert.That(publishedToDest2).Count().IsEqualTo(1);
  }

  [Test]
  public async Task PublishToTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync() {
    // Arrange - the routing key only exists on the TransportDestination the manager builds; no
    // subscriber can see it, because in-process delivery keys on the address alone. So the
    // transport itself is the observer here. (This test previously only waited for delivery,
    // which a manager that dropped the routing key entirely would have satisfied.)
    var manager = new TransportManager(new Whizbang.Core.Observability.ServiceInstanceProvider());
    var transport = new DestinationRecordingTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var message = new TestMessage { Content = "routed", Value = 123 };
    var targets = new List<PublishTarget> {
      new() {
        TransportType = TransportType.InProcess,
        Destination = "dest",
        RoutingKey = "routing.key.test"
      }
    };

    // Act
    await manager.PublishToTargetsAsync(message, targets);

    // Assert - address and routing key both reach the transport. A broker that routes on the key
    // (RabbitMQ topic exchanges, Service Bus subscription filters) silently delivers nowhere if
    // the manager drops it, and delivery-only assertions cannot tell the difference.
    await Assert.That(transport.Destinations).Count().IsEqualTo(1);
    await Assert.That(transport.Destinations[0].Address).IsEqualTo("dest");
    await Assert.That(transport.Destinations[0].RoutingKey).IsEqualTo("routing.key.test")
      .Because("the target's routing key must be carried onto the destination handed to the transport");
  }

  [Test]
  public async Task PublishToTargetsAsync_WithCustomContext_ShouldUseProvidedContextAsync() {
    // Arrange
    var manager = new TransportManager(new Whizbang.Core.Observability.ServiceInstanceProvider());
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var message = new TestMessage { Content = "context", Value = 456 };
    var customMessageId = MessageId.New();
    var customCorrelationId = CorrelationId.New();
    var customCausationId = MessageId.New();

    var context = new MessageContext {
      MessageId = customMessageId,
      CorrelationId = customCorrelationId,
      CausationId = customCausationId
    };

    var targets = new List<PublishTarget> {
      new() {
        TransportType = TransportType.InProcess,
        Destination = "dest"
      }
    };

    // Track published messages via completion signal
    var publishedEnvelopes = new List<IMessageEnvelope>();
    var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    await transport.SubscribeBatchAsync(
      async (batch, ct) => {
        foreach (var msg in batch) {
          publishedEnvelopes.Add(msg.Envelope);
        }
        received.TrySetResult(true);
      },
      new TransportDestination("dest"),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 },
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets, context);
    await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // Assert
    await Assert.That(publishedEnvelopes).Count().IsEqualTo(1);
    await Assert.That(publishedEnvelopes[0].MessageId).IsEqualTo(customMessageId);
  }

  [Test]
  public async Task PublishToTargetsAsync_CreatesEnvelopeWithHopsAsync() {
    // Arrange
    var manager = new TransportManager(Whizbang.Core.Observability.UnknownServiceInstanceProvider.Instance);
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var message = new TestMessage { Content = "hops", Value = 789 };
    var targets = new List<PublishTarget> {
      new() {
        TransportType = TransportType.InProcess,
        Destination = "dest"
      }
    };

    // Track published messages via completion signal
    var publishedEnvelopes = new List<IMessageEnvelope>();
    var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    await transport.SubscribeBatchAsync(
      async (batch, ct) => {
        foreach (var msg in batch) {
          publishedEnvelopes.Add(msg.Envelope);
        }
        received.TrySetResult(true);
      },
      new TransportDestination("dest"),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 },
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets);
    await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // Assert
    await Assert.That(publishedEnvelopes).Count().IsEqualTo(1);
    var envelope = publishedEnvelopes[0] as MessageEnvelope<TestMessage>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Hops).Count().IsEqualTo(1);
    await Assert.That(envelope.Hops[0].Type).IsEqualTo(HopType.Current);
    await Assert.That(envelope.Hops[0].ServiceInstance.ServiceName).IsEqualTo("Unknown");  // No instance provider configured
    await Assert.That(envelope.Hops[0].ServiceInstance.InstanceId).IsEqualTo(Guid.Empty);
    // Verify CorrelationId and CausationId are set (proper properties instead of Metadata)
    await Assert.That(envelope.Hops[0].CorrelationId).IsNotNull();
    await Assert.That(envelope.Hops[0].CausationId).IsNotNull();
  }

  [Test]
  public async Task PublishToTargetsAsync_WhenTransportNotRegistered_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager(new Whizbang.Core.Observability.ServiceInstanceProvider());
    var message = new TestMessage { Content = "fail", Value = 1 };
    var targets = new List<PublishTarget> {
      new() {
        TransportType = TransportType.Kafka, // Not registered
        Destination = "dest"
      }
    };

    // Act & Assert
    await Assert.That(() => manager.PublishToTargetsAsync(message, targets))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task Constructor_Default_ShouldCreateWithJsonSerializerAsync() {
    // Arrange & Act
    var manager = new TransportManager(new Whizbang.Core.Observability.ServiceInstanceProvider());
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    // Assert - Manager should be usable
    await Assert.That(manager.HasTransport(TransportType.InProcess)).IsTrue();
  }

  /// <summary>
  /// Records the <see cref="TransportDestination"/> each publish is addressed to. The routing key
  /// never travels with the envelope, so this is the only place a test can see whether the
  /// manager put it on the destination.
  /// </summary>
  private sealed class DestinationRecordingTransport : ITransport {
    public List<TransportDestination> Destinations { get; } = [];

    public bool IsInitialized => true;

    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) {
      Destinations.Add(destination);
      return Task.CompletedTask;
    }

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("This transport only records publishes.");

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope requestEnvelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull =>
      throw new NotSupportedException("This transport only records publishes.");

    public Task<IReadOnlyList<BulkPublishItemResult>> PublishBatchAsync(
      IReadOnlyList<BulkPublishItem> items,
      TransportDestination destination,
      CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("This transport only records publishes.");
  }
}
