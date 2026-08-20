using Whizbang.Core.Messaging;

namespace Whizbang.Core.Routing;

/// <summary>
/// Everything an inbox routing strategy may consult when computing this service's
/// subscription set (topology arc phase 3 seam). Carries the service identity, the
/// declared domain ownership, and the compile-time handled-message enumeration so
/// kind-aware/namespace-aware strategies can subscribe per contract namespace without
/// reflection.
/// </summary>
/// <param name="ServiceName">Name of this service (used for subscription naming).</param>
/// <param name="OwnedDomains">Domains this service owns (from <c>RoutingOptions.OwnDomains</c>);
/// lowercase-invariant namespace patterns.</param>
/// <param name="HandledMessages">The receptor-handled message enumeration from
/// <see cref="IReceptorRegistryQuery.GetHandledMessages"/>; empty when no registry is
/// resolvable (strategies must treat empty as "no metadata", not "handles nothing").</param>
/// <docs>fundamentals/dispatcher/routing#inbox-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:InboxSubscriptionContext_CarriesAllComponentsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderTests.cs:BuildInboxDestinations_RegistryResolvable_PassesHandledMessagesToContextAsync</tests>
public sealed record InboxSubscriptionContext(
  string ServiceName,
  IReadOnlySet<string> OwnedDomains,
  IReadOnlyList<HandledMessageInfo> HandledMessages
) {
  /// <summary>
  /// The event namespaces this service consumes (perspective-consumed + event-receptor +
  /// manual subscriptions, as enumerated by <see cref="EventSubscriptionDiscovery"/> — reused,
  /// not duplicated, from the generated <see cref="IEventNamespaceRegistry"/> sources). The
  /// composite/raw-carry routing surface (topology arc phase 5): receptor enumeration alone
  /// misses perspective-consumed events, so strategies computing "namespaces this service
  /// consumes ANY constituent from" union this with <see cref="HandledMessages"/>. Defaults to
  /// empty so pre-phase-5 construction sites keep compiling with unchanged behavior.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#inbox-routing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:ComputeConsumedNamespaces_UnionsHandledAndConsumedEventNamespacesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:InboxSubscriptionContext_ConsumedEventNamespaces_DefaultsToEmptyAsync</tests>
  public IReadOnlySet<string> ConsumedEventNamespaces { get; init; } =
    System.Collections.Frozen.FrozenSet<string>.Empty;
}
