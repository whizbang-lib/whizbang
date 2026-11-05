// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Directed integrity control messages go to exactly the addressee and expire (#2078): the destination carries the
/// integrity time to live, and a message sent to a service's advertised request topic (its own command inbox) reaches
/// that service's subscription and no other.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Transports/IntegrityControlDestination.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/RepairTopicResolver.cs</code-under-test>
public class IntegrityControlDestinationTests {

  [Test]
  public async Task For_StampsTheIntegrityTimeToLive_ByDefaultAnHourAsync() {
    var destination = IntegrityControlDestination.For("inbox.app.orders", TrackedGuid.New().Value,
      typeof(RequestIntegrityManifest), new StreamIntegrityOptions());

    await Assert.That(ControlMessageTtl.FromMetadata(destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(60))
      .Because("a request regenerated every cycle must expire at the broker, not pile up on a stopped subscriber");
    await Assert.That(destination.Address).IsEqualTo("inbox.app.orders");
    await Assert.That(destination.RoutingKey).IsEqualTo("whizbang.core.messaging.requestintegritymanifest");
  }

  [Test]
  public async Task For_StampsTheConfiguredTimeToLiveAsync() {
    var destination = IntegrityControlDestination.For("inbox.app.orders", TrackedGuid.New().Value,
      typeof(RequestRedeliveryCommand), new StreamIntegrityOptions { ControlMessageTimeToLiveMinutes = 15 });

    await Assert.That(ControlMessageTtl.FromMetadata(destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(15));
  }

  [Test]
  public async Task ADirectedMessage_ToTheAddresseesAdvertisedTopic_ReachesOnlyTheAddresseeAsync() {
    var transport = new InProcessTransport();
    await transport.InitializeAsync();
    var orders = new Received();
    var billing = new Received();
    // Each service consumes its own command inbox and the shared topic, as the namespace inbox strategy provisions them.
    var subscriptions = new List<ISubscription>();
    subscriptions.AddRange(await _subscribeAsync(transport, orders, "inbox.app.orders", "inbox.whizbang"));
    subscriptions.AddRange(await _subscribeAsync(transport, billing, "inbox.app.billing", "inbox.whizbang"));
    var ordersAdvertises = RepairTopicResolver.Resolve(null, _consumer(_ownInbox("inbox.app.orders"), new("inbox.whizbang")))!;

    await transport.PublishAsync(_envelope(), IntegrityControlDestination.For(
      ordersAdvertises, TrackedGuid.New().Value, typeof(RequestIntegrityManifest), new StreamIntegrityOptions()));
    // Disposing a subscription flushes what it collected, so every delivery is counted without waiting on a timer.
    foreach (var subscription in subscriptions) {
      subscription.Dispose();
    }

    await Assert.That(orders.Count).IsEqualTo(1);
    await Assert.That(billing.Count).IsEqualTo(0)
      .Because("a directed request goes to the addressee's own inbox, which no other service subscribes to");
  }

  private static async Task<List<ISubscription>> _subscribeAsync(InProcessTransport transport, Received received, params string[] topics) {
    var subscriptions = new List<ISubscription>();
    foreach (var topic in topics) {
      subscriptions.Add(await transport.SubscribeBatchAsync((batch, _) => {
        received.Add(batch.Count);
        return Task.CompletedTask;
      }, new TransportDestination(topic), new TransportBatchOptions()));
    }
    return subscriptions;
  }

  private static ServiceProvider _consumer(params TransportDestination[] destinations) {
    var options = new TransportConsumerOptions();
    options.Destinations.AddRange(destinations);
    return new ServiceCollection().AddSingleton(options).BuildServiceProvider();
  }

  private static TransportDestination _ownInbox(string topic) => new(topic, Metadata: new Dictionary<string, JsonElement> {
    [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = JsonElement.Parse("true"),
  });

  private static MessageEnvelope<JsonElement> _envelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonElement.Parse("{}"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Outbox },
  };

  private sealed class Received {
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Add(int n) => Interlocked.Add(ref _count, n);
  }
}
