using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Comprehensive coverage tests for RabbitMQTransport.
/// Covers constructor null guards, publish error paths, batch publish, subscribe edge cases,
/// message deserialization, delivery failure handling, JSON element conversion, and disposal.
/// </summary>
public class RabbitMQTransportCoverageTests {
  // Shared JsonSerializerOptions to avoid CA1869
  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
  };

  #region Helper Methods

  private static RabbitMQTransport _createTransport(
    FakeConnection? connection = null,
    RabbitMQChannelPool? pool = null,
    RabbitMQOptions? options = null,
    FakeChannel? channel = null
  ) {
    channel ??= new FakeChannel();
    connection ??= new FakeConnection(() => Task.FromResult<IChannel>(channel));
    pool ??= new RabbitMQChannelPool(connection, maxChannels: 5);
    options ??= new RabbitMQOptions();

    return new RabbitMQTransport(
      connection,
      _jsonOptions,
      pool,
      options,
      logger: null
    );
  }

  private static async Task<RabbitMQTransport> _createInitializedTransportAsync(
    FakeChannel? channel = null,
    FakeConnection? connection = null,
    RabbitMQChannelPool? pool = null,
    RabbitMQOptions? options = null
  ) {
    channel ??= new FakeChannel();
    connection ??= new FakeConnection(() => Task.FromResult<IChannel>(channel));
    pool ??= new RabbitMQChannelPool(connection, maxChannels: 5);
    options ??= new RabbitMQOptions();

    var transport = new RabbitMQTransport(
      connection,
      _jsonOptions,
      pool,
      options,
      logger: null
    );
    await transport.InitializeAsync();
    return transport;
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope(
    CorrelationId? correlationId = null,
    MessageId? causationId = null
  ) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-content"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown,
          CorrelationId = correlationId,
          CausationId = causationId
        }
      ]
    };
  }

  private static TransportDestination _createDestinationWithSubscriber(
    string exchange = "test-exchange",
    string? routingKey = "#",
    string subscriberName = "test-subscriber"
  ) {
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse($"\"{subscriberName}\"").RootElement.Clone()
    };
    return new TransportDestination(exchange, routingKey, metadata);
  }

  /// <summary>
  /// Simulates a message delivery to a consumer that was registered via SubscribeAsync.
  /// </summary>
  private static async Task _simulateMessageDeliveryAsync(
    FakeChannel channel,
    ReadOnlyMemory<byte> body,
    IDictionary<string, object?>? headers = null,
    string? messageId = null,
    ulong deliveryTag = 1,
    bool redelivered = false
  ) {
    if (channel.LastRegisteredConsumer is not AsyncEventingBasicConsumer consumer) {
      throw new InvalidOperationException("No consumer registered on channel");
    }

    var properties = new BasicProperties {
      MessageId = messageId ?? Guid.NewGuid().ToString(),
      Headers = headers ?? new Dictionary<string, object?>()
    };

    var args = new BasicDeliverEventArgs(
      consumerTag: channel.LastConsumerTag ?? "test-consumer",
      deliveryTag: deliveryTag,
      redelivered: redelivered,
      exchange: "test-exchange",
      routingKey: "#",
      properties: properties,
      body: body
    );

    // Invoke the ReceivedAsync event on the consumer
    // AsyncEventingBasicConsumer exposes ReceivedAsync as an event we can trigger
    await consumer.HandleBasicDeliverAsync(
      channel.LastConsumerTag ?? "test-consumer",
      deliveryTag,
      redelivered,
      "test-exchange",
      "#",
      properties,
      body
    );
  }

  #endregion

  #region Constructor Null Guard Tests

  [Test]
  public async Task Constructor_NullConnection_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new RabbitMQTransport(
      null!,
      _jsonOptions,
      new RabbitMQChannelPool(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())), maxChannels: 5),
      new RabbitMQOptions()
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullJsonOptions_ThrowsArgumentNullExceptionAsync() {
    var conn = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var pool = new RabbitMQChannelPool(conn, maxChannels: 5);
    await Assert.That(() => new RabbitMQTransport(
      conn,
      null!,
      pool,
      new RabbitMQOptions()
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullChannelPool_ThrowsArgumentNullExceptionAsync() {
    var conn = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    await Assert.That(() => new RabbitMQTransport(
      conn,
      _jsonOptions,
      null!,
      new RabbitMQOptions()
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    var conn = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var pool = new RabbitMQChannelPool(conn, maxChannels: 5);
    await Assert.That(() => new RabbitMQTransport(
      conn,
      _jsonOptions,
      pool,
      null!
    )).Throws<ArgumentNullException>();
  }

  #endregion

  #region InitializeAsync Tests

  [Test]
  public async Task InitializeAsync_WhenDisposed_ThrowsObjectDisposedExceptionAsync() {
    var transport = _createTransport();
    await transport.DisposeAsync();

    await Assert.That(async () => await transport.InitializeAsync())
      .Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task InitializeAsync_WhenAlreadyInitialized_ReturnsWithoutErrorAsync() {
    var transport = _createTransport();
    await transport.InitializeAsync();

    // Second call should be a no-op
    await Assert.That(async () => await transport.InitializeAsync()).ThrowsNothing();
    await Assert.That(transport.IsInitialized).IsTrue();
  }

  [Test]
  public async Task InitializeAsync_WhenConnectionNotOpen_ThrowsInvalidOperationExceptionAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel), isOpen: false);
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = new RabbitMQTransport(connection, _jsonOptions, pool, new RabbitMQOptions());

    await Assert.That(async () => await transport.InitializeAsync())
      .Throws<InvalidOperationException>();
  }

  #endregion

  #region PublishAsync Null Guards and Pre-condition Tests

  [Test]
  public async Task PublishAsync_WhenDisposed_ThrowsObjectDisposedExceptionAsync() {
    var transport = _createTransport();
    await transport.InitializeAsync();
    await transport.DisposeAsync();

    await Assert.That(async () => await transport.PublishAsync(
      _createTestEnvelope(),
      new TransportDestination("test")
    )).Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task PublishAsync_NullEnvelope_ThrowsArgumentNullExceptionAsync() {
    var transport = await _createInitializedTransportAsync();

    await Assert.That(async () => await transport.PublishAsync(
      null!,
      new TransportDestination("test")
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_NullDestination_ThrowsArgumentNullExceptionAsync() {
    var transport = await _createInitializedTransportAsync();

    await Assert.That(async () => await transport.PublishAsync(
      _createTestEnvelope(),
      null!
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task PublishAsync_WhenNotInitialized_ThrowsInvalidOperationExceptionAsync() {
    var transport = _createTransport();

    await Assert.That(async () => await transport.PublishAsync(
      _createTestEnvelope(),
      new TransportDestination("test")
    )).Throws<InvalidOperationException>();
  }

  #endregion

  #region PublishAsync Core Logic Tests

  [Test]
  public async Task PublishAsync_WithoutRoutingKey_UsesHashDefaultAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange"); // No routing key

    await transport.PublishAsync(envelope, destination);

    await Assert.That(channel.BasicPublishAsyncCalled).IsTrue();
    // The routing key defaults to "#" when null
    await Assert.That(channel.PublishedMessages.Count).IsEqualTo(1);
    await Assert.That(channel.PublishedMessages[0].RoutingKey).IsEqualTo("#");
  }

  [Test]
  public async Task PublishAsync_WithRoutingKey_UsesProvidedRoutingKeyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange", "orders.created");

    await transport.PublishAsync(envelope, destination);

    await Assert.That(channel.PublishedMessages[0].RoutingKey).IsEqualTo("orders.created");
  }

  [Test]
  public async Task PublishAsync_WithEnvelopeType_UsesProvidedEnvelopeTypeAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange");
    var envelopeType = "MyApp.CustomType, MyApp";

    await transport.PublishAsync(envelope, destination, envelopeType);

    await Assert.That(channel.BasicPublishAsyncCalled).IsTrue();
    // The EnvelopeType header should contain the provided type
    var props = channel.LastPublishedProperties;
    await Assert.That(props).IsNotNull();
    await Assert.That(props!.Headers).IsNotNull();
    await Assert.That(props.Headers!["EnvelopeType"]).IsEqualTo("MyApp.CustomType, MyApp");
  }

  [Test]
  public async Task PublishAsync_WithCorrelationAndCausation_SetsHeadersAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var envelope = _createTestEnvelope(correlationId, causationId);
    var destination = new TransportDestination("test-exchange");

    await transport.PublishAsync(envelope, destination);

    var props = channel.LastPublishedProperties;
    await Assert.That(props).IsNotNull();
    await Assert.That(props!.CorrelationId).IsEqualTo(correlationId.Value.ToString());
    await Assert.That(props.Headers!["CausationId"]).IsEqualTo(causationId.Value.ToString());
  }

  [Test]
  public async Task PublishAsync_WithoutCorrelationAndCausation_DoesNotSetHeadersAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope(); // No correlation/causation
    var destination = new TransportDestination("test-exchange");

    await transport.PublishAsync(envelope, destination);

    var props = channel.LastPublishedProperties;
    await Assert.That(props).IsNotNull();
    // CorrelationId should not be set on the properties
    await Assert.That(props!.CorrelationId).IsNull();
    // CausationId header should not exist
    await Assert.That(props.Headers!.ContainsKey("CausationId")).IsFalse();
  }

  [Test]
  public async Task PublishAsync_WithMetadata_ConvertsAndAddsToHeadersAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var metadata = new Dictionary<string, JsonElement> {
      ["CustomKey"] = JsonDocument.Parse("\"custom-value\"").RootElement.Clone(),
      ["IntKey"] = JsonDocument.Parse("42").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(envelope, destination);

    var props = channel.LastPublishedProperties;
    await Assert.That(props!.Headers!["CustomKey"]).IsEqualTo("custom-value");
    await Assert.That(props.Headers["IntKey"]).IsEqualTo(42);
  }

  [Test]
  public async Task PublishAsync_WhenAlreadyClosedException_WrapsInInvalidOperationExceptionAsync() {
    var channel = new FakeChannel();
    channel.ExceptionToThrowOnPublish = new AlreadyClosedException(
      new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "Connection reset"));
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange");

    await Assert.That(async () => await transport.PublishAsync(envelope, destination))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PublishAsync_WhenGeneralException_WrapsInInvalidOperationExceptionAsync() {
    var channel = new FakeChannel();
    channel.ExceptionToThrowOnPublish = new IOException("Network error");
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange");

    await Assert.That(async () => await transport.PublishAsync(envelope, destination))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PublishAsync_WhenOperationCanceled_PropagatesDirectlyAsync() {
    var channel = new FakeChannel();
    channel.ExceptionToThrowOnPublish = new OperationCanceledException("Canceled");
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange");

    await Assert.That(async () => await transport.PublishAsync(envelope, destination))
      .Throws<OperationCanceledException>();
  }

  #endregion

  #region PublishBatchAsync Tests

  [Test]
  public async Task PublishBatchAsync_WhenDisposed_ThrowsObjectDisposedExceptionAsync() {
    var transport = _createTransport();
    await transport.InitializeAsync();
    await transport.DisposeAsync();

    await Assert.That(async () => await transport.PublishBatchAsync(
      [], new TransportDestination("test")
    )).Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task PublishBatchAsync_NullItems_ThrowsArgumentNullExceptionAsync() {
    var transport = await _createInitializedTransportAsync();

    await Assert.That(async () => await transport.PublishBatchAsync(
      null!, new TransportDestination("test")
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task PublishBatchAsync_NullDestination_ThrowsArgumentNullExceptionAsync() {
    var transport = await _createInitializedTransportAsync();

    await Assert.That(async () => await transport.PublishBatchAsync(
      [], null!
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task PublishBatchAsync_WhenNotInitialized_ThrowsInvalidOperationExceptionAsync() {
    var transport = _createTransport();

    await Assert.That(async () => await transport.PublishBatchAsync(
      [], new TransportDestination("test")
    )).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PublishBatchAsync_EmptyItems_ReturnsEmptyResultsAsync() {
    var transport = await _createInitializedTransportAsync();

    var results = await transport.PublishBatchAsync(
      [], new TransportDestination("test-exchange")
    );

    await Assert.That(results.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PublishBatchAsync_WithValidItems_ReturnsSuccessForAllAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope1 = _createTestEnvelope();
    var envelope2 = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope1,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope1.MessageId.Value
      },
      new() {
        Envelope = envelope2,
        EnvelopeType = null, // Test fallback to GetType().AssemblyQualifiedName
        MessageId = envelope2.MessageId.Value
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange")
    );

    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(results[1].Success).IsTrue();
  }

  [Test]
  public async Task PublishBatchAsync_WithItemRoutingKey_UsesItemRoutingKeyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope.MessageId.Value,
        RoutingKey = "item-specific-key"
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange", "default-key")
    );

    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(channel.PublishedMessages[0].RoutingKey).IsEqualTo("item-specific-key");
  }

  [Test]
  public async Task PublishBatchAsync_WithDestinationRoutingKey_UsesDestinationKeyWhenItemKeyNullAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope.MessageId.Value,
        RoutingKey = null // Will fallback to destination routing key
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange", "dest-key")
    );

    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(channel.PublishedMessages[0].RoutingKey).IsEqualTo("dest-key");
  }

  [Test]
  public async Task PublishBatchAsync_WithMetadata_ConvertsAndAddsToHeadersAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var metadata = new Dictionary<string, JsonElement> {
      ["BatchMeta"] = JsonDocument.Parse("\"batch-value\"").RootElement.Clone()
    };
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope.MessageId.Value
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange", "#", metadata)
    );

    await Assert.That(results[0].Success).IsTrue();
  }

  [Test]
  public async Task PublishBatchAsync_WhenItemFails_RecordsFailureAndContinuesAsync() {
    // We need a channel that fails on the second publish
    var channel = new FakeChannelThatFailsOnSecondPublish();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = await _createInitializedTransportAsync(
      channel: channel, connection: connection, pool: pool);

    var envelope1 = _createTestEnvelope();
    var envelope2 = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope1,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope1.MessageId.Value
      },
      new() {
        Envelope = envelope2,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope2.MessageId.Value
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange")
    );

    await Assert.That(results.Count).IsEqualTo(2);
    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(results[1].Success).IsFalse();
    await Assert.That(results[1].Error).IsNotNull();
  }

  [Test]
  public async Task PublishBatchAsync_WhenAlreadyClosedException_FailsRemainingItemsAsync() {
    // Channel that throws AlreadyClosedException on ExchangeDeclare
    var channel = new FakeChannelThatThrowsOnExchangeDeclare(
      new AlreadyClosedException(
        new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "Closed")));
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = await _createInitializedTransportAsync(
      channel: channel, connection: connection, pool: pool);

    var envelope1 = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope1,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope1.MessageId.Value
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange")
    );

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Success).IsFalse();
    await Assert.That(results[0].Error).Contains("AlreadyClosedException");
  }

  [Test]
  public async Task PublishBatchAsync_WhenGeneralException_FailsRemainingItemsAsync() {
    // Channel that throws general exception on ExchangeDeclare
    var channel = new FakeChannelThatThrowsOnExchangeDeclare(
      new IOException("Network error"));
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = await _createInitializedTransportAsync(
      channel: channel, connection: connection, pool: pool);

    var envelope1 = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope1,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope1.MessageId.Value
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange")
    );

    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Success).IsFalse();
    await Assert.That(results[0].Error).Contains("IOException");
  }

  #endregion

  #region SubscribeAsync Null Guards and Pre-condition Tests

  [Test]
  public async Task SubscribeAsync_WhenDisposed_ThrowsObjectDisposedExceptionAsync() {
    var transport = _createTransport();
    await transport.InitializeAsync();
    await transport.DisposeAsync();

    await Assert.That(async () => await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    )).Throws<ObjectDisposedException>();
  }

  [Test]
  public async Task SubscribeAsync_NullHandler_ThrowsArgumentNullExceptionAsync() {
    var transport = await _createInitializedTransportAsync();

    await Assert.That(async () => await transport.SubscribeAsync(
      null!,
      _createDestinationWithSubscriber()
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SubscribeAsync_NullDestination_ThrowsArgumentNullExceptionAsync() {
    var transport = await _createInitializedTransportAsync();

    await Assert.That(async () => await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      null!
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SubscribeAsync_WhenNotInitialized_ThrowsInvalidOperationExceptionAsync() {
    var transport = _createTransport();

    await Assert.That(async () => await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    )).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task SubscribeAsync_WhenGenericExceptionOnSetup_WrapsInInvalidOperationExceptionAsync() {
    // Channel that throws a general exception on ExchangeDeclare (during subscription setup)
    var channel = new FakeChannelThatThrowsOnExchangeDeclare(new IOException("Network error"));
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = await _createInitializedTransportAsync(
      channel: channel, connection: connection, pool: pool);

    await Assert.That(async () => await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    )).Throws<InvalidOperationException>();
  }

  #endregion

  #region Routing Patterns Tests

  [Test]
  public async Task SubscribeAsync_WithRoutingPatternsArray_CreatesMultipleBindingsAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var patternsJson = JsonDocument.Parse("[\"orders.*\", \"payments.*\"]").RootElement.Clone();
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-sub\"").RootElement.Clone(),
      ["RoutingPatterns"] = patternsJson
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    );

    // Should have two bindings for the two patterns
    await Assert.That(channel.QueueBindings.Count).IsEqualTo(2);
    await Assert.That(channel.QueueBindings[0].RoutingKey).IsEqualTo("orders.*");
    await Assert.That(channel.QueueBindings[1].RoutingKey).IsEqualTo("payments.*");
  }

  [Test]
  public async Task SubscribeAsync_WithEmptyRoutingPatternsArray_FallsBackToDefaultAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var patternsJson = JsonDocument.Parse("[]").RootElement.Clone();
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-sub\"").RootElement.Clone(),
      ["RoutingPatterns"] = patternsJson
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    );

    // Should fall back to "#" default
    await Assert.That(channel.QueueBindings.Count).IsEqualTo(1);
    await Assert.That(channel.QueueBindings[0].RoutingKey).IsEqualTo("#");
  }

  [Test]
  public async Task SubscribeAsync_WithRoutingPatternSingular_UsesSinglePatternAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-sub\"").RootElement.Clone(),
      ["RoutingPattern"] = JsonDocument.Parse("\"orders.created\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    );

    await Assert.That(channel.QueueBindings.Count).IsEqualTo(1);
    await Assert.That(channel.QueueBindings[0].RoutingKey).IsEqualTo("orders.created");
  }

  [Test]
  public async Task SubscribeAsync_WithCommaSeparatedRoutingKey_SplitsIntoPatternsAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-sub\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "orders.*,payments.*", metadata);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    );

    await Assert.That(channel.QueueBindings.Count).IsEqualTo(2);
    await Assert.That(channel.QueueBindings[0].RoutingKey).IsEqualTo("orders.*");
    await Assert.That(channel.QueueBindings[1].RoutingKey).IsEqualTo("payments.*");
  }

  [Test]
  public async Task SubscribeAsync_WithNullRoutingPatternsArrayItems_SkipsEmptyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    // Array with empty string item - should be skipped
    var patternsJson = JsonDocument.Parse("[\"orders.*\", \"\", \"payments.*\"]").RootElement.Clone();
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-sub\"").RootElement.Clone(),
      ["RoutingPatterns"] = patternsJson
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    );

    // Empty string items should be skipped
    await Assert.That(channel.QueueBindings.Count).IsEqualTo(2);
    await Assert.That(channel.QueueBindings[0].RoutingKey).IsEqualTo("orders.*");
    await Assert.That(channel.QueueBindings[1].RoutingKey).IsEqualTo("payments.*");
  }

  [Test]
  public async Task SubscribeAsync_WithRoutingPatternsNotArray_FallsBackAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    // RoutingPatterns is not an array - should fallback
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-sub\"").RootElement.Clone(),
      ["RoutingPatterns"] = JsonDocument.Parse("\"not-an-array\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    );

    // Should fall back to default "#"
    await Assert.That(channel.QueueBindings.Count).IsEqualTo(1);
    await Assert.That(channel.QueueBindings[0].RoutingKey).IsEqualTo("#");
  }

  [Test]
  public async Task SubscribeAsync_WithEmptyRoutingPatternSingular_FallsBackToDefaultAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("\"test-sub\"").RootElement.Clone(),
      ["RoutingPattern"] = JsonDocument.Parse("\"\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    );

    // Empty pattern should fall through to default
    await Assert.That(channel.QueueBindings.Count).IsEqualTo(1);
    await Assert.That(channel.QueueBindings[0].RoutingKey).IsEqualTo("#");
  }

  #endregion

  #region SubscriberName Edge Cases

  [Test]
  public async Task SubscribeAsync_WithNonStringSubscriberName_ThrowsAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    // SubscriberName is a number, not a string
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse("42").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    // Should throw because SubscriberName is not a string and DefaultQueueName is null
    await Assert.That(async () => await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      destination
    )).Throws<InvalidOperationException>();
  }

  #endregion

  #region Dead Letter Exchange Tests

  [Test]
  public async Task SubscribeAsync_WithAutoDeclareDeadLetterExchange_DeclaresDeadLetterInfrastructureAsync() {
    var channel = new FakeChannel();
    var options = new RabbitMQOptions { AutoDeclareDeadLetterExchange = true };
    var transport = await _createInitializedTransportAsync(channel: channel, options: options);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    );

    // Should have declared the DLX exchange (fanout type)
    var dlxExchange = channel.DeclaredExchanges.FirstOrDefault(e => e.Exchange.EndsWith(".dlx", StringComparison.Ordinal));
    await Assert.That(dlxExchange.Exchange).IsNotNull();
    await Assert.That(dlxExchange.Type).IsEqualTo("fanout");

    // Queue args should include dead letter exchange
    await Assert.That(channel.LastQueueDeclareArguments).IsNotNull();
    await Assert.That(channel.LastQueueDeclareArguments!.ContainsKey("x-dead-letter-exchange")).IsTrue();
  }

  [Test]
  public async Task SubscribeAsync_WithoutAutoDeclareDeadLetterExchange_SkipsDeadLetterInfrastructureAsync() {
    var channel = new FakeChannel();
    var options = new RabbitMQOptions { AutoDeclareDeadLetterExchange = false };
    var transport = await _createInitializedTransportAsync(channel: channel, options: options);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    );

    // Should NOT have declared a DLX exchange
    var dlxExchange = channel.DeclaredExchanges.FirstOrDefault(e => e.Exchange.EndsWith(".dlx", StringComparison.Ordinal));
    await Assert.That(dlxExchange.Exchange).IsNull();

    // Queue args should NOT include dead letter exchange
    await Assert.That(
      channel.LastQueueDeclareArguments == null ||
      !channel.LastQueueDeclareArguments.ContainsKey("x-dead-letter-exchange")
    ).IsTrue();
  }

  #endregion

  #region Message Receive / Process Tests

  [Test]
  public async Task OnMessageReceived_WithValidMessage_AcksMessageAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var handlerCalled = new TaskCompletionSource<(IMessageEnvelope, string?)>();

    await transport.SubscribeAsync(
      async (envelope, envelopeType, ct) => {
        handlerCalled.SetResult((envelope, envelopeType));
        await Task.CompletedTask;
      },
      _createDestinationWithSubscriber()
    );

    // Create and serialize a valid envelope
    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);

    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 42);

    var (receivedEnvelope, receivedType) = await handlerCalled.Task;
    await Assert.That(receivedEnvelope).IsNotNull();
    await Assert.That(receivedType).IsEqualTo(envelopeTypeName);
    await Assert.That(channel.BasicAckAsyncCalled).IsTrue();
    await Assert.That(channel.LastAckedDeliveryTag).IsEqualTo((ulong)42);
  }

  [Test]
  public async Task OnMessageReceived_WithMissingEnvelopeTypeHeader_NacksWithoutRequeueAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    );

    // No EnvelopeType header
    var body = Encoding.UTF8.GetBytes("{}");
    var headers = new Dictionary<string, object?>();

    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1);

    // Should nack without requeue (deserialization failure -> DLQ)
    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsFalse();
  }

  [Test]
  public async Task OnMessageReceived_WithEnvelopeTypeNotBytes_NacksAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    );

    // EnvelopeType is a string, not byte[] (RabbitMQ typically sends string headers as byte[])
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = "not-bytes"
    };
    var body = Encoding.UTF8.GetBytes("{}");

    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1);

    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsFalse();
  }

  [Test]
  public async Task OnMessageReceived_WithUnknownEnvelopeType_NacksAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    );

    // EnvelopeType that is not registered in JsonContextRegistry
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes("NonExistent.Type, NonExistent.Assembly")
    };
    var body = Encoding.UTF8.GetBytes("{}");

    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1);

    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsFalse();
  }

  [Test]
  public async Task OnMessageReceived_WhenHandlerThrows_NacksWithRequeueAsync() {
    var channel = new FakeChannel();
    var options = new RabbitMQOptions { MaxDeliveryAttempts = 10 };
    var transport = await _createInitializedTransportAsync(channel: channel, options: options);

    await transport.SubscribeAsync(
      async (e, t, ct) => {
        await Task.CompletedTask;
        throw new InvalidOperationException("Handler failed");
      },
      _createDestinationWithSubscriber()
    );

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    // First delivery (not redelivered) => deliveryCount=1, below MaxDeliveryAttempts => requeue=true
    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1, redelivered: false);

    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsTrue();
  }

  [Test]
  public async Task OnMessageReceived_WhenHandlerThrowsAtMaxDelivery_NacksWithoutRequeueAsync() {
    var channel = new FakeChannel();
    var options = new RabbitMQOptions { MaxDeliveryAttempts = 2 };
    var transport = await _createInitializedTransportAsync(channel: channel, options: options);

    await transport.SubscribeAsync(
      async (e, t, ct) => {
        await Task.CompletedTask;
        throw new InvalidOperationException("Handler failed");
      },
      _createDestinationWithSubscriber()
    );

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    // Redelivered=true => deliveryCount=2, equals MaxDeliveryAttempts => nack without requeue (DLQ)
    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1, redelivered: true);

    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsFalse();
  }

  [Test]
  public async Task OnMessageReceived_WithDeliveryCountHeader_UsesHeaderValueAsync() {
    var channel = new FakeChannel();
    var options = new RabbitMQOptions { MaxDeliveryAttempts = 3 };
    var transport = await _createInitializedTransportAsync(channel: channel, options: options);

    await transport.SubscribeAsync(
      async (e, t, ct) => {
        await Task.CompletedTask;
        throw new InvalidOperationException("Handler failed");
      },
      _createDestinationWithSubscriber()
    );

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName),
      ["x-delivery-count"] = 5 // Exceeds MaxDeliveryAttempts of 3
    };

    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1, redelivered: false);

    // x-delivery-count=5 >= MaxDeliveryAttempts=3 => nack without requeue
    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsFalse();
  }

  [Test]
  public async Task OnMessageReceived_WhenNackThrowsAlreadyClosed_DoesNotPropagateAsync() {
    var channel = new FakeChannel();
    var options = new RabbitMQOptions { MaxDeliveryAttempts = 10 };
    var transport = await _createInitializedTransportAsync(channel: channel, options: options);

    await transport.SubscribeAsync(
      async (e, t, ct) => {
        await Task.CompletedTask;
        throw new InvalidOperationException("Handler failed");
      },
      _createDestinationWithSubscriber()
    );

    // Set channel to throw AlreadyClosedException on nack
    channel.ExceptionToThrowOnNack = new AlreadyClosedException(
      new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "Closed"));

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    // Should not throw - the AlreadyClosedException during nack is caught
    await Assert.That(async () =>
      await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1)
    ).ThrowsNothing();
  }

  [Test]
  public async Task OnMessageReceived_WhenChannelAlreadyClosed_DoesNotPropagateAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    await transport.SubscribeAsync(
      async (e, t, ct) => {
        await Task.CompletedTask;
        throw new AlreadyClosedException(
          new ShutdownEventArgs(ShutdownInitiator.Peer, 320, "Gone"));
      },
      _createDestinationWithSubscriber()
    );

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    // AlreadyClosedException from handler in _processMessageAsync should be caught by _onMessageReceivedAsync
    await Assert.That(async () =>
      await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1)
    ).ThrowsNothing();
  }

  [Test]
  public async Task OnMessageReceived_WhenSubscriptionPaused_NacksWithRequeueAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var subscription = await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    );

    // Pause the subscription
    await subscription.PauseAsync();

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1);

    // Should nack with requeue (paused subscription)
    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsTrue();
  }

  #endregion

  #region SendAsync Tests

  [Test]
  public async Task SendAsync_ThrowsNotSupportedExceptionAsync() {
    var transport = await _createInitializedTransportAsync();

    await Assert.That(async () => await transport.SendAsync<TestMessage, TestMessage>(
      _createTestEnvelope(),
      new TransportDestination("test")
    )).Throws<NotSupportedException>();
  }

  [Test]
  public async Task SendAsync_WhenDisposed_ThrowsObjectDisposedExceptionAsync() {
    var transport = _createTransport();
    await transport.InitializeAsync();
    await transport.DisposeAsync();

    await Assert.That(async () => await transport.SendAsync<TestMessage, TestMessage>(
      _createTestEnvelope(),
      new TransportDestination("test")
    )).Throws<ObjectDisposedException>();
  }

  #endregion

  #region DisposeAsync Tests

  [Test]
  public async Task DisposeAsync_Idempotent_SecondCallDoesNothingAsync() {
    var transport = _createTransport();

    await transport.DisposeAsync();
    // Second dispose should not throw
    await Assert.That(async () => await transport.DisposeAsync()).ThrowsNothing();
  }

  [Test]
  public async Task DisposeAsync_ClearsRecoveryHandlerAsync() {
    var channel = new FakeChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = new RabbitMQTransport(
      connection, _jsonOptions, pool, new RabbitMQOptions());

    var handlerCalled = false;
    transport.SetRecoveryHandler(async ct => {
      handlerCalled = true;
      await Task.CompletedTask;
    });

    await transport.DisposeAsync();

    // Recovery handler should be cleared - invoking recovery should not call it
    await connection.SimulateRecoverySucceededAsync();
    await Assert.That(handlerCalled).IsFalse();
  }

  #endregion

  #region Capabilities Tests

  [Test]
  public async Task Capabilities_IncludesBulkPublishAsync() {
    var transport = _createTransport();

    var capabilities = transport.Capabilities;

    await Assert.That((capabilities & TransportCapabilities.BulkPublish) != 0).IsTrue();
  }

  [Test]
  public async Task Capabilities_DoesNotIncludeOrderedAsync() {
    var transport = _createTransport();

    var capabilities = transport.Capabilities;

    await Assert.That((capabilities & TransportCapabilities.Ordered) == 0).IsTrue();
  }

  #endregion

  #region JsonElement Conversion Tests

  [Test]
  public async Task PublishAsync_WithStringMetadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["str"] = JsonDocument.Parse("\"hello\"").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    await Assert.That(channel.LastPublishedProperties!.Headers!["str"]).IsEqualTo("hello");
  }

  [Test]
  public async Task PublishAsync_WithInt32Metadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["num"] = JsonDocument.Parse("42").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    await Assert.That(channel.LastPublishedProperties!.Headers!["num"]).IsEqualTo(42);
  }

  [Test]
  public async Task PublishAsync_WithInt64Metadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    // Number too big for int32
    var metadata = new Dictionary<string, JsonElement> {
      ["bignum"] = JsonDocument.Parse("9999999999").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    await Assert.That(channel.LastPublishedProperties!.Headers!["bignum"]).IsEqualTo(9999999999L);
  }

  [Test]
  public async Task PublishAsync_WithDoubleMetadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["dbl"] = JsonDocument.Parse("3.14").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    await Assert.That(channel.LastPublishedProperties!.Headers!["dbl"]).IsEqualTo(3.14);
  }

  [Test]
  public async Task PublishAsync_WithTrueMetadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["flag"] = JsonDocument.Parse("true").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    await Assert.That(channel.LastPublishedProperties!.Headers!["flag"]).IsEqualTo(true);
  }

  [Test]
  public async Task PublishAsync_WithFalseMetadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["flag"] = JsonDocument.Parse("false").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    await Assert.That(channel.LastPublishedProperties!.Headers!["flag"]).IsEqualTo(false);
  }

  [Test]
  public async Task PublishAsync_WithNullMetadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["nil"] = JsonDocument.Parse("null").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    await Assert.That(channel.LastPublishedProperties!.Headers!["nil"]).IsNull();
  }

  [Test]
  public async Task PublishAsync_WithArrayMetadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["arr"] = JsonDocument.Parse("[1, 2, 3]").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    var arrValue = channel.LastPublishedProperties!.Headers!["arr"];
    await Assert.That(arrValue).IsTypeOf<List<object?>>();
    var list = (List<object?>)arrValue!;
    await Assert.That(list.Count).IsEqualTo(3);
  }

  [Test]
  public async Task PublishAsync_WithObjectMetadata_ConvertsCorrectlyAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var metadata = new Dictionary<string, JsonElement> {
      ["obj"] = JsonDocument.Parse("{\"key\": \"value\"}").RootElement.Clone()
    };
    var destination = new TransportDestination("test-exchange", "#", metadata);

    await transport.PublishAsync(_createTestEnvelope(), destination);

    var objValue = channel.LastPublishedProperties!.Headers!["obj"];
    await Assert.That(objValue).IsTypeOf<Dictionary<string, object?>>();
    var dict = (Dictionary<string, object?>)objValue!;
    await Assert.That(dict["key"]).IsEqualTo("value");
  }

  #endregion

  #region Batch Publish with Correlation/Causation Tests

  [Test]
  public async Task PublishBatchAsync_WithCorrelationAndCausation_SetsHeadersAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var correlationId = CorrelationId.New();
    var causationId = MessageId.New();
    var envelope = _createTestEnvelope(correlationId, causationId);
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope.MessageId.Value
      }
    };

    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange")
    );

    await Assert.That(results[0].Success).IsTrue();
    // Verify correlation/causation were set on properties
    var props = channel.LastPublishedProperties;
    await Assert.That(props!.CorrelationId).IsEqualTo(correlationId.Value.ToString());
    await Assert.That(props.Headers!["CausationId"]).IsEqualTo(causationId.Value.ToString());
  }

  #endregion

  #region Batch Publish OperationCanceledException Tests

  [Test]
  public async Task PublishBatchAsync_WhenOperationCanceled_PropagatesDirectlyAsync() {
    var channel = new FakeChannelThatThrowsOnExchangeDeclare(new OperationCanceledException("Canceled"));
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = await _createInitializedTransportAsync(
      channel: channel, connection: connection, pool: pool);

    var envelope = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope.MessageId.Value
      }
    };

    await Assert.That(async () => await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange")
    )).Throws<OperationCanceledException>();
  }

  #endregion

  #region Batch Publish Routing Key Fallback Tests

  [Test]
  public async Task PublishBatchAsync_WithNullRoutingKeys_UsesHashDefaultAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var items = new List<BulkPublishItem> {
      new() {
        Envelope = envelope,
        EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
        MessageId = envelope.MessageId.Value,
        RoutingKey = null
      }
    };

    // Destination also has null routing key => defaults to "#"
    var results = await transport.PublishBatchAsync(
      items, new TransportDestination("test-exchange")
    );

    await Assert.That(results[0].Success).IsTrue();
    await Assert.That(channel.PublishedMessages[0].RoutingKey).IsEqualTo("#");
  }

  #endregion

  #region Subscribe Timeout Tests

  [Test]
  public async Task SubscribeAsync_WhenOperationCanceledNotFromToken_WrapsAsTimeoutAsync() {
    // OperationCanceledException thrown when the CancellationToken is NOT the one passed to SubscribeAsync
    // This is interpreted as a timeout
    var channel = new FakeChannelThatThrowsOnBind();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 5);
    var transport = await _createInitializedTransportAsync(
      channel: channel, connection: connection, pool: pool);

    // The TaskCanceledException thrown by FakeChannelThatThrowsOnBind simulates a timeout
    // (CancellationToken.IsCancellationRequested is false)
    await Assert.That(async () => await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    )).Throws<InvalidOperationException>();
  }

  #endregion

  #region OnMessageReceived with ObjectDisposedException Tests

  [Test]
  public async Task OnMessageReceived_WhenObjectDisposedExceptionDuringProcessing_DoesNotPropagateAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    await transport.SubscribeAsync(
      async (e, t, ct) => {
        await Task.CompletedTask;
        throw new ObjectDisposedException("channel");
      },
      _createDestinationWithSubscriber()
    );

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    // ObjectDisposedException from handler should be caught by _onMessageReceivedAsync
    await Assert.That(async () =>
      await _simulateMessageDeliveryAsync(channel, body, headers)
    ).ThrowsNothing();
  }

  #endregion

  #region HandleMessageFailure with ObjectDisposedException during Nack

  [Test]
  public async Task OnMessageReceived_WhenNackThrowsObjectDisposed_DoesNotPropagateAsync() {
    var channel = new FakeChannel();
    var options = new RabbitMQOptions { MaxDeliveryAttempts = 10 };
    var transport = await _createInitializedTransportAsync(channel: channel, options: options);

    await transport.SubscribeAsync(
      async (e, t, ct) => {
        await Task.CompletedTask;
        throw new InvalidOperationException("Handler failed");
      },
      _createDestinationWithSubscriber()
    );

    // Set channel to throw ObjectDisposedException on nack
    channel.ExceptionToThrowOnNack = new ObjectDisposedException("channel");

    var testEnvelope = _createTestEnvelope();
    var json = JsonSerializer.Serialize(testEnvelope, _jsonOptions);
    var body = Encoding.UTF8.GetBytes(json);
    var envelopeTypeName = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };

    // Should not throw - ObjectDisposedException during nack is caught
    await Assert.That(async () =>
      await _simulateMessageDeliveryAsync(channel, body, headers)
    ).ThrowsNothing();
  }

  #endregion

  #region Deserialization Failure Returns Null Envelope

  [Test]
  public async Task OnMessageReceived_WhenDeserializationReturnsNonEnvelope_NacksAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    await transport.SubscribeAsync(
      async (e, t, ct) => await Task.CompletedTask,
      _createDestinationWithSubscriber()
    );

    // Use TestMessage type (not IMessageEnvelope) - deserialization will return non-envelope
    var envelopeTypeName = typeof(TestMessage).AssemblyQualifiedName!;
    var headers = new Dictionary<string, object?> {
      ["EnvelopeType"] = Encoding.UTF8.GetBytes(envelopeTypeName)
    };
    var body = Encoding.UTF8.GetBytes("{\"content\": \"test\"}");

    await _simulateMessageDeliveryAsync(channel, body, headers, deliveryTag: 1);

    // Deserialization result is not IMessageEnvelope => nack without requeue
    await Assert.That(channel.BasicNackAsyncCalled).IsTrue();
    await Assert.That(channel.LastNackRequeue).IsFalse();
  }

  #endregion

  #region Publish with No Metadata Tests

  [Test]
  public async Task PublishAsync_WithNullMetadata_DoesNotThrowAsync() {
    var channel = new FakeChannel();
    var transport = await _createInitializedTransportAsync(channel: channel);

    var envelope = _createTestEnvelope();
    var destination = new TransportDestination("test-exchange"); // Null metadata

    await Assert.That(async () =>
      await transport.PublishAsync(envelope, destination)
    ).ThrowsNothing();

    await Assert.That(channel.BasicPublishAsyncCalled).IsTrue();
  }

  #endregion
}

