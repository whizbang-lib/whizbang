// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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
using Whizbang.Testing.Transport;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Integration.Tests;

/// <summary>
/// #2421 upgrade path, on a real broker. A queue an earlier version declared dead-letters into the fanout
/// <c>{exchange}.dlx</c> with no routing key, and RabbitMQ refuses to change a queue's arguments in place
/// (PRECONDITION_FAILED). Such a queue is deleted and re-declared only when it is empty and has no consumers; one that
/// holds messages, or that a running instance still consumes from, is refused by name so a deploy never drops a
/// message. The earlier fanout is retired once no dead-letter queue is bound to it, so services still on the earlier
/// version keep their dead-letter route until they upgrade.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQEntityProvisioning.cs</code-under-test>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The [After(Test)] hook disposes them, this suite's one teardown.")]
public sealed class RabbitMQDeadLetterUpgradeIntegrationTests {
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
  public async Task CleanupAsync() {
    _channelPool?.Dispose();
    if (_connection is not null) {
      await _connection.CloseAsync();
      _connection.Dispose();
    }
    _channelPool = null;
    _connection = null;
  }

  [Test]
  [Timeout(90000)]
  public async Task EmptyQueueFromEarlierVersion_IsRedeclaredWithItsOwnDeadLetterRouteAsync(CancellationToken ct) {
    var (exchange, subscriber, queue, dlq) = _names("upgrade-empty");
    await _declareEarlierVersionTopologyAsync(exchange, queue, dlq, ct);

    await using var transport = await _transportAsync(ct);
    var envelope = _envelope();
    var awaiter = new MessageIdAwaiter(envelope.MessageId.ToString());
    var subscription = await transport.SubscribeAsync(awaiter.Handler, _destination(exchange, subscriber), ct);
    try {
      var arguments = await RabbitMqManagementProbe.QueueArgumentsAsync(queue, ct);
      await Assert.That(arguments!["x-dead-letter-exchange"]).IsEqualTo($"{exchange}.dlx.direct");
      await Assert.That(arguments["x-dead-letter-routing-key"]).IsEqualTo(dlq)
        .Because("the empty queue was deleted and re-declared with the per-queue dead-letter route");
      await Assert.That(await RabbitMqManagementProbe.QueueBindingsAsync(dlq, ct))
        .IsEquivalentTo([($"{exchange}.dlx.direct", dlq)])
        .Because("the DLQ leaves the earlier fanout, or other subscribers' poison would still be copied into it");
      await Assert.That(await RabbitMqManagementProbe.ExchangeTypeAsync($"{exchange}.dlx", ct)).IsNull()
        .Because("with no dead-letter queue left bound to it, the earlier fanout is retired");

      // The subscription consumes: the re-declare happened on channels of its own, not on a closed one.
      await transport.PublishAsync(envelope, new TransportDestination(exchange, "#"), cancellationToken: ct);
      await Assert.That(await awaiter.WaitAsync(TimeSpan.FromSeconds(30), ct)).IsEqualTo(envelope.MessageId.ToString());
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task QueueFromEarlierVersionHoldingMessages_IsRefusedByNameAndKeepsThemAsync(CancellationToken ct) {
    var (exchange, subscriber, queue, dlq) = _names("upgrade-held");
    await _declareEarlierVersionTopologyAsync(exchange, queue, dlq, ct);
    await _publishConfirmedToQueueAsync(queue, ct);

    await using var transport = await _transportAsync(ct);
    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination(exchange, subscriber), ct));

    var refusal = thrown!.InnerException!.Message;
    await Assert.That(refusal).Contains($"'{queue}'").And.Contains("1 ready message")
      .Because("the operator must be told WHICH queue blocks the upgrade, and why");

    await Assert.That(await _depthAsync(queue, ct)).IsEqualTo(1u)
      .Because("a refused upgrade must never drop the queue's messages");
    var arguments = await RabbitMqManagementProbe.QueueArgumentsAsync(queue, ct);
    await Assert.That(arguments!["x-dead-letter-exchange"]).IsEqualTo($"{exchange}.dlx");
    await Assert.That(arguments.ContainsKey("x-dead-letter-routing-key")).IsFalse();
    await Assert.That(await RabbitMqManagementProbe.QueueBindingsAsync(dlq, ct))
      .Contains(($"{exchange}.dlx", ""))
      .Because("the refused queue still dead-letters into the earlier fanout, so its DLQ must stay bound there");
  }

  [Test]
  [Timeout(90000)]
  public async Task QueueFromEarlierVersionWithAConsumer_IsRefusedByNameAsync(CancellationToken ct) {
    var (exchange, subscriber, queue, dlq) = _names("upgrade-consumed");
    await _declareEarlierVersionTopologyAsync(exchange, queue, dlq, ct);

    // An instance of the earlier version, still consuming: its unacknowledged deliveries would be lost with the queue.
    var earlier = await _connection!.CreateChannelAsync(cancellationToken: ct);
    await using (earlier) {
      await earlier.BasicConsumeAsync(queue, autoAck: false, new AsyncEventingBasicConsumer(earlier), ct);

      await using var transport = await _transportAsync(ct);
      var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination(exchange, subscriber), ct));

