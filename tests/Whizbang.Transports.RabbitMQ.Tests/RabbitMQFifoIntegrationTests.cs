using System.Text.Json;
using System.Threading.Channels;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Containers;

#pragma warning disable CA1707 // Test method names use underscores by convention
#pragma warning disable CA1001 // Types that own disposable fields should be disposable
#pragma warning disable TUnit0023 // Disposable field should be disposed in cleanup method

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Integration tests proving FIFO ordering guarantees through RabbitMQ Single Active Consumer.
/// Uses a real RabbitMQ container. All tests use deterministic completion signals — no Task.Delay polling.
/// </summary>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
public class RabbitMQFifoIntegrationTests {
  private IConnection? _connection;
  private RabbitMQChannelPool? _channelPool;
  private RabbitMQTransport? _transport;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedRabbitMqContainer.InitializeOrSkipAsync();

    var factory = new ConnectionFactory {
      Uri = new Uri(SharedRabbitMqContainer.ConnectionString)
    };
    _connection = await factory.CreateConnectionAsync();
    _channelPool = new RabbitMQChannelPool(_connection, maxChannels: 5);

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new RabbitMQOptions {
      EnableSingleActiveConsumer = true
    };

    _transport = new RabbitMQTransport(
      _connection,
      jsonOptions,
      _channelPool,
      options,
      logger: null
    );

    await _transport.InitializeAsync();
  }

  [After(Test)]
  public Task CleanupAsync() {
    var transport = _transport;
    var channelPool = _channelPool;
    var connection = _connection;

    _transport = null;
    _channelPool = null;
    _connection = null;

    _ = Task.Run(async () => {
      try {
        if (transport != null) {
          await transport.DisposeAsync();
        }
        channelPool?.Dispose();
        if (connection != null) {
          await connection.CloseAsync();
          connection.Dispose();
        }
      } catch {
        // Ignore cleanup errors
      }
    }, CancellationToken.None);

    return Task.CompletedTask;
  }

  // ========================================
  // SAC FIFO ORDERING TESTS
  // ========================================

  [Test]
  [Timeout(90_000)]
  public async Task SAC_SinglePublish_50Messages_ReceivedInPublishOrderAsync(CancellationToken cancellationToken) {
    // Arrange
    var exchangeName = $"fifo-test-{Guid.NewGuid():N}";
    var destination = new TransportDestination(exchangeName, "#");
    var subscribeDestination = _createSubscribeDestination(exchangeName);

    var receivedChannel = Channel.CreateUnbounded<Guid>();
    var expectedIds = new List<Guid>(50);

    var subscription = await _transport!.SubscribeAsync(
      async (envelope, _, ct) => {
        await receivedChannel.Writer.WriteAsync(envelope.MessageId.Value, ct);
      },
      subscribeDestination,
      cancellationToken
    );

    try {
      // Brief delay for queue binding to propagate
      await Task.Delay(500, cancellationToken);

      // Act — publish 50 messages
      for (var i = 0; i < 50; i++) {
        var envelope = _createTestEnvelope($"sac-msg-{i}");
        expectedIds.Add(envelope.MessageId.Value);
        await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);
      }

      // Assert — read back in order
      var receivedIds = new List<Guid>(50);
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      cts.CancelAfter(TimeSpan.FromSeconds(30));

      for (var i = 0; i < 50; i++) {
        var id = await receivedChannel.Reader.ReadAsync(cts.Token);
        receivedIds.Add(id);
      }

      for (var i = 0; i < 50; i++) {
        await Assert.That(receivedIds[i]).IsEqualTo(expectedIds[i])
          .Because($"SAC: message at position {i} should match publish order");
      }
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90_000)]
  public async Task SAC_BulkPublish_50Messages_ReceivedInPublishOrderAsync(CancellationToken cancellationToken) {
    // Arrange
    var exchangeName = $"fifo-bulk-{Guid.NewGuid():N}";
    var destination = new TransportDestination(exchangeName, "#");
    var subscribeDestination = _createSubscribeDestination(exchangeName);

    var receivedChannel = Channel.CreateUnbounded<Guid>();

    var subscription = await _transport!.SubscribeAsync(
      async (envelope, _, ct) => {
        await receivedChannel.Writer.WriteAsync(envelope.MessageId.Value, ct);
      },
      subscribeDestination,
      cancellationToken
    );

    try {
      await Task.Delay(500, cancellationToken);

      // Build batch items
      var bulkItems = new List<BulkPublishItem>(50);
      var expectedIds = new List<Guid>(50);

      for (var i = 0; i < 50; i++) {
        var envelope = _createTestEnvelope($"sac-bulk-{i}");
        expectedIds.Add(envelope.MessageId.Value);
        bulkItems.Add(new BulkPublishItem {
          Envelope = envelope,
          EnvelopeType = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName,
          MessageId = envelope.MessageId.Value,
          RoutingKey = "#",
          StreamId = Guid.CreateVersion7()
        });
      }

      // Act
      var results = await _transport.PublishBatchAsync(bulkItems, destination, cancellationToken);

      foreach (var result in results) {
        await Assert.That(result.Success).IsTrue();
      }

      // Assert
      var receivedIds = new List<Guid>(50);
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      cts.CancelAfter(TimeSpan.FromSeconds(30));

      for (var i = 0; i < 50; i++) {
        var id = await receivedChannel.Reader.ReadAsync(cts.Token);
        receivedIds.Add(id);
      }

      for (var i = 0; i < 50; i++) {
        await Assert.That(receivedIds[i]).IsEqualTo(expectedIds[i])
          .Because($"SAC bulk: message at position {i} should match publish order");
      }
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // CAPABILITIES TESTS
  // ========================================

  [Test]
  [Timeout(10_000)]
  public async Task SAC_Capabilities_IncludesOrderedAsync(CancellationToken cancellationToken) {
    _ = cancellationToken; // Satisfy TUnit0015
    await Assert.That((_transport!.Capabilities & TransportCapabilities.Ordered) != 0).IsTrue()
      .Because("SAC-enabled transport should claim Ordered capability");
  }

  [Test]
  [Timeout(10_000)]
  public async Task NonSAC_Capabilities_ExcludesOrderedAsync(CancellationToken cancellationToken) {
    _ = cancellationToken;
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var options = new RabbitMQOptions { EnableSingleActiveConsumer = false };
    var nonSacTransport = new RabbitMQTransport(
      _connection!,
      jsonOptions,
      _channelPool!,
      options,
      logger: null
    );

    await Assert.That((nonSacTransport.Capabilities & TransportCapabilities.Ordered) != 0).IsFalse()
      .Because("Non-SAC transport should NOT claim Ordered capability");
  }

  [Test]
  [Timeout(10_000)]
  public async Task EnableSingleActiveConsumer_DefaultsToFalseAsync(CancellationToken cancellationToken) {
    _ = cancellationToken;
    var options = new RabbitMQOptions();
    await Assert.That(options.EnableSingleActiveConsumer).IsFalse()
      .Because("SAC must be opt-in for backward compatibility");
  }

  // ========================================
  // HELPERS
  // ========================================

  private static TransportDestination _createSubscribeDestination(string exchangeName) {
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse($"\"fifo-sub-{Guid.NewGuid():N}\"").RootElement.Clone()
    };
    return new TransportDestination(exchangeName, "#", metadata);
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope(string content = "test") {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(content),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "fifo-test",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }
}
