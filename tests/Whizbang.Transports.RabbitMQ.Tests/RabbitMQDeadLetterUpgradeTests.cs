// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// #2421 upgrade of a queue an earlier version declared with other arguments, decision by decision. The broker refuses
/// the declare with PRECONDITION_FAILED and closes the channel; the queue is deleted and declared again only when it is
/// empty and unconsumed, and the work continues on a fresh channel. The real-broker behavior is locked in
/// <c>RabbitMQDeadLetterUpgradeIntegrationTests</c>; these tests drive every branch, including the races and broker
/// faults a real broker cannot be made to produce on demand.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQEntityProvisioning.cs</code-under-test>
public class RabbitMQDeadLetterUpgradeTests {
  private const string EXCHANGE = "orders";
  private const string QUEUE = "svc-orders";
  private const string DLQ = "svc-orders.dlq";

  [Test]
  public async Task Upgrade_EmptyUnconsumedQueue_IsDeclaredAgainAndTheWorkContinuesOnAFreshChannelAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE };
    var main = script.NewChannel();
    var logger = new CapturingLogger<RabbitMQDeadLetterUpgradeTests>();

    var live = await _declareAsync(script, main, logger: logger);

    await Assert.That(live).IsNotSameReferenceAs(main)
      .Because("the broker closed the channel it refused the declare on");
    var (probe, redeclared, retire) = (script.Channels[1], script.Channels[2], script.Channels[3]);
    await Assert.That(live).IsSameReferenceAs(redeclared);
    await Assert.That(probe.QueueDeletes).IsEquivalentTo([(QUEUE, true, true)])
      .Because("the delete repeats both conditions, so a message or consumer arriving after the check refuses it");
    var arguments = redeclared.QueueDeclareArgumentsByQueue[QUEUE]!;
    await Assert.That((string)arguments["x-dead-letter-routing-key"]!).IsEqualTo(DLQ);
    await Assert.That(redeclared.QueueBindings).Contains((QUEUE, EXCHANGE, "#"))
      .Because("the bindings follow on the channel the queue was declared again on");
    await Assert.That(retire.Unbinds).IsEquivalentTo([(DLQ, "orders.dlx", "")]);
    await Assert.That(retire.ExchangeDeletes).IsEquivalentTo([("orders.dlx", true)])
      .Because("the earlier fanout is deleted only if no other DLQ is still bound to it");
    await Assert.That(probe.IsDisposed).IsTrue();
    await Assert.That(retire.IsDisposed).IsTrue();
    await Assert.That(live.IsOpen).IsTrue().Because("the caller now owns the fresh channel");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(QUEUE, StringComparison.Ordinal))).IsTrue();
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("Deleted the earlier fanout", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Upgrade_WithoutALogger_StillUpgradesAndRetiresTheFanoutAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE };

    var live = await _declareAsync(script, script.NewChannel(), logger: null);

    await Assert.That(live).IsSameReferenceAs(script.Channels[2]);
    await Assert.That(script.Channels[3].ExchangeDeletes).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Upgrade_WithDeadLetteringOff_LeavesAnyFanoutAloneAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE };

    var live = await _declareAsync(
      script, script.NewChannel(), new RabbitMQOptions { AutoDeclareDeadLetterExchange = false });

    await Assert.That(live).IsSameReferenceAs(script.Channels[2]);
    await Assert.That(script.Channels).Count().IsEqualTo(3)
      .Because("with no DLQ of its own, the queue has no binding on an earlier fanout to move");
  }

  [Test]
  public async Task Upgrade_NoEarlierFanoutOnTheBroker_IsNotAnErrorAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, UnbindFailure = _closed(Constants.NotFound) };
    var logger = new CapturingLogger<RabbitMQDeadLetterUpgradeTests>();

    var live = await _declareAsync(script, script.NewChannel(), logger: logger);

    await Assert.That(live.IsOpen).IsTrue();
    await Assert.That(script.Channels[3].ExchangeDeletes).IsEmpty();
    await Assert.That(logger.EntriesWithExceptions.Any(e =>
      e.Message.Contains("Kept the earlier fanout", StringComparison.Ordinal) && e.Exception is OperationInterruptedException)).IsTrue();
  }

  [Test]
  public async Task Upgrade_AnotherDeadLetterQueueStillOnTheFanout_KeepsItAsync() {
    // A service still on the earlier version keeps its DLQ bound to the fanout: the if-unused delete is refused.
    var script = new BrokerScript {
      RefuseFirstDeclareOf = QUEUE,
      ExchangeDeleteFailure = _closed(Constants.PreconditionFailed),
    };

    var live = await _declareAsync(script, script.NewChannel(), logger: null);

    await Assert.That(live.IsOpen).IsTrue();
    await Assert.That(script.Channels[3].Unbinds).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Upgrade_UnexpectedFaultRetiringTheFanout_FailsAndReleasesTheFreshChannelAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, UnbindFailure = _closed(Constants.AccessRefused) };

    await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
      await _declareAsync(script, script.NewChannel()));

    await Assert.That(script.Channels[2].IsDisposed).IsTrue();
  }

  [Test]
  public async Task Upgrade_QueueHoldingMessages_IsRefusedByNameAndNotDeletedAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, Messages = 1 };

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await _declareAsync(script, script.NewChannel()));

    await Assert.That(thrown!.Message).Contains($"'{QUEUE}'").And.Contains("1 ready message(s) and has 0 consumer(s)");
    await Assert.That(script.Channels[1].QueueDeletes).IsEmpty();
    await Assert.That(script.Channels[1].IsDisposed).IsTrue();
    await Assert.That(script.Channels).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Upgrade_QueueWithAConsumer_IsRefusedByNameAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, Consumers = 1 };

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await _declareAsync(script, script.NewChannel()));

    await Assert.That(thrown!.Message).Contains($"'{QUEUE}'").And.Contains("0 ready message(s) and has 1 consumer(s)");
    await Assert.That(script.Channels[1].QueueDeletes).IsEmpty();
  }

  [Test]
  public async Task Upgrade_MessageArrivesBetweenTheCheckAndTheDelete_IsRefusedAsync() {
    var raced = _closed(Constants.PreconditionFailed);
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, DeleteFailure = raced };

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
      await _declareAsync(script, script.NewChannel()));

    await Assert.That(thrown!.Message).Contains($"'{QUEUE}'").And.Contains("while it was being upgraded");
    await Assert.That(thrown.InnerException).IsSameReferenceAs(raced);
    await Assert.That(script.Channels).Count().IsEqualTo(2).Because("nothing is declared after a refused delete");
  }

  [Test]
  public async Task Upgrade_OtherFaultDeletingTheQueue_PropagatesAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, DeleteFailure = _closed(Constants.NotFound) };

    await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
      await _declareAsync(script, script.NewChannel()));
  }

  [Test]
  public async Task Upgrade_DeclaringAgainFails_ReleasesTheFreshChannelAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, RefuseSecondDeclare = true };

    await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
      await _declareAsync(script, script.NewChannel()));

    await Assert.That(script.Channels[2].IsDisposed).IsTrue();
  }

  [Test]
  public async Task Declare_OtherBrokerFault_IsNotTreatedAsAnUpgradeAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, Refusal = _closed(Constants.NotFound) };

    await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
      await _declareAsync(script, script.NewChannel()));

    await Assert.That(script.Channels).Count().IsEqualTo(1).Because("only PRECONDITION_FAILED means other arguments");
  }

  [Test]
  public async Task Declare_InterruptionWithoutAReason_IsNotTreatedAsAnUpgradeAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = QUEUE, Refusal = new OperationInterruptedException() };

    await Assert.ThrowsAsync<OperationInterruptedException>(async () =>
      await _declareAsync(script, script.NewChannel()));

    await Assert.That(script.Channels).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Declare_QueueWithTheSameArguments_StaysOnTheCallersChannelAsync() {
    var script = new BrokerScript();
    var main = script.NewChannel();

    var live = await _declareAsync(script, main);

    await Assert.That(live).IsSameReferenceAs(main);
    await Assert.That(script.Channels).Count().IsEqualTo(1).Because("the common path opens no channel of its own");
  }

  [Test]
  public async Task Subscribe_QueueFromEarlierVersion_ConsumesOnTheFreshChannelAndReleasesTheClosedOneAsync() {
    var script = new BrokerScript { RefuseFirstDeclareOf = $"sub-{EXCHANGE}" };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(script.NewChannel()));
    var transport = new RabbitMQTransport(
      connection, new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() },
      new RabbitMQChannelPool(connection, maxChannels: 5), new RabbitMQOptions());
    await using var __ = transport;
    await transport.InitializeAsync();

    using var subscription = await transport.SubscribeAsync(
      (_, _, _) => Task.CompletedTask,
      new TransportDestination(EXCHANGE, "#", new Dictionary<string, JsonElement> {
        ["SubscriberName"] = JsonElement.Parse("\"sub\"")
      }));

    var refused = script.Channels.First(c => c.RefusedDeclare);
    var consuming = script.Channels.Single(c => c.BasicConsumeAsyncCalled);
    await Assert.That(refused.IsDisposed).IsTrue().Because("the broker closed it; the transport must not leak it");
    await Assert.That(consuming).IsNotSameReferenceAs(refused);
    await Assert.That(consuming.QueueDeclareArgumentsByQueue.ContainsKey($"sub-{EXCHANGE}")).IsTrue();
  }

  [Test]
  public async Task ProvisionManifest_QueueFromEarlierVersion_ContinuesOnAFreshChannelAsync() {
    const string service = "order-service";
    var context = new InboxSubscriptionContext(
      service,
      new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "myapp.orders.commands" },
      [new HandledMessageInfo("MyApp.Orders.Commands.CreateOrder", "myapp.orders.commands", MessageKind.Command)]);
    var manifest = TopologyManifestBuilder.Build(
      new SharedTopicOutboxStrategy(), new NamespaceInboxStrategy(), context, []);
    var firstQueue = $"{service}-{manifest.Subscriptions[0].Topic}";
    var secondQueue = $"{service}-{manifest.Subscriptions[1].Topic}";
    var script = new BrokerScript { RefuseFirstDeclareOf = firstQueue };
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(script.NewChannel()));
    var provisioner = new RabbitMQInfrastructureProvisioner(
      new RabbitMQChannelPool(connection, maxChannels: 10), NullLogger<RabbitMQInfrastructureProvisioner>.Instance);

    await provisioner.ProvisionManifestAsync(manifest);

    var redeclared = script.Channels.Single(c => !c.RefusedDeclare && c.QueueDeclareArgumentsByQueue.ContainsKey(firstQueue));
    await Assert.That(redeclared.QueueDeclareArgumentsByQueue.ContainsKey(secondQueue)).IsTrue()
      .Because("the rest of the pass continues on the fresh channel, not the closed one");
    await Assert.That(redeclared.IsDisposed).IsTrue().Because("the provisioner owns and releases the fresh channel");
  }

  // ========================================
  // HELPERS
  // ========================================

  private static Task<IChannel> _declareAsync(
      BrokerScript script, IChannel main, RabbitMQOptions? options = null, ILogger? logger = null) =>
    RabbitMQEntityProvisioning.DeclareSubscriptionInfrastructureAsync(
      main, _ => Task.FromResult<IChannel>(script.NewChannel()), options ?? new RabbitMQOptions(),
      EXCHANGE, QUEUE, ["#"], logger, CancellationToken.None);

  private static OperationInterruptedException _closed(int replyCode) =>
    new(new ShutdownEventArgs(ShutdownInitiator.Peer, (ushort)replyCode, $"reply {replyCode}"));

  /// <summary>The broker as these tests script it; every channel opened in a test shares it.</summary>
  private sealed class BrokerScript {
    private bool _refused;
    private bool _redeclared;

    public List<ScriptedChannel> Channels { get; } = [];
    public string? RefuseFirstDeclareOf { get; init; }
    public OperationInterruptedException? Refusal { get; init; }
    public bool RefuseSecondDeclare { get; init; }
    public uint Messages { get; init; }
    public uint Consumers { get; init; }
    public Exception? DeleteFailure { get; init; }
    public Exception? UnbindFailure { get; init; }
    public Exception? ExchangeDeleteFailure { get; init; }

    public ScriptedChannel NewChannel() {
      var channel = new ScriptedChannel(this);
      Channels.Add(channel);
      return channel;
    }

    /// <summary>The exception the declare of <paramref name="queue"/> fails with, if it does.</summary>
    public OperationInterruptedException? DeclareFailureFor(string queue) {
      if (queue != RefuseFirstDeclareOf) {
        return null;
      }
      if (!_refused) {
        _refused = true;
        return Refusal ?? _closed(Constants.PreconditionFailed);
      }
      if (RefuseSecondDeclare && !_redeclared) {
        _redeclared = true;
        return _closed(Constants.PreconditionFailed);
      }
      return null;
    }
  }

  private sealed class ScriptedChannel(BrokerScript script) : FakeChannel, IChannel {
    public bool RefusedDeclare { get; private set; }
    public List<(string Queue, bool IfUnused, bool IfEmpty)> QueueDeletes { get; } = [];
    public List<(string Queue, string Exchange, string RoutingKey)> Unbinds { get; } = [];
    public List<(string Exchange, bool IfUnused)> ExchangeDeletes { get; } = [];

    Task<QueueDeclareOk> IChannel.QueueDeclareAsync(string queue, bool durable, bool exclusive, bool autoDelete,
        IDictionary<string, object?>? arguments, bool passive, bool noWait, CancellationToken cancellationToken) {
      var failure = script.DeclareFailureFor(queue);
      if (failure is not null) {
        RefusedDeclare = true;
        throw failure;
      }
      return QueueDeclareAsync(queue, durable, exclusive, autoDelete, arguments, passive, noWait, cancellationToken);
    }

    Task<QueueDeclareOk> IChannel.QueueDeclarePassiveAsync(string queue, CancellationToken cancellationToken) =>
      Task.FromResult(new QueueDeclareOk(queue, script.Messages, script.Consumers));

    Task<uint> IChannel.QueueDeleteAsync(string queue, bool ifUnused, bool ifEmpty, bool noWait, CancellationToken cancellationToken) {
      if (script.DeleteFailure is not null) {
        throw script.DeleteFailure;
      }
      QueueDeletes.Add((queue, ifUnused, ifEmpty));
      return Task.FromResult(0u);
    }

    Task IChannel.QueueUnbindAsync(string queue, string exchange, string routingKey,
        IDictionary<string, object?>? arguments, CancellationToken cancellationToken) {
      if (exchange.EndsWith(".dlx", StringComparison.Ordinal)) {
        if (script.UnbindFailure is not null) {
          throw script.UnbindFailure;
        }
        Unbinds.Add((queue, exchange, routingKey));
        return Task.CompletedTask;
      }
      return QueueUnbindAsync(queue, exchange, routingKey, arguments, cancellationToken);
    }

    Task IChannel.ExchangeDeleteAsync(string exchange, bool ifUnused, bool noWait, CancellationToken cancellationToken) {
      if (script.ExchangeDeleteFailure is not null) {
        throw script.ExchangeDeleteFailure;
      }
      ExchangeDeletes.Add((exchange, ifUnused));
      return Task.CompletedTask;
    }
  }
}
