// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Integration.Tests;

/// <summary>
/// #2421: a message dead-lettered from one subscriber's queue lands only in THAT subscriber's dead-letter queue, and so
/// only in its own dead-letter store. Every subscriber's queue on an exchange dead-letters through one direct exchange,
/// with its own dead-letter queue name as the routing key, and each dead-letter queue is bound only on that key. Before
/// #2421 the dead-letter exchange was a fanout every subscriber's DLQ was bound to, so one service's poison message was
/// copied into every other service's DLQ and imported into every other service's dead-letter store.
/// <para>
/// Where a dead-lettered message CAN go is a property of the bindings, which the management API reads back
/// consistently after the AMQP call returns; asserting the bindings is what makes the absence half deterministic,
/// rather than waiting a while to see that nothing arrived.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQEntityProvisioning.cs</code-under-test>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
public sealed class RabbitMQDeadLetterIsolationIntegrationTests {
  [Test]
  [Timeout(120000)]
  public async Task PoisonFromOneSubscriber_LandsOnlyInItsOwnDeadLetterQueueAndStoreAsync(
      CancellationToken cancellationToken) {
    await SharedRabbitMqContainer.InitializeOrSkipAsync(cancellationToken);

    // Arrange: two services subscribed to the same exchange. The owner quarantines every message, so it dead-letters
    // the poison; the bystander handles it normally.
    var ownerStore = new RecordingWorkCoordinator();
    var bystanderStore = new RecordingWorkCoordinator();
    await using var owner = _buildProvider(services => {
      services.AddSingleton<IWorkCoordinator>(ownerStore);
      services.AddSingleton<IPoisonMessageDetector>(new QuarantineEveryMessage());
    });
    await using var bystander = _buildProvider(services => services.AddSingleton<IWorkCoordinator>(bystanderStore));

    var exchange = $"dlq-isolation-{Guid.CreateVersion7():N}";
    var ownerSubscriber = $"owner-{Guid.CreateVersion7():N}";
    var bystanderSubscriber = $"bystander-{Guid.CreateVersion7():N}";
    var ownerDlq = $"{ownerSubscriber}-{exchange}.dlq";
    var bystanderDlq = $"{bystanderSubscriber}-{exchange}.dlq";
    var deadLetterExchange = $"{exchange}.dlx.direct";

    var ownerTransport = _transport(owner);
    var bystanderTransport = _transport(bystander);
    var ownerSubscription = await ownerTransport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, _destination(exchange, ownerSubscriber), cancellationToken);
    var bystanderSubscription = await bystanderTransport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, _destination(exchange, bystanderSubscriber), cancellationToken);
    try {
      // Act
      var envelope = _createTestEnvelope();
      var messageId = envelope.MessageId.Value.ToString();
      await ownerTransport.PublishAsync(
        envelope, new TransportDestination(exchange, "#"), cancellationToken: cancellationToken);
      await _awaitOnDeadLetterQueueAsync(owner, ownerDlq, messageId, cancellationToken);

      // Assert: each dead-letter queue is reachable only through its own key on the direct dead-letter exchange.
      await Assert.That(await RabbitMqManagementProbe.ExchangeTypeAsync(deadLetterExchange, cancellationToken))
        .IsEqualTo("direct")
        .Because("a direct dead-letter exchange routes each dead-lettered message by its queue's own key");
      await Assert.That(await RabbitMqManagementProbe.QueueBindingsAsync(bystanderDlq, cancellationToken))
        .IsEquivalentTo(new[] { (deadLetterExchange, bystanderDlq) })
        .Because("the bystander's DLQ must be bound only on its own key, so the owner's poison can never reach it");
      await Assert.That(await RabbitMqManagementProbe.QueueBindingsAsync(ownerDlq, cancellationToken))
        .IsEquivalentTo(new[] { (deadLetterExchange, ownerDlq) });

      // ...and so only the owner's dead-letter store takes custody of it.
      var ownerDrained = await owner.GetRequiredService<ITransportDeadLetterDrainer>()
        .DrainDeadLetterQueueAsync(10, cancellationToken);
      var bystanderDrained = await bystander.GetRequiredService<ITransportDeadLetterDrainer>()
        .DrainDeadLetterQueueAsync(10, cancellationToken);

      await Assert.That(ownerDrained).IsEqualTo(1);
      await Assert.That(ownerStore.Imports.Single().MessageId).IsEqualTo(envelope.MessageId.Value);
      await Assert.That(ownerStore.Imports.Single().Destination).IsEqualTo(ownerDlq);
      await Assert.That(bystanderDrained).IsEqualTo(0)
        .Because("the bystander handled the message; it is not the bystander's poison");
      await Assert.That(bystanderStore.Imports).IsEmpty();
    } finally {
      ownerSubscription.Dispose();
      bystanderSubscription.Dispose();
    }
  }

  // ========================================
  // HELPERS
  // ========================================

  private static ServiceProvider _buildProvider(Action<IServiceCollection> configure) {
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    services.AddRabbitMQTransport(SharedRabbitMqContainer.ConnectionString);
    configure(services);
    return services.BuildServiceProvider();
  }

  private static RabbitMQTransport _transport(IServiceProvider provider) =>
    provider.GetRequiredService<ITransport>() as RabbitMQTransport
      ?? throw new InvalidOperationException("ITransport did not resolve to RabbitMQTransport.");

  private static TransportDestination _destination(string exchange, string subscriber) =>
    new(exchange, "#", new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonElement.Parse($"\"{subscriber}\"")
    });

  /// <summary>
  /// Completes when <paramref name="expectedMessageId"/> is on the dead-letter queue and leaves it there: a consumer
  /// is the arrival signal, and the delivery is left unacked so the broker requeues it when the channel closes.
  /// </summary>
  private static async Task _awaitOnDeadLetterQueueAsync(
      IServiceProvider provider, string dlqName, string expectedMessageId, CancellationToken ct) {
    var connection = provider.GetRequiredService<IConnection>();
    var channel = await connection.CreateChannelAsync(cancellationToken: ct);
    await using (channel) {
      var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      var consumer = new AsyncEventingBasicConsumer(channel);
      consumer.ReceivedAsync += async (_, args) => {
        if (args.BasicProperties.MessageId == expectedMessageId) {
          arrived.TrySetResult(true);
          return;
        }
        await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true, ct);
      };
      var consumerTag = await channel.BasicConsumeAsync(dlqName, autoAck: false, consumer, ct);
      try {
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
      } finally {
        await channel.BasicCancelAsync(consumerTag, cancellationToken: ct);
      }
    }
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = new TestMessage("dlq-isolation"),
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

  /// <summary>Records every dead-letter import: the dead-letter store, as far as custody is concerned.</summary>
  private sealed class RecordingWorkCoordinator : IWorkCoordinator {
    private readonly List<BrokerDeadLetterImport> _imports = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<BrokerDeadLetterImport> Imports {
      get {
        lock (_lock) {
          return [.. _imports];
        }
      }
    }

    public Task<bool> ImportBrokerDeadLetterAsync(
        BrokerDeadLetterImport import, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _imports.Add(import);
      }
      return Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
