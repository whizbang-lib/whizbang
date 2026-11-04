// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Reads "what does this service consume" from the consumer registry (<see cref="IConsumerRegistry"/>), the one
/// registry that answers it, for the callers that need it outside the receive boundary.
/// </summary>
/// <remarks>
/// <para>
/// Which question each type source answers:
/// </para>
/// <list type="bullet">
/// <item><see cref="IConsumerRegistry"/>: the message types something on this service consumes (an inbox or
/// lifecycle receptor, a perspective, a notification tag, the composite fan-out, the collective sink). The receive
/// boundary asks it as a predicate; stream integrity's audit, checkpoint and manifest comparisons, the
/// subscription-expansion backfill and the orphan-inbox janitor read it here.</item>
/// <item><see cref="RoutingOptions.AbsorbedNamespaces"/>: namespaces whose events are stored though nothing consumes
/// them. The receive boundary keeps them, so stream integrity audits them too.</item>
/// <item><see cref="IEventTypeProvider"/>: the event types this service's perspectives fold, plus collective events.
/// Perspective-specific work (the perspective worker's deserialization, the dispatcher's perspective cache) reads it.
/// It is not the consumed set: a service with no perspectives has none, and a receptor-only type is never in it.</item>
/// <item><see cref="IMessageTypeCatalog"/>: every message type the application declares, consumed or not.</item>
/// </list>
/// </remarks>
/// <docs>internals/receptor-registry-query#what-a-service-consumes</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ConsumedMessageTypesTests.cs</tests>
public static class ConsumedMessageTypes {
  /// <summary>
  /// The event types this service consumes, in the assembly-qualified wire form, deduplicated and in ordinal order.
  /// Empty when nothing here consumes an event, and when no registry is registered.
  /// </summary>
  /// <param name="services">The service provider to resolve the registry from.</param>
  /// <returns>The consumed event type names.</returns>
  public static IReadOnlyList<string> EventTypeNames(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.GetService<IConsumerRegistry>()?.GetConsumedEventTypes() ?? [];
  }

  /// <summary>
  /// The event namespaces this service absorbs (<see cref="RoutingOptions.AbsorbNamespaces"/>), lowercase and in
  /// ordinal order: their events are stored here though nothing consumes them. Empty when routing is not configured.
  /// </summary>
  /// <param name="services">The service provider to resolve the routing options from.</param>
  /// <returns>The absorbed namespaces.</returns>
  public static IReadOnlyList<string> AbsorbedNamespaces(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    var absorbed = services.GetService<IOptions<RoutingOptions>>()?.Value.AbsorbedNamespaces ?? new HashSet<string>();
    return [.. absorbed.Order(StringComparer.Ordinal)];
  }

  /// <summary>
  /// The receive boundary's decision as a predicate over payload type names: true when this service keeps an inbound
  /// message of the type (something here consumes it, it is a composite, or the discard policy keeps it, as for an
  /// absorbed namespace). Stream integrity filters what an origin reported through it.
  /// </summary>
  /// <param name="services">The service provider to resolve the registries and the discard policy from.</param>
  /// <returns>The predicate.</returns>
  public static Predicate<string> Keeps(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    var registry = services.GetService<IConsumerRegistry>();
    var runtime = services.GetService<IReceptorRegistry>() ?? NullReceptorRegistry.Instance;
    var markers = services.GetService<IEventMarkerResolver>();
    var policy = services.GetService<IMessageDiscardPolicy>();
    return typeName => ReceiveConsumerGate.Keeps(typeName, registry, runtime, markers, policy);
  }
}
