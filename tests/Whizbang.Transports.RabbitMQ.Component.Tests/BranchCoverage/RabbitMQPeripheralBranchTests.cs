// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CS0067 // Event is never used (test double)

namespace Whizbang.Transports.RabbitMQ.Tests.BranchCoverage;

/// <summary>
/// Branch coverage for the RabbitMQ package's smaller types: subscription lifecycle arms
/// (null reply text, no OnDisconnected subscriber, a logger with Debug disabled), the readiness
/// probe's close-reason fallback, the configuration binder with no configuration, the manifest
/// provisioner's owned-inbox marker and absent drift state, constructor guards, the dead-letter
/// drainer's logger fallback, the fleet drainer's deferred argument guards, and the hosting
/// registration's DLQ snapshot when the resolved transport is not RabbitMQ.
/// </summary>
public class RabbitMQPeripheralBranchTests {
  private const string QUEUE = "peripheral-queue";

  #region RabbitMQSubscription

  [Test]
  public async Task SubscriptionShutdown_NullReplyText_ReportsTheReplyCodeAsTheReasonAsync() {
    await using var channel = new FakeChannel();
    using var subscription = new RabbitMQSubscription(channel, QUEUE);
    var tcs = new TaskCompletionSource<SubscriptionDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    subscription.OnDisconnected += (_, args) => tcs.TrySetResult(args);

    await channel.SimulateShutdownAsync(ShutdownInitiator.Peer, null!);

    var received = await tcs.Task;
    await Assert.That(received.Reason).IsEqualTo("Code: 320")
      .Because("a broker shutdown without reply text must still tell the reconnect logic why");
  }