#region Additional Test Doubles

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

/// <summary>
/// Fake channel that fails on the second BasicPublishAsync call.
/// Used to test partial batch failures.
/// </summary>
internal sealed class FakeChannelThatFailsOnSecondPublish : FakeChannel {
  private int _publishCount;

  public new ValueTask BasicPublishAsync<TProperties>(
    string exchange, string routingKey, bool mandatory, TProperties basicProperties,
    ReadOnlyMemory<byte> body = default, CancellationToken cancellationToken = default
  ) where TProperties : IReadOnlyBasicProperties, IAmqpHeader {
    _publishCount++;
    if (_publishCount >= 2) {
      throw new IOException("Network error on second publish");
    }
    // Call base to track the publish
    return base.BasicPublishAsync(exchange, routingKey, mandatory, basicProperties, body, cancellationToken);
  }
}

/// <summary>
/// Fake channel that throws a configurable exception on ExchangeDeclareAsync.
/// Used to test batch publish and subscribe error paths when initial setup fails.
/// </summary>
internal sealed class FakeChannelThatThrowsOnExchangeDeclare : FakeChannel {
  private readonly Exception _exception;

  public FakeChannelThatThrowsOnExchangeDeclare(Exception exception) {
    _exception = exception;
  }

  public new Task ExchangeDeclareAsync(
    string exchange, string type, bool durable, bool autoDelete,
    IDictionary<string, object?>? arguments, bool passive, bool noWait,
    CancellationToken cancellationToken = default
  ) {
    throw _exception;
  }
}

#pragma warning restore CS0067
#pragma warning restore CA1822

#endregion
