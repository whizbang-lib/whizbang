namespace Whizbang.Core.Routing;

/// <summary>
/// Each domain has its own inbox topic.
/// JDNext-style - explicit inbox per domain.
/// </summary>
/// <docs>core-concepts/routing#domain-topic-inbox</docs>
public sealed class DomainTopicInboxStrategy : IInboxRoutingStrategy {
  private readonly string _suffix;

  /// <summary>
  /// Creates a domain topic inbox strategy with default suffix.
  /// </summary>
  public DomainTopicInboxStrategy()
      : this(".inbox") { }

  /// <summary>
  /// Creates a domain topic inbox strategy with custom suffix.
  /// </summary>
  /// <param name="suffix">The suffix to append to domain names (e.g., ".inbox", ".in").</param>
  public DomainTopicInboxStrategy(string suffix) {
    _suffix = suffix ?? throw new ArgumentNullException(nameof(suffix));
  }

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
}
