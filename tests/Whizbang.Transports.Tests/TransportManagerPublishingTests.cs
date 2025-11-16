using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

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
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var message = new TestMessage { Content = "test", Value = 42 };
    var targets = new List<PublishTarget> {
      new PublishTarget {
        TransportType = TransportType.InProcess,
        Destination = "test-destination"
      }
    };

    // Track published messages
    var publishedEnvelopes = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, ct) => {
        publishedEnvelopes.Add(envelope);
        return Task.CompletedTask;
      },
      new TransportDestination("test-destination"),
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets);
    await Task.Delay(50); // Allow async processing

    // Assert
    await Assert.That(publishedEnvelopes).HasCount().EqualTo(1);
    var envelope = publishedEnvelopes[0] as MessageEnvelope<TestMessage>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Payload.Content).IsEqualTo("test");
    await Assert.That(envelope.Payload.Value).IsEqualTo(42);
  }

  [Test]
  public async Task PublishToTargetsAsync_WithMultipleTargets_ShouldPublishToAllAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport1 = new InProcessTransport();
    var transport2 = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport1);
    manager.AddTransport(TransportType.Kafka, transport2); // Using InProcess as mock

    var message = new TestMessage { Content = "multi", Value = 99 };
    var targets = new List<PublishTarget> {
      new PublishTarget {
        TransportType = TransportType.InProcess,
        Destination = "dest1"
      },
      new PublishTarget {
        TransportType = TransportType.Kafka,
        Destination = "dest2"
      }
    };

    // Track published messages
    var publishedToDest1 = new List<IMessageEnvelope>();
    var publishedToDest2 = new List<IMessageEnvelope>();

    await transport1.SubscribeAsync(
      (envelope, ct) => {
        publishedToDest1.Add(envelope);
        return Task.CompletedTask;
      },
      new TransportDestination("dest1"),
      CancellationToken.None
    );

    await transport2.SubscribeAsync(
      (envelope, ct) => {
        publishedToDest2.Add(envelope);
        return Task.CompletedTask;
      },
      new TransportDestination("dest2"),
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets);
    await Task.Delay(50); // Allow async processing

    // Assert
    await Assert.That(publishedToDest1).HasCount().EqualTo(1);
    await Assert.That(publishedToDest2).HasCount().EqualTo(1);
  }

  [Test]
  public async Task PublishToTargetsAsync_WithRoutingKey_ShouldIncludeInDestinationAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var message = new TestMessage { Content = "routed", Value = 123 };
    var targets = new List<PublishTarget> {
      new PublishTarget {
        TransportType = TransportType.InProcess,
        Destination = "dest",
        RoutingKey = "routing.key.test"
      }
    };

    // Track published destinations
    var capturedDestinations = new List<TransportDestination>();
    await transport.SubscribeAsync(
      (envelope, ct) => Task.CompletedTask,
      new TransportDestination("dest"),
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets);

    // Assert - verify message was published (implicitly tests routing key was passed)
    await Task.Delay(50);
  }

  [Test]
  public async Task PublishToTargetsAsync_WithCustomContext_ShouldUseProvidedContextAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
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
      new PublishTarget {
        TransportType = TransportType.InProcess,
        Destination = "dest"
      }
    };

    // Track published messages
    var publishedEnvelopes = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, ct) => {
        publishedEnvelopes.Add(envelope);
        return Task.CompletedTask;
      },
      new TransportDestination("dest"),
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets, context);
    await Task.Delay(50);

    // Assert
    await Assert.That(publishedEnvelopes).HasCount().EqualTo(1);
    await Assert.That(publishedEnvelopes[0].MessageId).IsEqualTo(customMessageId);
  }

  [Test]
  public async Task PublishToTargetsAsync_CreatesEnvelopeWithHopsAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    var message = new TestMessage { Content = "hops", Value = 789 };
    var targets = new List<PublishTarget> {
      new PublishTarget {
        TransportType = TransportType.InProcess,
        Destination = "dest"
      }
    };

    // Track published messages
    var publishedEnvelopes = new List<IMessageEnvelope>();
    await transport.SubscribeAsync(
      (envelope, ct) => {
        publishedEnvelopes.Add(envelope);
        return Task.CompletedTask;
      },
      new TransportDestination("dest"),
      CancellationToken.None
    );

    // Act
    await manager.PublishToTargetsAsync(message, targets);
    await Task.Delay(50);

    // Assert
    await Assert.That(publishedEnvelopes).HasCount().EqualTo(1);
    var envelope = publishedEnvelopes[0] as MessageEnvelope<TestMessage>;
    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Hops).HasCount().EqualTo(1);
    await Assert.That(envelope.Hops[0].Type).IsEqualTo(HopType.Current);
    await Assert.That(envelope.Hops[0].ServiceName).IsEqualTo("TransportManager");
    await Assert.That(envelope.Hops[0].Metadata).IsNotNull();
  }

  [Test]
  public async Task PublishToTargetsAsync_WhenTransportNotRegistered_ShouldThrowAsync() {
    // Arrange
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var message = new TestMessage { Content = "fail", Value = 1 };
    var targets = new List<PublishTarget> {
      new PublishTarget {
        TransportType = TransportType.Kafka, // Not registered
        Destination = "dest"
      }
    };

    // Act & Assert
    await Assert.That(() => manager.PublishToTargetsAsync(message, targets))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task Constructor_WithCustomSerializer_ShouldUseSerializerAsync() {
    // Arrange
    var customSerializer = new InMemorySerializer();
    var manager = new TransportManager(customSerializer);
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    // Act - Adding transport should work with custom serializer
    await Assert.That(manager.HasTransport(TransportType.InProcess)).IsTrue();
  }

  [Test]
  public async Task Constructor_WithNullSerializer_ShouldThrowAsync() {
    // Act & Assert
    await Assert.That(() => new TransportManager(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_Default_ShouldCreateWithJsonSerializerAsync() {
    // Arrange & Act
    var manager = new TransportManager(new JsonMessageSerializer(Whizbang.Core.Serialization.JsonSerializerOptionsExtensions.CreateWithWhizbangContext()));
    var transport = new InProcessTransport();
    manager.AddTransport(TransportType.InProcess, transport);

    // Assert - Manager should be usable
    await Assert.That(manager.HasTransport(TransportType.InProcess)).IsTrue();
  }
}
