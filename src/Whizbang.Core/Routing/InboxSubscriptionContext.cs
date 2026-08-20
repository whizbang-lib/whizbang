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
);
