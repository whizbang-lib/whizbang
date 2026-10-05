// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace WbRuntimeSub.Scheduling.Events {
  /// <summary>An event consumed only through a receptor registered at startup, never at compile time.</summary>
  public sealed class ReminderDueEvent : Whizbang.Core.IEvent {
    [Whizbang.Core.StreamId] public Guid StreamId { get; set; }
  }

  /// <summary>A second event in the same namespace, so two subscriptions can share one topic.</summary>
  public sealed class ReminderCanceledEvent : Whizbang.Core.IEvent {
    [Whizbang.Core.StreamId] public Guid StreamId { get; set; }
  }
}

namespace WbRuntimeSub.Owned.Events {
  /// <summary>An event in a namespace the test service owns.</summary>
  public sealed class OwnedThingHappenedEvent : Whizbang.Core.IEvent {
    [Whizbang.Core.StreamId] public Guid StreamId { get; set; }
  }
}

namespace Whizbang.Core.Tests.Routing {
  /// <summary>
  /// A receptor registered at startup — a framework-owned one in particular — consumes an event just
  /// as a generated receptor does, so the host must subscribe to that event's topic just as it would
  /// for a generated one. Subscriptions used to come only from compile-time discovery, which left a
  /// startup-registered receptor listening on a topic nothing subscribed to.
  /// </summary>
  /// <code-under-test>src/Whizbang.Core/Routing/RuntimeEventSubscription.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Core/Routing/EventSubscriptionDiscovery.cs</code-under-test>
  public class RuntimeEventSubscriptionTests {
    private const string SCHEDULING = "wbruntimesub.scheduling.events";

    private static EventSubscriptionDiscovery _discovery(
        RoutingOptions routing, IEventNamespaceRegistry registry, params IRuntimeEventSubscription[] runtime)
      => new(Options.Create(routing), registry, runtime);

