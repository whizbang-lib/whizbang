// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="TransportSubscriptionBuilder"/>: subscription metadata whose value
/// is null or renders to no text converts to an empty string, and the topology manifest factory
/// builds from routing alone when no message catalog is registered.
/// </summary>
public class TransportSubscriptionBuilderBranchCoverageTests {

  [Test]
  public async Task BuildInboxDestinations_NullAndTextlessMetadataValues_ConvertToEmptyStringsAsync() {
    var routingOptions = new RoutingOptions();
    routingOptions.Inbox.UseCustom(new TextlessMetadataStrategy());
    var discovery = new EventSubscriptionDiscovery(routingOptions: Options.Create(routingOptions), registry: new StaticEventNamespaceRegistry());
    var builder = new TransportSubscriptionBuilder(
        routingOptions: Options.Create(routingOptions),
        discovery: discovery,
        serviceName: "OrderService",
        inboxStrategy: routingOptions.InboxStrategy,
        receptorRegistry: new PermissiveReceptorRegistryQuery());

    var destinations = builder.BuildInboxDestinations();

    await Assert.That(destinations.Count).IsEqualTo(1);
    var metadata = destinations[0].Metadata!;
    await Assert.That(metadata["NullValue"].GetString()).IsEqualTo(string.Empty)
      .Because("a null metadata value is forwarded as an empty string rather than dropped or crashing the build");
    await Assert.That(metadata["TextlessValue"].GetString()).IsEqualTo(string.Empty)
      .Because("a value whose ToString yields null is forwarded as an empty string too");
  }

  [Test]
  public async Task TopologyManifest_RoutingConfiguredButNoMessageCatalog_BuildsWithNoPublishDestinationsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(new RoutingOptions()));
    services.AddSingleton<IReceptorRegistryQuery>(new PermissiveReceptorRegistryQuery());
    TransportSubscriptionBuilderExtensions.TryAddTopologyManifest(services, _ => "OrderService");
    await using var provider = services.BuildServiceProvider();

    var manifest = provider.GetRequiredService<TopologyManifest>();

    await Assert.That(manifest.PublishDestinations.Count).IsEqualTo(0)
      .Because("with no message catalog registered there is nothing to publish, which reads as an empty catalog");
    await Assert.That(manifest.Subscriptions.Select(s => s.Topic)).Contains(CommandInboxNaming.SystemBroadcastTopic)
      .Because("routing is configured, so the subscriptions are still computed rather than taking the empty fallback");
  }

  private sealed class TextlessValue {
#pragma warning disable S2225 // A null ToString() is the case under test.
    public override string? ToString() => null;
#pragma warning restore S2225
  }

  private sealed class TextlessMetadataStrategy : IInboxRoutingStrategy {
    public InboxSubscription GetSubscription(
        IReadOnlySet<string> ownedDomains, string serviceName, MessageKind kind)
      => new("inbox.textless-metadata");

    public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context)
      => [new InboxSubscription("inbox.textless-metadata", Metadata: new Dictionary<string, object> {
        ["NullValue"] = null!,
        ["TextlessValue"] = new TextlessValue(),
      })];
  }
}
