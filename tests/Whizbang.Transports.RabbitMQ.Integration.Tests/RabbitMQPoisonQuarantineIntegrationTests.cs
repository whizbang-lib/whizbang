using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Containers;
using Whizbang.Testing.Transport;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)
#pragma warning disable TUnit0023 // Disposable field disposed in the fire-and-forget cleanup, per this suite's idiom

namespace Whizbang.Transports.RabbitMQ.Integration.Tests;

/// <summary>
/// Topology arc phase 8.5 — broker-tier lock that RabbitMQ's poison quarantine really reaches the
/// dead-letter queue.
/// <para>
/// The decision is Core's, shared with Azure Service Bus; only the mechanism is RabbitMQ's —
/// <c>BasicNack(requeue: false)</c> onto the queue's dead-letter exchange. A unit test can prove
/// the nack flag; only a real broker proves the message actually LANDS in the DLQ the recovery
/// flow drains, rather than being dropped because no dead-letter binding exists.
/// </para>
/// Also locks the capability-honesty half: RabbitMQ's timestamp is publisher-set, so the transport
/// stamps its own publishes; a message without that stamp must NOT be quarantined and must NOT
/// silently stop enforcing.
/// </summary>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
public sealed class RabbitMQPoisonQuarantineIntegrationTests : IAsyncDisposable {
  private IConnection? _connection;
  private RabbitMQChannelPool? _channelPool;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedRabbitMqContainer.InitializeOrSkipAsync();
    var factory = new ConnectionFactory { Uri = new Uri(SharedRabbitMqContainer.ConnectionString) };
    _connection = await factory.CreateConnectionAsync();
    _channelPool = new RabbitMQChannelPool(_connection, maxChannels: 5);
  }

  [After(Test)]
  public Task CleanupAsync() {
    var channelPool = _channelPool;
    var connection = _connection;
    _channelPool = null;
    _connection = null;

    _ = Task.Run(async () => {
      try {
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

  [Test]
  [Timeout(90000)]
  public async Task Subscribe_AgedMessage_LandsInTheDeadLetterQueueAsync(CancellationToken cancellationToken) {
    // Arrange — a real exchange/queue pair with the transport's own dead-letter exchange wiring.
    // AgeThreshold = zero makes the just-published message aged; the derivation itself is
    // property-locked in Whizbang.Core.Tests and is not re-derived here.
    var exchange = $"poison-quarantine-{Guid.CreateVersion7():N}";
    var subscriber = $"sub-{Guid.CreateVersion7():N}";
    var transport = _buildTransport(_detector(TimeSpan.Zero));
    await using var __transport = transport;
    await transport.InitializeAsync(cancellationToken);

    var handlerInvoked = 0;
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => { Interlocked.Increment(ref handlerInvoked); return Task.CompletedTask; },
      _destination(exchange, subscriber),
      cancellationToken);

    try {
      // Act
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(
        envelope, new TransportDestination(exchange, "#"), cancellationToken: cancellationToken);

      // Assert — the message reaches the DLQ the transport declared for this queue. Draining the
      // DLQ IS the completion signal; there is no sleep.
      var deadLettered = await _drainDeadLetterQueueAsync(
        $"{subscriber}-{exchange}.dlq", envelope.MessageId.Value.ToString(), cancellationToken);

      await Assert.That(deadLettered).IsTrue()
        .Because("an aged message must be nacked without requeue onto the dead-letter exchange");
      await Assert.That(Volatile.Read(ref handlerInvoked)).IsEqualTo(0)
        .Because("quarantine happens at the receive boundary, before the handler");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task Subscribe_FreshMessage_IsDeliveredNotQuarantinedAsync(CancellationToken cancellationToken) {
    // The negative half: with a realistic threshold a fresh message flows through untouched.
    var exchange = $"poison-fresh-{Guid.CreateVersion7():N}";
    var subscriber = $"sub-{Guid.CreateVersion7():N}";
    var transport = _buildTransport(_detector(TimeSpan.FromHours(6)));
    await using var __transport = transport;
    await transport.InitializeAsync(cancellationToken);

    var awaiter = new MessageIdAwaiter();
    var subscription = await transport.SubscribeAsync(
      awaiter.Handler, _destination(exchange, subscriber), cancellationToken);

    try {
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(
        envelope, new TransportDestination(exchange, "#"), cancellationToken: cancellationToken);

      var receivedMessageId = await awaiter.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

      await Assert.That(receivedMessageId).IsNotNull()
        .Because("a fresh message must never be quarantined");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task Subscribe_MessageWithoutPublisherTimestamp_DegradesLoudlyAndDeliversAsync(
      CancellationToken cancellationToken) {
    // Capability honesty at broker tier. A foreign publisher (here: a raw BasicPublish with no
    // Timestamp) gives the transport no trustworthy age. Layer 1 must not invent one — an absent
    // stamp read as "infinitely old" would quarantine every foreign message on the queue — and it
    // must not go quietly inert: the surface is recorded degraded so health can show it.
    var exchange = $"poison-notime-{Guid.CreateVersion7():N}";
    var subscriber = $"sub-{Guid.CreateVersion7():N}";
    var capability = new PoisonDetectionCapabilityState();
    var transport = _buildTransport(_detector(TimeSpan.Zero, capability));
    await using var __transport = transport;
    await transport.InitializeAsync(cancellationToken);

    var awaiter = new MessageIdAwaiter();
    var subscription = await transport.SubscribeAsync(
      awaiter.Handler, _destination(exchange, subscriber), cancellationToken);

    try {
      var envelope = _createTestEnvelope();
      await _publishWithoutTimestampAsync(exchange, envelope, cancellationToken);

      var receivedMessageId = await awaiter.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

      await Assert.That(receivedMessageId).IsNotNull()
        .Because("no timestamp means layer 1 cannot judge age — it must not quarantine on a guess");
      await Assert.That(capability.HasDegradedSurface).IsTrue()
        .Because("the fallback to layer 2 must be visible, never silent");
      await Assert.That(capability.DegradedSurfaces[0].Transport).IsEqualTo("rabbitmq");
      await Assert.That(capability.DegradedSurfaces[0].Entity).IsEqualTo($"{subscriber}-{exchange}");
    } finally {
      subscription.Dispose();
    }
  }

  #region Helpers

  private RabbitMQTransport _buildTransport(IPoisonMessageDetector detector) =>
    new(_connection!, JsonContextRegistry.CreateCombinedOptions(), _channelPool!,
      new RabbitMQOptions(), logger: null, discardPolicy: null, poisonDetector: detector);

  private static PoisonMessageDetector _detector(
      TimeSpan ageThreshold, PoisonDetectionCapabilityState? capability = null) =>
    new(
      Microsoft.Extensions.Options.Options.Create(new PoisonMessageOptions { AgeThreshold = ageThreshold }),
      NullLogger<PoisonMessageDetector>.Instance,
      new System.Diagnostics.Metrics.Meter("Whizbang.Transports.RabbitMQ.Integration.Tests.Poison"),
      capability);

  private static TransportDestination _destination(string exchange, string subscriber) =>
    new(exchange, "#", new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse($"\"{subscriber}\"").RootElement.Clone()
    });

  /// <summary>
  /// Publishes the wire shape the transport produces but WITHOUT the phase-8.5 timestamp — a
  /// stand-in for any publisher that predates it or is not Whizbang.
  /// </summary>
  private async Task _publishWithoutTimestampAsync(
      string exchange, MessageEnvelope<TestMessage> envelope, CancellationToken cancellationToken) {
    var channel = await _connection!.CreateChannelAsync(cancellationToken: cancellationToken);
    await using (channel) {
      var aqn = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!;
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var typeInfo = JsonContextRegistry.GetTypeInfoByName(aqn, jsonOptions)!;
      var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, typeInfo));
      var properties = new BasicProperties {
        MessageId = envelope.MessageId.Value.ToString(),
        ContentType = "application/json",
        Persistent = true,
        Headers = new Dictionary<string, object?> { ["EnvelopeType"] = aqn },
      };
      await channel.BasicPublishAsync(
        exchange, "poison.probe", mandatory: false, basicProperties: properties, body: body,
        cancellationToken: cancellationToken);
    }
  }

  /// <summary>
  /// Polls the dead-letter queue with the broker's own blocking get until the expected message id
  /// appears or the attempt budget is spent. BasicGet is the completion signal — no Task.Delay.
  /// </summary>
  private async Task<bool> _drainDeadLetterQueueAsync(
      string dlqName, string expectedMessageId, CancellationToken cancellationToken) {
    var channel = await _connection!.CreateChannelAsync(cancellationToken: cancellationToken);
    await using (channel) {
      for (var attempt = 0; attempt < 60; attempt++) {
        var result = await channel.BasicGetAsync(dlqName, autoAck: true, cancellationToken);
        if (result is null) {
          await Task.Yield();
          continue;
        }
        if (result.BasicProperties.MessageId == expectedMessageId) {
          return true;
        }
      }
      return false;
    }
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = new TestMessage("poison-quarantine-content"),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        Topic = "test-topic",
        ServiceInstance = ServiceInstanceInfo.Unknown
      }
    ]
  };

  public async ValueTask DisposeAsync() {
    _channelPool?.Dispose();
    if (_connection != null) { await _connection.CloseAsync(); _connection.Dispose(); }
  }

  #endregion
}
