using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Testing.Containers;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Integration.Tests;

/// <summary>
/// <para>Covers the two pieces of <c>AddRabbitMQTransport</c> that only a live broker can
/// execute, because both are DI factories that unit tests deliberately never let run:</para>
/// <list type="bullet">
///   <item>The <see cref="IConnection"/> factory. Every unit test pre-registers a fake
///   connection — which is what makes them hermetic, and also what leaves the real factory
///   (retry wrapper, ConnectionFactory settings, recovery-event wire-up) unexecuted.</item>
///   <item>The broker DLQ import seam handed to the fleet drainer. It resolves
///   <see cref="IWorkCoordinator"/> from a fresh scope per call, so it runs only when a real
///   dead-lettered message is actually imported.</item>
/// </list>
/// <para>Both arms of the seam matter and they are opposites: with a coordinator, custody
/// transfers and the broker copy is acked; without one the seam THROWS, so the message is
/// requeued and stays on the broker DLQ rather than being acked away into nothing.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMqDeadLetterDrainer.cs</code-under-test>
[Category("Integration")]
[NotInParallel("RabbitMQ")]
public sealed class RabbitMqHostRegistrationIntegrationTests {

  [Test]
  [Timeout(90000)]
  public async Task AddRabbitMQTransport_WithNoPreRegisteredConnection_OpensARealBrokerConnectionAsync(
      CancellationToken cancellationToken) {
    await SharedRabbitMqContainer.InitializeOrSkipAsync(cancellationToken);

    // Arrange + Act — nothing pre-registers IConnection, so resolving it runs the registration's
    // own factory: the retry wrapper, the ConnectionFactory this library configures, and the
    // recovery-event wire-up.
    await using var provider = _buildProvider(_ => { });
    var connection = provider.GetRequiredService<IConnection>();

    // Assert — open, and genuinely usable rather than merely constructed
    await Assert.That(connection.IsOpen).IsTrue()
      .Because("the registration's contract is that a host which resolves IConnection gets a "
             + "connected broker, not an object that fails on first use");

    var pool = provider.GetRequiredService<RabbitMQChannelPool>();
    using var pooled = await pool.RentAsync(cancellationToken);
    var queue = $"registration-probe-{Guid.CreateVersion7():N}";
    await pooled.Channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true,
      cancellationToken: cancellationToken);
    await Assert.That((await pooled.Channel.QueueDeclarePassiveAsync(queue, cancellationToken)).MessageCount)
      .IsEqualTo(0u)
      .Because("a channel from the registered pool must be able to talk to the broker");
  }

  [Test]
  [Timeout(120000)]
  public async Task DrainDeadLetterQueue_WithACoordinatorRegistered_ImportsThroughTheSeamAndAcksTheBrokerCopyAsync(
      CancellationToken cancellationToken) {
    await SharedRabbitMqContainer.InitializeOrSkipAsync(cancellationToken);

    // Arrange — a host container: the registration under test plus a coordinator that can take
    // custody. The quarantine detector is what puts a REAL dead-letter on the DLQ, x-death header
    // and all, rather than a message this test published there by hand.
    var coordinator = new RecordingWorkCoordinator();
    await using var provider = _buildProvider(services => {
      services.AddSingleton<IWorkCoordinator>(coordinator);
      services.AddSingleton<IPoisonMessageDetector>(_quarantineEverything());
    });

    var exchange = $"dlq-import-{Guid.CreateVersion7():N}";
    var subscriber = $"sub-{Guid.CreateVersion7():N}";
    var dlq = $"{subscriber}-{exchange}.dlq";

    var transport = _asRabbitMqTransport(provider);
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, _destination(exchange, subscriber), cancellationToken);
    try {
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(
        envelope, new TransportDestination(exchange, "#"), cancellationToken: cancellationToken);

      var messageId = envelope.MessageId.Value.ToString();
      await _awaitOnDeadLetterQueueAsync(provider, dlq, messageId, cancellationToken);

      // Act
      var drainer = provider.GetRequiredService<ITransportDeadLetterDrainer>();
      var drained = await drainer.DrainDeadLetterQueueAsync(10, cancellationToken);

      // Assert — the registered seam ran, with the broker's own x-death metadata parsed off a
      // real header rather than one a unit test hand-assembled
      await Assert.That(drained).IsEqualTo(1);
      await Assert.That(coordinator.Imports.Count).IsEqualTo(1);

      var import = coordinator.Imports[0];
      await Assert.That(import.MessageId).IsEqualTo(envelope.MessageId.Value);
      await Assert.That(import.Destination).IsEqualTo(dlq);
      await Assert.That(import.BrokerReason).IsEqualTo("rejected")
        .Because("quarantine nacks without requeue, and RabbitMQ records that as the x-death "
               + "reason the custody row must preserve");
      await Assert.That(import.DeliveryCount).IsEqualTo(1)
        .Because("x-death carries the death count, and losing it would hide how many times a "
               + "message has already been through this");

      await Assert.That(await _queueDepthAsync(provider, dlq, cancellationToken)).IsEqualTo(0u)
        .Because("custody succeeded, so the broker copy is acked — leaving it is how a DLQ grows "
               + "without bound");
    } finally {
      subscription.Dispose();
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task DrainDeadLetterQueue_WithNoCoordinatorRegistered_LeavesTheMessageOnTheBrokerDlqAsync(
      CancellationToken cancellationToken) {
    await SharedRabbitMqContainer.InitializeOrSkipAsync(cancellationToken);

    // The same registration, minus the coordinator — a transport-only host, or one whose data
    // package failed to register.
    await using var provider = _buildProvider(services =>
      services.AddSingleton<IPoisonMessageDetector>(_quarantineEverything()));

    var exchange = $"dlq-nocustody-{Guid.CreateVersion7():N}";
    var subscriber = $"sub-{Guid.CreateVersion7():N}";
    var dlq = $"{subscriber}-{exchange}.dlq";

    var transport = _asRabbitMqTransport(provider);
    var subscription = await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask, _destination(exchange, subscriber), cancellationToken);
    try {
      var envelope = _createTestEnvelope();
      await transport.PublishAsync(
        envelope, new TransportDestination(exchange, "#"), cancellationToken: cancellationToken);
      await _awaitOnDeadLetterQueueAsync(
        provider, dlq, envelope.MessageId.Value.ToString(), cancellationToken);

      // Act
      var drainer = provider.GetRequiredService<ITransportDeadLetterDrainer>();
      var drained = await drainer.DrainDeadLetterQueueAsync(10, cancellationToken);

      // Assert — nothing drained, and nothing LOST
      await Assert.That(drained).IsEqualTo(0)
        .Because("a message that never reached custody was not drained, and counting it would "
               + "hide the misconfiguration from the drain worker's metrics");
      await Assert.That(await _queueDepthAsync(provider, dlq, cancellationToken)).IsEqualTo(1u)
        .Because("with no coordinator the seam throws, the drainer requeues, and the broker DLQ "
               + "stays the only custody there is — acking here would destroy the message");
    } finally {
      subscription.Dispose();
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

  /// <summary>
  /// The cast the registered fleet drainer itself makes to reach ActiveDeadLetterQueues: with a
  /// single namespace the router short-circuits to the transport. If that stops holding, the
  /// drainer's queue snapshot comes back empty and it silently drains nothing.
  /// </summary>
  private static RabbitMQTransport _asRabbitMqTransport(IServiceProvider provider) =>
    provider.GetRequiredService<ITransport>() as RabbitMQTransport
      ?? throw new InvalidOperationException(
        "ITransport did not resolve to RabbitMQTransport — the fleet drainer's dead-letter queue "
        + "snapshot depends on exactly this cast and would come back empty.");

  /// <summary>
  /// A detector with a zero age threshold: every message is quarantined at the receive boundary
  /// and nacked without requeue onto the transport's dead-letter exchange. The derivation itself
  /// is property-locked in Whizbang.Core.Tests and is not re-derived here.
  /// </summary>
  private static PoisonMessageDetector _quarantineEverything() =>
    new(
      Microsoft.Extensions.Options.Options.Create(new PoisonMessageOptions { AgeThreshold = TimeSpan.Zero }),
      NullLogger<PoisonMessageDetector>.Instance,
      new System.Diagnostics.Metrics.Meter("Whizbang.Transports.RabbitMQ.Integration.Tests.HostRegistration"));

  private static TransportDestination _destination(string exchange, string subscriber) =>
    new(exchange, "#", new Dictionary<string, JsonElement> {
      ["SubscriberName"] = JsonDocument.Parse($"\"{subscriber}\"").RootElement.Clone()
    });

  /// <summary>
  /// Completes when <paramref name="expectedMessageId"/> is sitting on the dead-letter queue, and
  /// leaves it there for the drainer under test: a consumer is the arrival signal (no polling),
  /// and the expected delivery is left unacked so the broker requeues it when the channel closes.
  /// Nacking it back with requeue instead sets up a hot redeliver/nack loop with the broker, and
  /// an in-flight nack racing the channel close is a wire error (504 CHANNEL_ERROR,
  /// "expected 'channel.open'") that failed this suite intermittently.
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
          // Deliberately unacked: the channel close below requeues it, and completing the signal
          // is the last thing this consumer ever does on the channel — nothing is left in flight
          // to race the close.
          arrived.TrySetResult(true);
          return;
        }
        // Anything else on this uniquely-named queue is unexpected; put it back rather than
        // consume it out from under the drain pass.
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

  private static async Task<uint> _queueDepthAsync(
      IServiceProvider provider, string queueName, CancellationToken ct) {
    var connection = provider.GetRequiredService<IConnection>();
    var channel = await connection.CreateChannelAsync(cancellationToken: ct);
    await using (channel) {
      return (await channel.QueueDeclarePassiveAsync(queueName, ct)).MessageCount;
    }
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = new TestMessage("dlq-import-seam"),
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

  /// <summary>
  /// Records every import the seam performs. Every other coordinator operation is the interface
  /// default — this exists to observe custody transfer, not to store anything.
  /// </summary>
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

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) =>
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
