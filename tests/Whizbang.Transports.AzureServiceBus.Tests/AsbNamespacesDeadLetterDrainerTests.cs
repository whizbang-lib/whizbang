// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// #1186: a host with several Service Bus namespaces resolves its transport as a namespace router,
/// and the dead-letter drainer used to see only a plain Service Bus transport. Behind the router it
/// found no subscriptions at all, so no namespace's broker dead-letter queues were ever drained.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusNamespacesDeadLetterDrainer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ServiceCollectionExtensions.cs</code-under-test>
/// <docs>operations/dead-letter-queue/transport-recovery#multiple-namespaces</docs>
public class AsbNamespacesDeadLetterDrainerTests {

  private sealed class RecordingDrainer : ITransportDeadLetterDrainer, IAsyncDisposable {
    public int Invocations;
    public int LastBudget;
    public bool Disposed;
    public int ReturnPerDrain { get; init; } = 1;
    public string TransportName => "asb:recording";
    public Task<int> DrainDeadLetterQueueAsync(int maxCount, CancellationToken ct = default) {
      Interlocked.Increment(ref Invocations);
      LastBudget = maxCount;
      return Task.FromResult(Math.Min(ReturnPerDrain, maxCount));
    }
    public ValueTask DisposeAsync() {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private static AzureServiceBusTransport _transport() =>
    new(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions {
        AutoProvisionInfrastructure = false,
        EnableSessions = false,
        EnableReceiveLivenessWatchdog = false,
      },
      NullLogger<AzureServiceBusTransport>.Instance);

  private static async Task<AzureServiceBusTransport> _subscribedTransportAsync(string topic, string subscription) {
    var transport = _transport();
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, new TransportDestination(topic, subscription));
    return transport;
  }

  /// <summary>Builds the drainer with real fleets whose per-subscription drainers are recorded per transport.</summary>
  private static AzureServiceBusNamespacesDeadLetterDrainer _drainerOver(
      IReadOnlyList<AzureServiceBusTransport> transports,
      Dictionary<AzureServiceBusTransport, List<RecordingDrainer>> made,
      int returnPerDrain = 1) =>
    new(
      () => transports,
      transport => new AzureServiceBusFleetDeadLetterDrainer(
        () => transport.ActiveSubscriptions,
        _ => {
          var drainer = new RecordingDrainer { ReturnPerDrain = returnPerDrain };
          if (!made.TryGetValue(transport, out var list)) {
            made[transport] = list = [];
          }
          list.Add(drainer);
          return drainer;
        }));

  [Test]
  public async Task ServiceBusTransportsOf_APlainServiceBusTransport_IsItselfAsync() {
    var transport = _transport();

    var found = AzureServiceBusNamespacesDeadLetterDrainer.ServiceBusTransportsOf(transport);

    await Assert.That(found).Count().IsEqualTo(1);
    await Assert.That(found[0]).IsSameReferenceAs(transport);
  }

  [Test]
  public async Task ServiceBusTransportsOf_ANamespaceRouter_IsEveryNamespacesTransportAsync() {
    var defaultNamespace = _transport();
    var reporting = _transport();
    var billing = _transport();
    var router = new NamespaceRoutingTransport(
      defaultNamespace,
      new Dictionary<string, ITransport>(StringComparer.Ordinal) {
        ["reporting"] = reporting,
        ["billing"] = billing,
      });

    var found = AzureServiceBusNamespacesDeadLetterDrainer.ServiceBusTransportsOf(router);

    await Assert.That(found).Count().IsEqualTo(3)
      .Because("every namespace behind the router has dead-letter queues of its own (#1186)");
    await Assert.That(found).Contains(defaultNamespace);
    await Assert.That(found).Contains(reporting);
    await Assert.That(found).Contains(billing);
  }

  [Test]
  public async Task ServiceBusTransportsOf_ARouterWithAnotherKindOfTransport_SkipsItAsync() {
    var serviceBus = _transport();
    var router = new NamespaceRoutingTransport(
      new InProcessTransport(),
      new Dictionary<string, ITransport>(StringComparer.Ordinal) { ["asb"] = serviceBus });

    var found = AzureServiceBusNamespacesDeadLetterDrainer.ServiceBusTransportsOf(router);

    await Assert.That(found).Count().IsEqualTo(1);
    await Assert.That(found[0]).IsSameReferenceAs(serviceBus);
  }

  [Test]
  public async Task ServiceBusTransportsOf_AnotherKindOfTransport_IsNothingAsync() {
    var found = AzureServiceBusNamespacesDeadLetterDrainer.ServiceBusTransportsOf(new InProcessTransport());

    await Assert.That(found).IsEmpty();
  }

