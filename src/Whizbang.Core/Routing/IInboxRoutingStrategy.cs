namespace Whizbang.Core.Routing;

/// <summary>
/// Determines where this service receives commands (inbox subscription).
/// </summary>
/// <docs>fundamentals/dispatcher/routing#inbox-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:Inbox_UseCustomStrategy_SetsCustomStrategyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:SharedTopicInboxStrategy_GetSubscription_ReturnsDefaultInboxTopicAsync</tests>
public interface IInboxRoutingStrategy {
  /// <summary>
  /// Gets the subscription configuration for receiving commands.
  /// </summary>
  /// <remarks>
  /// DEPRECATION NOTE (topology arc phase 7): scheduled for removal in v1.0, superseded by
  /// <see cref="NamespaceInboxStrategy"/> / <see cref="GetSubscriptions"/> — the plural,
  /// context-driven surface that supports strategies with more than one subscription (e.g.
  /// one inbox per handled contract namespace + the system broadcast inbox). This singular
  /// method remains for compatibility through the migration window (no <c>[Obsolete]</c>:
  /// the repository escalates CS0618 to error, so the attribute would break consumers
  /// building with warnings-as-errors); under
  /// <see cref="RoutingOptions.RetireSharedInbox"/> the namespace-inbox strategy's
  /// implementation throws rather than answer with the retired shared subscription.
  /// </remarks>
  /// <param name="ownedDomains">Domains this service owns (from OwnDomains() registration).</param>
  /// <param name="serviceName">Name of this service.</param>
  /// <param name="kind">Message kind of the inbox traffic (Command, Query, or System).</param>
  /// <returns>Subscription configuration with topic and filter.</returns>
  /// <exception cref="ArgumentNullException">Thrown when ownedDomains is null.</exception>
  InboxSubscription GetSubscription(
    IReadOnlySet<string> ownedDomains,
    string serviceName,
    MessageKind kind
  );

  /// <summary>
  /// Gets every subscription this service needs to receive its inbox traffic. The plural
  /// seam lets a strategy subscribe to multiple entities (e.g. one inbox topic per handled
  /// contract namespace plus the system broadcast inbox) instead of exactly one.
  /// </summary>
  /// <remarks>
  /// Default interface member wrapping today's singular
  /// <see cref="GetSubscription(IReadOnlySet{string}, string, MessageKind)"/> with
  /// <see cref="MessageKind.Command"/> in a one-element list — existing implementations
  /// keep working unchanged, with bit-identical topology.
  /// </remarks>
  /// <param name="context">Service identity, owned domains, and the handled-message
  /// enumeration.</param>
  /// <returns>All subscriptions to create; never null, at least one for built-in
  /// strategies.</returns>
  /// <exception cref="ArgumentNullException">Thrown when context is null.</exception>
  /// <docs>fundamentals/dispatcher/routing#inbox-routing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:SharedTopicInboxStrategy_GetSubscriptions_BitIdenticalToSingularAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/InboxRoutingStrategyTests.cs:CustomSingularOnlyStrategy_GetSubscriptions_DefaultWrapsSingularAsCommandAsync</tests>
  IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return [GetSubscription(context.OwnedDomains, context.ServiceName, MessageKind.Command)];
  }
}