    [Test]
    public async Task DiscoverEventNamespaces_RuntimeSubscription_SubscribesToTheEventsTopicAsync() {
      var discovery = _discovery(new RoutingOptions(), new FixedRegistry(),
        new RuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>());

      var namespaces = discovery.DiscoverEventNamespaces();

      await Assert.That(namespaces).Contains(SCHEDULING)
        .Because("a receptor registered at startup consumes the event, so the host must subscribe to its topic");
      await Assert.That(namespaces.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DiscoverEventNamespaces_RuntimeSubscription_UsesTheTopicThePublisherSendsToAsync() {
      var discovery = _discovery(new RoutingOptions(), new FixedRegistry(),
        new RuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>());
      var published = new RoutingOptions().OutboxStrategy.GetDestination(
        typeof(WbRuntimeSub.Scheduling.Events.ReminderDueEvent), new HashSet<string>(), MessageKind.Event);

      var namespaces = discovery.DiscoverEventNamespaces();

      await Assert.That(namespaces).Contains(published.Address)
        .Because("a subscription to any other name than the one the event is published to receives nothing");
    }

    [Test]
    public async Task DiscoverEventNamespaces_RuntimeAndCompileTimeConsumersOfOneTopic_SubscribeOnceAsync() {
      var discovery = _discovery(new RoutingOptions(), new FixedRegistry(SCHEDULING),
        new RuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>(),
        new RuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderCanceledEvent>());

      var namespaces = discovery.DiscoverEventNamespaces();

      await Assert.That(namespaces.Count).IsEqualTo(1)
        .Because("a second subscription to the same topic would deliver every message on it twice");
    }

    [Test]
    public async Task DiscoverEventNamespaces_RuntimeSubscriptionOnAnOwnedNamespace_IsLeftOutLikeACompileTimeOneAsync() {
      var routing = new RoutingOptions();
      routing.OwnDomains("wbruntimesub.owned");
      var discovery = _discovery(routing, new FixedRegistry(),
        new RuntimeEventSubscription<WbRuntimeSub.Owned.Events.OwnedThingHappenedEvent>());

      var namespaces = discovery.DiscoverEventNamespaces();

      await Assert.That(namespaces).IsEmpty()
        .Because("a service publishes to the namespaces it owns and does not subscribe to them, whoever consumes the event");
    }

    [Test]
    public async Task Constructor_WithoutRuntimeSubscriptions_DiscoversCompileTimeNamespacesOnlyAsync() {
      var discovery = new EventSubscriptionDiscovery(Options.Create(new RoutingOptions()), new FixedRegistry("myapp.orders.events"));

      var namespaces = discovery.DiscoverEventNamespaces();

      await Assert.That(namespaces.Count).IsEqualTo(1);
      await Assert.That(namespaces).Contains("myapp.orders.events");
    }

    [Test]
    public async Task Constructor_NullRuntimeSubscriptions_ThrowsAsync() {
      EventSubscriptionDiscovery action() => new(Options.Create(new RoutingOptions()), new FixedRegistry(), runtimeSubscriptions: null!);

      await Assert.That(action).Throws<ArgumentNullException>().WithMessageContaining("runtimeSubscriptions");
    }

    [Test]
    public async Task EventType_IsTheDeclaredEventAsync() {
      var subscription = new RuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>();

      await Assert.That(subscription.EventType).IsEqualTo(typeof(WbRuntimeSub.Scheduling.Events.ReminderDueEvent));
    }

    [Test]
    public async Task AddRuntimeEventSubscription_CalledTwiceForOneEvent_RegistersItOnceAsync() {
      var services = new ServiceCollection();

      services.AddRuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>();
      services.AddRuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>();
      services.AddRuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderCanceledEvent>();

      await using var provider = services.BuildServiceProvider();
      var registered = provider.GetServices<IRuntimeEventSubscription>().Select(s => s.EventType).ToList();
      await Assert.That(registered.Count).IsEqualTo(2)
        .Because("registering the same consumer twice, as a library and a host both might, must not change anything");
      await Assert.That(registered).Contains(typeof(WbRuntimeSub.Scheduling.Events.ReminderDueEvent));
      await Assert.That(registered).Contains(typeof(WbRuntimeSub.Scheduling.Events.ReminderCanceledEvent));
    }

    [Test]
    public async Task AddRuntimeEventSubscription_NullServices_ThrowsAsync() {
      IServiceCollection action() => RuntimeEventSubscriptionExtensions.AddRuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>(null!);

      await Assert.That(action).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WithRouting_ResolvedDiscovery_IncludesRuntimeSubscriptionsRegisteredInAnyOrderAsync() {
      var services = new ServiceCollection();
      // Registered on both sides of the routing configuration: a library's extension can run before
      // or after the host configures routing, and the subscription must hold either way.
      services.AddRuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>();
      services.AddSingleton<IEventNamespaceRegistry>(new FixedRegistry());
      services.AddWhizbang().WithRouting(_ => { });
      services.AddRuntimeEventSubscription<WbRuntimeSub.Owned.Events.OwnedThingHappenedEvent>();

      await using var provider = services.BuildServiceProvider();
      var namespaces = provider.GetRequiredService<EventSubscriptionDiscovery>().DiscoverEventNamespaces();

      await Assert.That(namespaces).Contains(SCHEDULING);
      await Assert.That(namespaces).Contains("wbruntimesub.owned.events");
    }

    [Test]
    public async Task TransportSubscriptionBuilder_RuntimeSubscription_BuildsAnEventDestinationForItAsync() {
      var discovery = _discovery(new RoutingOptions(), new FixedRegistry(),
        new RuntimeEventSubscription<WbRuntimeSub.Scheduling.Events.ReminderDueEvent>());
      var builder = new TransportSubscriptionBuilder(
        Options.Create(new RoutingOptions()), discovery, "reminder-service", inboxStrategy: null,
        receptorRegistry: new Whizbang.Core.Messaging.WhizbangReceptorRegistryQueryAdapter(Whizbang.Core.Messaging.NullReceptorRegistry.Instance));

      var destinations = builder.BuildEventDestinations();

      await Assert.That(destinations.Count(d => d.Address == SCHEDULING)).IsEqualTo(1)
        .Because("the consumer worker subscribes to exactly the destinations this builder produces");
    }

    private sealed class FixedRegistry(params string[] namespaces) : IEventNamespaceRegistry {
      private readonly HashSet<string> _namespaces = new(namespaces, StringComparer.OrdinalIgnoreCase);
      public IReadOnlySet<string> GetPerspectiveEventNamespaces() => new HashSet<string>();
      public IReadOnlySet<string> GetReceptorEventNamespaces() => _namespaces;
      public IReadOnlySet<string> GetAllEventNamespaces() => _namespaces;
    }
  }
}