  /// <summary>
  /// Two namespaces can hold the same topic and subscription names. Each is drained through its own
  /// fleet, so neither hides the other.
  /// </summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_TwoNamespacesWithTheSameSubscription_DrainsBothAsync() {
    var first = await _subscribedTransportAsync("orders", "svc-orders");
    var second = await _subscribedTransportAsync("orders", "svc-orders");
    var made = new Dictionary<AzureServiceBusTransport, List<RecordingDrainer>>();
    await using var drainer = _drainerOver([first, second], made, returnPerDrain: 2);

    var drained = await drainer.DrainDeadLetterQueueAsync(100);

    await Assert.That(drained).IsEqualTo(4);
    await Assert.That(made[first].Single().Invocations).IsEqualTo(1);
    await Assert.That(made[second].Single().Invocations).IsEqualTo(1);
  }

  /// <summary>The budget is one total across namespaces: it paces broker operations.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_BudgetIsOneTotalAcrossNamespacesAsync() {
    var first = await _subscribedTransportAsync("orders", "svc-orders");
    var second = await _subscribedTransportAsync("billing", "svc-billing");
    var third = await _subscribedTransportAsync("audit", "svc-audit");
    var made = new Dictionary<AzureServiceBusTransport, List<RecordingDrainer>>();
    await using var drainer = _drainerOver([first, second, third], made, returnPerDrain: 3);

    var drained = await drainer.DrainDeadLetterQueueAsync(5);

    await Assert.That(drained).IsEqualTo(5);
    await Assert.That(made[first].Single().LastBudget).IsEqualTo(5);
    await Assert.That(made[second].Single().LastBudget).IsEqualTo(2)
      .Because("the second namespace gets only what the first left of the pass's budget");
    await Assert.That(made.ContainsKey(third)).IsFalse()
      .Because("a spent budget ends the pass before the next namespace is touched");
  }

  [Test]
  public async Task DrainDeadLetterQueueAsync_NoBudget_DrainsNothingAsync() {
    var transport = await _subscribedTransportAsync("orders", "svc-orders");
    var made = new Dictionary<AzureServiceBusTransport, List<RecordingDrainer>>();
    await using var drainer = _drainerOver([transport], made);

    await Assert.That(await drainer.DrainDeadLetterQueueAsync(0)).IsEqualTo(0);
    await Assert.That(made).IsEmpty();
  }

  /// <summary>A namespace's fleet is built once and reused, so its per-subscription drainers persist.</summary>
  [Test]
  public async Task DrainDeadLetterQueueAsync_ReusesEachNamespacesFleetAcrossPassesAsync() {
    var transport = await _subscribedTransportAsync("orders", "svc-orders");
    var made = new Dictionary<AzureServiceBusTransport, List<RecordingDrainer>>();
    await using var drainer = _drainerOver([transport], made);

    await drainer.DrainDeadLetterQueueAsync(10);
    await drainer.DrainDeadLetterQueueAsync(10);

    await Assert.That(made[transport]).Count().IsEqualTo(1);
    await Assert.That(made[transport][0].Invocations).IsEqualTo(2);
  }

  [Test]
  public async Task DisposeAsync_DisposesEveryNamespacesFleetAsync() {
    var first = await _subscribedTransportAsync("orders", "svc-orders");
    var second = await _subscribedTransportAsync("billing", "svc-billing");
    var made = new Dictionary<AzureServiceBusTransport, List<RecordingDrainer>>();
    var drainer = _drainerOver([first, second], made);
    await drainer.DrainDeadLetterQueueAsync(10);

    await drainer.DisposeAsync();

    await Assert.That(made.Values.SelectMany(d => d).All(d => d.Disposed)).IsTrue();
  }

  [Test]
  public async Task TransportName_IsAsbAsync() {
    await using var drainer = _drainerOver([], []);

    await Assert.That(drainer.TransportName).IsEqualTo("asb");
  }

  [Test]
  public async Task Constructor_NullArguments_ThrowAsync() {
    await Assert.That(() => new AzureServiceBusNamespacesDeadLetterDrainer(null!, _ => null!))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new AzureServiceBusNamespacesDeadLetterDrainer(() => [], null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  /// <summary>
  /// The registration hands the drainer the transport each namespace owns, so a drain reaches each
  /// namespace through its own client rather than the default namespace's.
  /// </summary>
  [Test]
  public async Task Client_IsTheClientTheTransportWasBuiltWithAsync() {
    var client = new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableReceiveLivenessWatchdog = false },
      NullLogger<AzureServiceBusTransport>.Instance);

    await Assert.That(transport.Client).IsSameReferenceAs(client);
  }
}
