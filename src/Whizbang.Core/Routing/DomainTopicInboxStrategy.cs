namespace Whizbang.Core.Routing;

/// <summary>
/// Each domain has its own inbox topic.
/// Domain-specific style - explicit inbox per domain.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#domain-topic-inbox</docs>
/// <remarks>
/// <para>
/// DEPRECATION NOTE (topology arc phase 7): scheduled for removal in v1.0, superseded by
/// <see cref="NamespaceInboxStrategy"/> — which subscribes one inbox per HANDLED contract
/// namespace (registry-derived, not just the primary owned domain) plus the system broadcast
/// inbox. No <c>[Obsolete]</c>: the repository escalates CS0618 to error, which would break
/// consumers building with warnings-as-errors.
/// </para>
/// <para>Creates a domain topic inbox strategy with custom suffix.</para>
/// </remarks>
/// <param name="suffix">The suffix to append to domain names (e.g., ".inbox", ".in").</param>
public sealed class DomainTopicInboxStrategy(string suffix) : IInboxRoutingStrategy {
  private readonly string _suffix = suffix ?? throw new ArgumentNullException(nameof(suffix));

  /// <summary>
  /// Creates a domain topic inbox strategy with default suffix.
  /// </summary>
  public DomainTopicInboxStrategy()
      : this(".inbox") { }

  /// <inheritdoc />
  public InboxSubscription GetSubscription(
    IReadOnlySet<string> ownedDomains,
    string serviceName,
    MessageKind kind
  ) {
    ArgumentNullException.ThrowIfNull(ownedDomains);

    // Get primary domain (first in set), fallback to service name if empty
    var primaryDomain = ownedDomains.FirstOrDefault() ?? serviceName;

    // Topic is domain + suffix (e.g., "orders.inbox")
    var topic = $"{primaryDomain}{_suffix}";

    // No filter needed - the topic itself IS the filter
    return new InboxSubscription(
      Topic: topic,
      FilterExpression: null,
      Metadata: null
    );
  }

  /// <summary>
  /// Explicit plural override (topology arc phase 3): the domain-topic strategy has exactly
  /// one subscription — the primary domain's inbox topic — so the plural surface returns
  /// that same single subscription. Overridden (rather than inherited from the default
  /// interface member) so the seam is exercised by the built-in strategies; bit-identical
  /// output is locked by tests.
  /// </summary>
  /// <param name="context">Service identity, owned domains, and handled-message enumeration
  /// (unused here — the topic derives from the primary owned domain).</param>
  /// <returns>A one-element list containing today's singular subscription.</returns>
  /// <exception cref="ArgumentNullException">Thrown when context is null.</exception>
  /// <docs>fundamentals/dispatcher/routing#domain-topic-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DomainTopicInboxStrategyTests.cs:GetSubscriptions_BitIdenticalToSingularAsync</tests>
  public IReadOnlyList<InboxSubscription> GetSubscriptions(InboxSubscriptionContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return [GetSubscription(context.OwnedDomains, context.ServiceName, MessageKind.Command)];
  }
}
