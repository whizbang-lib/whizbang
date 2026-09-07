using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Coverage-round-23 targeted tests for <see cref="TransportSubscriptionBuilder"/>: metadata
/// value types the conversion switch does not special-case, the DI-registered builder factory
/// actually being exercised by a resolve, and the topology manifest factory's "no routing
/// configured" fallback.
/// </summary>
public class TransportSubscriptionBuilderCoverageTests {

  // Targets TransportSubscriptionBuilder.cs line 190 (the int case) and line 193 (the default
  // fallback case) of the metadata-conversion switch in _buildMetadata. If an int or an
  // otherwise-unrecognized-typed metadata value silently fell through without converting, a
  // transport-specific hint a subscriber depends on (a numeric budget, an opaque correlation
  // value) would be missing from the built destination with no error - the subscription would
  // still get created, just without the piece of metadata a downstream reader needed.
  [Test]
  public async Task BuildInboxDestinations_IntAndUnknownTypedMetadata_ConvertBothToJsonElementAsync() {
    var routingOptions = new RoutingOptions();
    routingOptions.Inbox.UseCustom(new MetadataVarietyStrategy());

    var discovery = new EventSubscriptionDiscovery(Options.Create(routingOptions), null);
    var builder = new TransportSubscriptionBuilder(
        Options.Create(routingOptions),
        discovery,
        "OrderService");

    var destinations = builder.BuildInboxDestinations();

    await Assert.That(destinations.Count).IsEqualTo(1);
    var metadata = destinations[0].Metadata!;
    await Assert.That(metadata["RetryBudget"].GetInt32()).IsEqualTo(3)
      .Because("int-valued subscription metadata must convert via the int case, not fall through " +
               "to the string default and corrupt a numeric value a subscriber depends on");
    await Assert.That(metadata["CorrelationSeed"].GetString())
      .IsEqualTo("11111111-1111-1111-1111-111111111111")
      .Because("a metadata value of a type the switch does not special-case must still be " +
               "forwarded via ToString(), not silently dropped from the destination");
  }

  // Targets lines 217-222: the AddSingleton factory registered by AddTransportSubscriptionBuilder.
  // A test that only calls the extension method without ever resolving TransportSubscriptionBuilder
  // never runs this lambda. If the factory grabbed the wrong (or a default, unconfigured)
  // RoutingOptions/discovery instance instead of the ones actually registered, the resolved
  // builder would compute subscriptions for the wrong topology and the service would come up
  // listening to nothing real, with no exception anywhere.
  [Test]
  public async Task AddTransportSubscriptionBuilder_ResolvesBuilderWiredToRegisteredDependenciesAsync() {
    var routingOptions = new RoutingOptions();
    routingOptions.OwnDomains("myapp.orders.commands");
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(routingOptions));
    services.AddSingleton(new EventSubscriptionDiscovery(Options.Create(routingOptions), null));
    services.AddTransportSubscriptionBuilder("OrderService");
    using var provider = services.BuildServiceProvider();

    var builder = provider.GetRequiredService<TransportSubscriptionBuilder>();
    var destination = builder.BuildInboxDestination();

    await Assert.That(destination).IsNotNull();
    await Assert.That(destination!.Metadata!["SubscriberName"].GetString()).IsEqualTo("OrderService")
      .Because("the DI-registered factory must construct the builder with the service name passed " +
               "to AddTransportSubscriptionBuilder, not a default or mismatched value");
  }

  // Targets line 252: the "no routing configured" branch of TryAddTopologyManifest's factory,
  // which fires when no IOptions<RoutingOptions> is registered at all. A host that has not wired
  // up routing must still resolve a usable TopologyManifest - manifest-driven provisioning has to
  // be a deliberate no-op here, not a startup crash and not a manifest that invents subscriptions
  // or publications nobody configured.
  [Test]
  public async Task AddTransportSubscriptionBuilder_NoRoutingOptionsRegistered_TopologyManifestIsEmptyAsync() {
    var services = new ServiceCollection();
    services.AddTransportSubscriptionBuilder("OrderService");
    using var provider = services.BuildServiceProvider();

    var manifest = provider.GetService<TopologyManifest>();

    await Assert.That(manifest).IsNotNull();
    await Assert.That(manifest!.ServiceName).IsEqualTo("OrderService");
    await Assert.That(manifest.Subscriptions).IsEmpty()
      .Because("with no routing configured, provisioning must name nothing rather than guess a " +
               "default topology");
    await Assert.That(manifest.PublishDestinations).IsEmpty();
  }

  private sealed class MetadataVarietyStrategy : IInboxRoutingStrategy {
    public InboxSubscription GetSubscription(
        IReadOnlySet<string> ownedDomains, string serviceName, MessageKind kind)
      => new("inbox.metadata-variety");

    public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context)
      => [new InboxSubscription("inbox.metadata-variety", Metadata: new Dictionary<string, object> {
        ["RetryBudget"] = 3,
        ["CorrelationSeed"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
      })];
  }
}