      await Assert.That(thrown!.InnerException!.Message).Contains($"'{queue}'").And.Contains("1 consumer");
      var arguments = await RabbitMqManagementProbe.QueueArgumentsAsync(queue, ct);
      await Assert.That(arguments!["x-dead-letter-exchange"]).IsEqualTo($"{exchange}.dlx")
        .Because("a queue still being consumed is left exactly as it was");
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task EarlierFanout_StaysWhileAnotherServicesDeadLetterQueueIsStillBoundAsync(CancellationToken ct) {
    // A rolling upgrade: this service upgrades while another service on the same exchange is still on the earlier
    // version. The other service's queue dead-letters into the fanout, so the fanout and its binding must stay.
    var (exchange, subscriber, queue, dlq) = _names("upgrade-rolling");
    var otherQueue = $"other-{Guid.CreateVersion7():N}-{exchange}";
    var otherDlq = $"{otherQueue}.dlq";
    await _declareEarlierVersionTopologyAsync(exchange, queue, dlq, ct);
    await _declareEarlierVersionTopologyAsync(exchange, otherQueue, otherDlq, ct);

    await using var transport = await _transportAsync(ct);
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, _destination(exchange, subscriber), ct);
    try {
      await Assert.That(await RabbitMqManagementProbe.ExchangeTypeAsync($"{exchange}.dlx", ct)).IsEqualTo("fanout")
        .Because("another service's DLQ is still bound to the earlier fanout");
      await Assert.That(await RabbitMqManagementProbe.QueueBindingsAsync(otherDlq, ct))
        .IsEquivalentTo([($"{exchange}.dlx", "")]);
      await Assert.That(await RabbitMqManagementProbe.QueueBindingsAsync(dlq, ct))
        .IsEquivalentTo([($"{exchange}.dlx.direct", dlq)])
        .Because("the upgraded service's DLQ no longer receives copies of the other service's poison");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(90000)]
  public async Task EmptyQueueWithOtherArgumentsAndNoEarlierFanout_IsRedeclaredAsync(CancellationToken ct) {
    // A queue whose arguments differ for another reason (declared with none), with no earlier fanout on the broker:
    // retiring a fanout that does not exist must not fail the subscribe.
    var (exchange, subscriber, queue, dlq) = _names("upgrade-noargs");
    var channel = await _connection!.CreateChannelAsync(cancellationToken: ct);
    await using (channel) {
      await channel.ExchangeDeclareAsync(exchange, "topic", durable: true, autoDelete: false, cancellationToken: ct);
      await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
    }

    await using var transport = await _transportAsync(ct);
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, _destination(exchange, subscriber), ct);
    try {
      var arguments = await RabbitMqManagementProbe.QueueArgumentsAsync(queue, ct);
      await Assert.That(arguments!["x-dead-letter-routing-key"]).IsEqualTo(dlq);
      await Assert.That(await RabbitMqManagementProbe.ExchangeTypeAsync($"{exchange}.dlx", ct)).IsNull();
    } finally {
      subscription.Dispose();
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private static (string Exchange, string Subscriber, string Queue, string Dlq) _names(string prefix) {
    var exchange = $"{prefix}-{Guid.CreateVersion7():N}";
    var subscriber = $"sub-{Guid.CreateVersion7():N}";
    var queue = $"{subscriber}-{exchange}";
    return (exchange, subscriber, queue, $"{queue}.dlq");
  }

  /// <summary>The topology an earlier version declared: a fanout dead-letter exchange and a queue without a routing key.</summary>
  private async Task _declareEarlierVersionTopologyAsync(string exchange, string queue, string dlq, CancellationToken ct) {
    var channel = await _connection!.CreateChannelAsync(cancellationToken: ct);
    await using (channel) {
      await channel.ExchangeDeclareAsync(exchange, "topic", durable: true, autoDelete: false, cancellationToken: ct);
      await channel.ExchangeDeclareAsync($"{exchange}.dlx", "fanout", durable: true, autoDelete: false, cancellationToken: ct);
      await channel.QueueDeclareAsync(dlq, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
      await channel.QueueBindAsync(dlq, $"{exchange}.dlx", "", cancellationToken: ct);
      await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
        arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = $"{exchange}.dlx" }, cancellationToken: ct);
      await channel.QueueBindAsync(queue, exchange, "#", cancellationToken: ct);
    }
  }

  /// <summary>Publishes straight to <paramref name="queue"/> and returns once the broker has confirmed it.</summary>
  private async Task _publishConfirmedToQueueAsync(string queue, CancellationToken ct) {
    var channel = await _connection!.CreateChannelAsync(
      new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
    await using (channel) {
      await channel.BasicPublishAsync("", queue, mandatory: true, new BasicProperties { Persistent = true },
        "held"u8.ToArray(), ct);
    }
  }

  private async Task<uint> _depthAsync(string queue, CancellationToken ct) {
    var channel = await _connection!.CreateChannelAsync(cancellationToken: ct);
    await using (channel) {
      return (await channel.QueueDeclarePassiveAsync(queue, ct)).MessageCount;
    }
  }

  private async Task<RabbitMQTransport> _transportAsync(CancellationToken ct) {
    var transport = new RabbitMQTransport(
      _connection!, JsonContextRegistry.CreateCombinedOptions(), _channelPool!, new RabbitMQOptions());
    await transport.InitializeAsync(ct);
    return transport;
  }

  private static TransportDestination _destination(string exchange, string subscriber) =>
    new(exchange, "#", new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonElement.Parse($"\"{subscriber}\"")
    });

  private static MessageEnvelope<TestMessage> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = new TestMessage("dlq-upgrade"),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Topic = "test-topic",
        ServiceInstance = ServiceInstanceInfo.Unknown
      }
    ]
  };
}