  [Test]
  public async Task SubscriptionShutdown_WithLogger_LogsTheShutdownAsync() {
    await using var channel = new FakeChannel();
    var logger = new CapturingLogger<RabbitMQSubscription>();
    using var subscription = new RabbitMQSubscription(channel, QUEUE, logger: logger);

    await channel.SimulateShutdownAsync(ShutdownInitiator.Peer, "connection lost");

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains(QUEUE, StringComparison.Ordinal)
      && e.Message.Contains("connection lost", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task SubscriptionShutdown_PeerWithNoDisconnectSubscriber_StillMarksInactiveAsync() {
    await using var channel = new FakeChannel();
    using var subscription = new RabbitMQSubscription(channel, QUEUE);

    await Assert.That(async () => await channel.SimulateShutdownAsync(ShutdownInitiator.Peer, "gone")).ThrowsNothing();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task SubscriptionPauseAndResume_RepeatedWithDebugDisabled_ShortCircuitWithoutLoggingAsync() {
    await using var channel = new FakeChannel();
    var logger = new DisabledLogger();
    using var subscription = new RabbitMQSubscription(channel, QUEUE, logger: logger);

    await subscription.PauseAsync();
    await subscription.PauseAsync();
    var pausedState = subscription.IsActive;
    await subscription.ResumeAsync();
    await subscription.ResumeAsync();

    await Assert.That(pausedState).IsFalse();
    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(logger.LogCalls).IsEqualTo(0)
      .Because("every log statement is guarded by IsEnabled, and this logger enables nothing");
  }

  #endregion

  #region RabbitMQReadinessCheck

  [Test]
  [Arguments("broker restart", "broker restart")]
  [Arguments(null, "unknown")]
  public async Task Readiness_ClosedWithCloseReason_LogsTheReplyTextOrUnknownAsync(string? replyText, string expected) {
    var connection = new ClosedConnection(new ShutdownEventArgs(ShutdownInitiator.Peer, 320, replyText!));
    var logger = new CapturingLogger<RabbitMQReadinessCheck>();
    var check = new RabbitMQReadinessCheck(connection, logger);

    var ready = await check.IsReadyAsync();

    await Assert.That(ready).IsFalse();
    await Assert.That(logger.Entries.Any(e =>
      e.Message.Contains("CloseReason: " + expected, StringComparison.Ordinal))).IsTrue();
  }

  #endregion

  #region RabbitMQOptionsConfigurationBinder

  [Test]
  public async Task Binder_NullConfiguration_ReturnsTheOptionsUntouchedAsync() {
    var options = new RabbitMQOptions { MaxDeliveryAttempts = 7 };

    var result = RabbitMQOptionsConfigurationBinder.Apply(options, null);

    await Assert.That(result).IsSameReferenceAs(options);
    await Assert.That(result.MaxDeliveryAttempts).IsEqualTo(7);
  }

  #endregion

  #region RabbitMQInfrastructureProvisioner

  [Test]
  public async Task Provisioner_OwnedInboxMarkerFalse_SkipsTheOwnershipProbeAsync() {
    const string topic = "inbox.myapp.orders.commands";
    var channel = new FakeChannel { ExistingExchanges = { topic } };
    var drift = new TopologyDriftState();
    var provisioner = _provisioner(channel, NullLogger<RabbitMQInfrastructureProvisioner>.Instance, drift);
    var manifest = new TopologyManifest(
      "order-service",
      [],
      [new InboxSubscription(topic, null, new Dictionary<string, object> {
        [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = false
      })]);

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(channel.PassiveExchangeDeclareCount).IsEqualTo(0)
      .Because("only a subscription marked owned (marker == true) is probed for ownership drift");
    await Assert.That(drift.HasDrift).IsFalse();
  }

  [Test]
  public async Task Provisioner_DriftWithoutDriftState_StillLogsTheOwnershipErrorAsync() {
    const string topic = "inbox.myapp.orders.commands";
    var channel = new FakeChannel { ExistingExchanges = { topic } };
    var logger = new CapturingLogger<RabbitMQInfrastructureProvisioner>();
    var provisioner = _provisioner(channel, logger, driftState: null);
    var manifest = new TopologyManifest(
      "order-service",
      [],
      [new InboxSubscription(topic, null, new Dictionary<string, object> {
        [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = true
      })]);

    await Assert.That(async () => await provisioner.ProvisionManifestAsync(manifest)).ThrowsNothing();

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Error && e.Message.Contains("Topology ownership drift", StringComparison.Ordinal))).IsTrue()
      .Because("with drift recording disabled the error log is the only signal, and it must still fire");
  }

  #endregion

  #region Constructor guards

  [Test]
  public async Task BacklogPeek_NullChannelPool_ThrowsNamingTheParameterAsync() {
    var ex = _catchArgumentNull(() => _ = new RabbitMQBacklogPeek(null!, () => []));

    await Assert.That(ex?.ParamName).IsEqualTo("channelPool");
  }

  [Test]
  public async Task BacklogPeek_NullQueueNames_ThrowsNamingTheParameterAsync() {
    var pool = new RabbitMQChannelPool(new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel())), 1);

    var ex = _catchArgumentNull(() => _ = new RabbitMQBacklogPeek(pool, null!));

    await Assert.That(ex?.ParamName).IsEqualTo("queueNames");
  }

  [Test]
  public async Task ChannelPool_NullConnection_ThrowsNamingTheParameterAsync() {
    var ex = _catchArgumentNull(() => _ = new RabbitMQChannelPool(null!, 1));

    await Assert.That(ex?.ParamName).IsEqualTo("connection");
  }

  #endregion

  #region Dead-letter drainers

  [Test]
  public async Task DeadLetterDrainer_NullLogger_FallsBackAndStillRequeuesAForeignMessageAsync() {
    var channel = new ForeignHeadChannel();
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var drainer = new RabbitMqDeadLetterDrainer(connection, "orders.dlq", (_, _) => Task.FromResult(true), null!);

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channel.Nacks.Single()).IsEqualTo((7UL, true))
      .Because("the skipped-message warning goes to the fallback logger, then the head is requeued");
  }

  [Test]
  public async Task FleetDrainer_NullConnectionFactory_ThrowsWhenAQueueIsFirstDrainedAsync() {
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      null!, () => ["a.dlq"], (_, _) => Task.FromResult(true), NullLoggerFactory.Instance);

    var ex = await _catchArgumentNullAsync(() => fleet.DrainDeadLetterQueueAsync(5));

