using Whizbang.Core.Routing;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One message type this service handles (has a receptor for), as enumerated by the
/// source-generated receptor registry. The routing seam consumes this to build
/// kind-aware/namespace-aware inbox subscriptions and topology manifests without
/// reflection.
/// </summary>
/// <param name="MessageTypeName">
/// Fully-qualified CLR type name in the C# display form the receptor registry stores —
/// dotted (nested types use <c>.</c>, not <c>+</c>), no assembly qualifier
/// (e.g. <c>"MyApp.Orders.Commands.CreateOrder"</c>).
/// </param>
/// <param name="ContractNamespace">
/// The message type's contract namespace, lowercase-invariant to match routing-key
/// conventions (<c>RoutingOptions.OwnDomains</c>, broker routing patterns) —
/// e.g. <c>"myapp.orders.commands"</c>. "Contract" disambiguates from a transport/broker
/// namespace.
/// </param>
/// <param name="Kind">
/// The message kind detected at compile time using <see cref="MessageKindDetector"/>
/// priority rules (attribute, framework system namespace, interface, namespace
/// convention, type-name suffix).
/// </param>
/// <docs>internals/receptor-registry-query</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:InboxSubscriptionContext_CarriesAllComponentsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/ReceptorRegistryQueryGeneratorTests.cs:Generator_WithCommandReceptor_EmitsHandledMessageWithCommandKindAsync</tests>
public sealed record HandledMessageInfo(
  string MessageTypeName,
  string ContractNamespace,
  MessageKind Kind
);
