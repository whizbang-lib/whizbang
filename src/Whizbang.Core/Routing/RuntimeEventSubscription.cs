// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Routing;

/// <summary>
/// An event this host consumes through a receptor registered at startup rather than one the source
/// generator discovered, declared so the transport subscribes to the event's topic all the same.
/// </summary>
/// <remarks>
/// <para>
/// Transport subscriptions are derived from the event namespaces the generator finds on the host's
/// perspectives and receptors (<see cref="IEventNamespaceRegistry"/>). A receptor registered at
/// startup through <c>IReceptorRegistry.Register</c> — typically a framework-owned one that a
/// library adds on the host's behalf — is invisible to that discovery. Without a declaration the
/// receptor is registered, the event is published to its topic, and nothing on this host ever
/// subscribes to that topic, so the event is never received. Nothing fails and nothing logs.
/// </para>
/// <para>
/// <see cref="EventSubscriptionDiscovery"/> adds each declared event's topic to the ones it
/// discovered, before the owned-domain subtraction, so a declared event is subscribed exactly as a
/// generated receptor for it would be: once per topic, however many consumers the topic has.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#runtime-event-subscriptions</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/RuntimeEventSubscriptionTests.cs:DiscoverEventNamespaces_RuntimeSubscription_SubscribesToTheEventsTopicAsync</tests>
public interface IRuntimeEventSubscription {
  /// <summary>The consumed event type. Its topic is derived the same way the publisher derives it.</summary>
  Type EventType { get; }
}

/// <summary>
/// The declaration for one event type. The type is closed over the event, so the container keeps one
/// registration per event however many times it is declared.
/// </summary>
/// <typeparam name="TEvent">The consumed event type.</typeparam>
/// <docs>fundamentals/dispatcher/routing#runtime-event-subscriptions</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/RuntimeEventSubscriptionTests.cs:EventType_IsTheDeclaredEventAsync</tests>
public sealed class RuntimeEventSubscription<TEvent> : IRuntimeEventSubscription where TEvent : IEvent {
  /// <inheritdoc />
  public Type EventType => typeof(TEvent);
}

/// <summary>Registration for <see cref="IRuntimeEventSubscription"/>.</summary>
/// <docs>fundamentals/dispatcher/routing#runtime-event-subscriptions</docs>
public static class RuntimeEventSubscriptionExtensions {
  /// <summary>
  /// Declares that this host consumes <typeparamref name="TEvent"/> through a receptor it registers
  /// at startup, so the transport consumer subscribes to the event's topic as it would for a
  /// generated receptor. Idempotent, and independent of the order in which routing is configured.
  /// </summary>
  /// <typeparam name="TEvent">The consumed event type.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The same service collection.</returns>
  /// <docs>fundamentals/dispatcher/routing#runtime-event-subscriptions</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RuntimeEventSubscriptionTests.cs:AddRuntimeEventSubscription_CalledTwiceForOneEvent_RegistersItOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RuntimeEventSubscriptionTests.cs:WithRouting_ResolvedDiscovery_IncludesRuntimeSubscriptionsRegisteredInAnyOrderAsync</tests>
  public static IServiceCollection AddRuntimeEventSubscription<TEvent>(this IServiceCollection services) where TEvent : IEvent {
    ArgumentNullException.ThrowIfNull(services);
    // TryAddEnumerable deduplicates on the implementation type, which is closed over TEvent: the same
    // event registered twice is one subscription, two events are two.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IRuntimeEventSubscription>(new RuntimeEventSubscription<TEvent>()));
    return services;
  }
}
