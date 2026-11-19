// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Routing.ExcludedEventFixtures;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Every event destination carries the subjects its subscription excludes (#2175), an empty list when it excludes
/// nothing, so a transport converges a subscription back to delivering everything when a type leaves the list. The
/// startup checks run wherever event destinations are built.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/TransportSubscriptionBuilder.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerBuilderExtensions.cs</code-under-test>
public class TransportSubscriptionBuilderExcludedEventTypesTests {
  private const string FIXTURE_TOPIC = "whizbang.core.tests.routing.excludedeventfixtures";
  private const string OTHER_TOPIC = "myapp.other.events";

  private static TransportSubscriptionBuilder _builder(
      RoutingOptions options,
      IReceptorRegistryQuery? registry = null,
      ILogger? logger = null) {
    var registryOf = new FixedEventNamespaces([FIXTURE_TOPIC, OTHER_TOPIC]);
    var discovery = new EventSubscriptionDiscovery(Options.Create(options), registryOf);
    return new TransportSubscriptionBuilder(
      Options.Create(options),
      discovery,
      "OrderService",
      options.InboxStrategy,
      registry ?? new NothingConsumed(),
      new FixedMessageCatalog(true, typeof(NoisyEvent)),
      logger);
  }

  private static string[] _excludedOf(TransportDestination destination) =>
    [.. destination.Metadata![TransportSubscriptionBuilder.ExcludedEventSubjectsMetadataKey].EnumerateArray()
      .Select(e => e.GetString()!)];

  [Test]
  public async Task BuildEventDestinations_NothingExcluded_EveryDestinationCarriesAnEmptyListAsync() {
    var destinations = _builder(new RoutingOptions()).BuildEventDestinations();

    await Assert.That(destinations.Count).IsEqualTo(2);
    foreach (var destination in destinations) {
      await Assert.That(destination.Metadata![TransportSubscriptionBuilder.ExcludedEventSubjectsMetadataKey].ValueKind)
        .IsEqualTo(JsonValueKind.Array);
      await Assert.That(_excludedOf(destination)).IsEmpty();
    }
  }

  [Test]
  public async Task BuildEventDestinations_ExcludedType_IsOnlyOnItsOwnTopicAsync() {
    var logger = new RecordingRoutingLogger();
    var destinations = _builder(new RoutingOptions().ExcludeEventType<NoisyEvent>(), logger: logger)
      .BuildEventDestinations();

    var fixture = destinations.Single(d => d.Address == FIXTURE_TOPIC);
    var other = destinations.Single(d => d.Address == OTHER_TOPIC);
    await Assert.That(_excludedOf(fixture)).IsEquivalentTo([EventSubject.For(typeof(NoisyEvent))]);
    await Assert.That(_excludedOf(other)).IsEmpty();
    await Assert.That(fixture.Metadata!["SubscriberName"].GetString()).IsEqualTo("OrderService");
    await Assert.That(logger.Has(LogLevel.Information, FIXTURE_TOPIC)).IsTrue()
      .Because("the logger handed to the builder receives the effective exclusions");
  }

  [Test]
  public async Task BuildDestinations_ExcludedTypeThisServiceConsumes_FailsAsync() {
    var builder = _builder(new RoutingOptions().ExcludeEventType<NoisyEvent>(), registry: new ConsumesNoisy());

    await Assert.That(() => builder.BuildDestinations()).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ShortConstructor_StillCarriesTheExclusionsAsync() {
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>();
    var discovery = new EventSubscriptionDiscovery(Options.Create(options), new FixedEventNamespaces([FIXTURE_TOPIC]));
    var builder = new TransportSubscriptionBuilder(
      Options.Create(options), discovery, "OrderService", options.InboxStrategy, new NothingConsumed());

    var destination = builder.BuildEventDestinations().Single();

    await Assert.That(_excludedOf(destination)).IsEquivalentTo([EventSubject.For(typeof(NoisyEvent))]);
  }

  [Test]
  public async Task AddTransportConsumer_ExcludedTypeThisServiceConsumes_FailsWhenTheConsumerOptionsResolveAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbang()
      .WithRouting(routing => routing.ExcludeEventType<NoisyEvent>())
      .AddTransportConsumer();
    services.AddSingleton<IEventNamespaceRegistry>(new FixedEventNamespaces([FIXTURE_TOPIC]));
    services.AddSingleton<IReceptorRegistryQuery>(new ConsumesNoisy());
    var provider = services.BuildServiceProvider();

    var thrown = await Assert.That(() => provider.GetRequiredService<TransportConsumerOptions>())
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.Message).Contains("NoisyEvent");
  }

  [Test]
  public async Task AddTransportConsumer_ExcludedType_LandsOnTheConsumerDestinationAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbang()
      .WithRouting(routing => routing.ExcludeEventType<NoisyEvent>())
      .AddTransportConsumer();
    services.AddSingleton<IEventNamespaceRegistry>(new FixedEventNamespaces([FIXTURE_TOPIC]));
    services.AddSingleton<IReceptorRegistryQuery>(new NothingConsumed());

    var options = services.BuildServiceProvider().GetRequiredService<TransportConsumerOptions>();

    var destination = options.Destinations.Single(d => d.Address == FIXTURE_TOPIC);
    await Assert.That(_excludedOf(destination)).IsEquivalentTo([EventSubject.For(typeof(NoisyEvent))]);
  }

  private sealed class FixedEventNamespaces(IEnumerable<string> namespaces) : IEventNamespaceRegistry {
    private readonly HashSet<string> _namespaces = new(namespaces, StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> GetPerspectiveEventNamespaces() => _namespaces;
    public IReadOnlySet<string> GetReceptorEventNamespaces() => new HashSet<string>();
    public IReadOnlySet<string> GetAllEventNamespaces() => _namespaces;
  }

  private class NothingConsumed : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public virtual bool HasAnyConsumer(string messageType) => false;
    public IReadOnlyList<HandledMessageInfo> GetHandledMessages() => [];
    public virtual IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => [];
  }

  private sealed class ConsumesNoisy : NothingConsumed {
    public override IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() =>
      [new ConsumedMessageInfo(TypeNameFormatter.Format(typeof(NoisyEvent)), IsEvent: true)];
  }
}