    await Assert.That(ex?.ParamName).IsEqualTo("connectionFactory");
  }

  [Test]
  public async Task FleetDrainer_NullImport_ThrowsWhenAQueueIsFirstDrainedAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => connection, () => ["a.dlq"], null!, NullLoggerFactory.Instance);

    var ex = await _catchArgumentNullAsync(() => fleet.DrainDeadLetterQueueAsync(5));

    await Assert.That(ex?.ParamName).IsEqualTo("importAsync");
  }

  [Test]
  public async Task FleetDrainer_NullLoggerFactory_ThrowsWhenAQueueIsFirstDrainedAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()));
    var fleet = new RabbitMqFleetDeadLetterDrainer(
      () => connection, () => ["a.dlq"], (_, _) => Task.FromResult(true), null!);

    var ex = await _catchArgumentNullAsync(() => fleet.DrainDeadLetterQueueAsync(5));

    await Assert.That(ex?.ParamName).IsEqualTo("loggerFactory");
  }

  #endregion

  #region Hosting registration

  [Test]
  public async Task Registration_FleetDrainer_TransportIsNotRabbitMq_DrainsNothingAsync() {
    // A host can replace ITransport (a test double, a router of its own); the DLQ snapshot must
    // then be empty rather than casting and faulting the recurring drain worker.
    var channelsCreated = 0;
    var services = new ServiceCollection();
    services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
    services.AddSingleton<IConnection>(new FakeConnection(() => {
      channelsCreated++;
      return Task.FromResult<IChannel>(new FakeChannel());
    }));
    services.AddRabbitMQTransport("amqp://guest:guest@localhost:5672/");
    services.AddSingleton<ITransport>(new InProcessTransport());
    await using var provider = services.BuildServiceProvider();
    var drainer = provider.GetServices<ITransportDeadLetterDrainer>().Single(d => d.TransportName == "rmq");

    var drained = await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(channelsCreated).IsEqualTo(0)
      .Because("with no RabbitMQ transport there are no declared DLQs, so the broker is never touched");
  }

  #endregion

  #region Helpers

  private static RabbitMQInfrastructureProvisioner _provisioner(
      FakeChannel channel, ILogger<RabbitMQInfrastructureProvisioner> logger, TopologyDriftState? driftState) {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(channel));
    var pool = new RabbitMQChannelPool(connection, maxChannels: 10);
    return new RabbitMQInfrastructureProvisioner(pool, logger, options: null, driftState);
  }

  private static ArgumentNullException? _catchArgumentNull(Action action) {
    try {
      action();
    } catch (ArgumentNullException ex) {
      return ex;
    }
    return null;
  }

  private static async Task<ArgumentNullException?> _catchArgumentNullAsync(Func<Task> action) {
    try {
      await action();
    } catch (ArgumentNullException ex) {
      return ex;
    }
    return null;
  }

  /// <summary>Logger that enables no level and counts any Log call that slips past a guard.</summary>
  private sealed class DisabledLogger : ILogger {
    public int LogCalls { get; private set; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => LogCalls++;
  }

  /// <summary>Dead-letter channel whose head message is not a Whizbang wire message.</summary>
  private sealed class ForeignHeadChannel : FakeChannel, IChannel {
    private bool _served;

    public List<(ulong DeliveryTag, bool Requeue)> Nacks { get; } = [];

    public new Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck, CancellationToken cancellationToken = default) {
      if (_served) {
        return Task.FromResult<BasicGetResult?>(null);
      }
      _served = true;
      return Task.FromResult<BasicGetResult?>(new BasicGetResult(
        deliveryTag: 7,
        redelivered: false,
        exchange: "",
        routingKey: "rk",
        messageCount: 0,
        basicProperties: new BasicProperties { MessageId = "not-a-guid" },
        body: "foreign"u8.ToArray()));
    }

    public new ValueTask BasicNackAsync(ulong deliveryTag, bool multiple, bool requeue, CancellationToken cancellationToken = default) {
      Nacks.Add((deliveryTag, requeue));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>A closed connection that carries a broker close reason.</summary>
  private sealed class ClosedConnection(ShutdownEventArgs closeReason) : IConnection {
    public ushort ChannelMax => 0;
    public IDictionary<string, object?> ClientProperties => new Dictionary<string, object?>();
    public ShutdownEventArgs? CloseReason => closeReason;
    public AmqpTcpEndpoint Endpoint => throw new NotImplementedException();
    public uint FrameMax => 0;
    public TimeSpan Heartbeat => TimeSpan.Zero;
    public bool IsOpen => false;
    public IProtocol Protocol => throw new NotImplementedException();
    public IDictionary<string, object?>? ServerProperties => null;
    public IEnumerable<ShutdownReportEntry> ShutdownReport => [];
    public string? ClientProvidedName => null;
    public int LocalPort => 0;
    public int RemotePort => 0;

    public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;
    public event AsyncEventHandler<ConnectionBlockedEventArgs>? ConnectionBlockedAsync;
    public event AsyncEventHandler<ShutdownEventArgs>? ConnectionShutdownAsync;
    public event AsyncEventHandler<AsyncEventArgs>? ConnectionUnblockedAsync;
    public event AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>? ConsumerTagChangeAfterRecoveryAsync;
    public event AsyncEventHandler<QueueNameChangedAfterRecoveryEventArgs>? QueueNameChangedAfterRecoveryAsync;
    public event AsyncEventHandler<AsyncEventArgs>? RecoverySucceededAsync;
    public event AsyncEventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryErrorAsync;
    public event AsyncEventHandler<RecoveringConsumerEventArgs>? RecoveringConsumerAsync;

    public Task<IChannel> CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default) =>
      throw new NotImplementedException();

    public Task CloseAsync(ushort reasonCode, string reasonText, TimeSpan timeout, bool abort, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() { }

    public Task UpdateSecretAsync(string newSecret, string reason, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  #endregion
}
