namespace Whizbang.Core.Routing;

/// <summary>
/// Configuration options for message routing strategies.
/// Supports fluent API for configuring domain ownership and inbox/outbox routing.
/// </summary>
/// <docs>core-concepts/routing#routing-options</docs>
public sealed class RoutingOptions {
  private readonly HashSet<string> _ownedDomains = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Gets the domains owned by this service.
  /// Commands to owned domains are routed to this service's inbox.
  /// </summary>
  public IReadOnlySet<string> OwnedDomains => _ownedDomains;

  /// <summary>
  /// Gets or sets the inbox routing strategy.
  /// Determines where this service receives commands.
  /// Default: SharedTopicInboxStrategy (shared topic with broker-side filtering).
  /// </summary>
  public IInboxRoutingStrategy InboxStrategy { get; private set; } = new SharedTopicInboxStrategy();

  /// <summary>
  /// Gets or sets the outbox routing strategy.
  /// Determines where this service publishes events.
  /// Default: DomainTopicOutboxStrategy (domain-specific topics).
  /// </summary>
  public IOutboxRoutingStrategy OutboxStrategy { get; private set; } = new DomainTopicOutboxStrategy();

  /// <summary>
  /// Gets the inbox options for fluent configuration.
  /// </summary>
  public InboxRoutingOptionsBuilder Inbox { get; }

  /// <summary>
  /// Gets the outbox options for fluent configuration.
  /// </summary>
  public OutboxRoutingOptionsBuilder Outbox { get; }

  /// <summary>
  /// Creates a new instance of routing options with default strategies.
  /// </summary>
  public RoutingOptions() {
    Inbox = new InboxRoutingOptionsBuilder(this);
    Outbox = new OutboxRoutingOptionsBuilder(this);
  }

  /// <summary>
  /// Declares domains owned by this service.
  /// Commands to owned domains are routed to this service's inbox.
  /// </summary>
  /// <param name="domains">Domain names (case-insensitive).</param>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="ArgumentNullException">Thrown when domains is null.</exception>
  public RoutingOptions OwnDomains(params string[] domains) {
    ArgumentNullException.ThrowIfNull(domains);

    foreach (var domain in domains) {
      if (!string.IsNullOrWhiteSpace(domain)) {
        _ownedDomains.Add(domain);
      }
    }

    return this;
  }

  /// <summary>
  /// Configures inbox routing using an action.
  /// </summary>
  /// <param name="configure">Action to configure inbox options.</param>
  /// <returns>This options instance for chaining.</returns>
  public RoutingOptions ConfigureInbox(Action<InboxRoutingOptionsBuilder> configure) {
    ArgumentNullException.ThrowIfNull(configure);
    configure(Inbox);
    return this;
  }

  /// <summary>
  /// Configures outbox routing using an action.
  /// </summary>
  /// <param name="configure">Action to configure outbox options.</param>
  /// <returns>This options instance for chaining.</returns>
  public RoutingOptions ConfigureOutbox(Action<OutboxRoutingOptionsBuilder> configure) {
    ArgumentNullException.ThrowIfNull(configure);
    configure(Outbox);
    return this;
  }

  /// <summary>
  /// Sets the inbox routing strategy.
  /// </summary>
  internal void SetInboxStrategy(IInboxRoutingStrategy strategy) {
    InboxStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
  }

  /// <summary>
  /// Sets the outbox routing strategy.
  /// </summary>
  internal void SetOutboxStrategy(IOutboxRoutingStrategy strategy) {
    OutboxStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
  }
}

/// <summary>
/// Builder for configuring inbox routing strategy.
/// </summary>
public sealed class InboxRoutingOptionsBuilder {
  private readonly RoutingOptions _parent;

  internal InboxRoutingOptionsBuilder(RoutingOptions parent) {
    _parent = parent;
  }

  /// <summary>
  /// Uses shared topic inbox strategy (default).
  /// All commands route to a single shared topic with broker-side filtering.
  /// </summary>
  /// <param name="topic">Topic name. Default: "whizbang.inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  public RoutingOptions UseSharedTopic(string topic = "whizbang.inbox") {
    _parent.SetInboxStrategy(new SharedTopicInboxStrategy(topic));
    return _parent;
  }

  /// <summary>
  /// Uses domain-specific inbox topics (JDNext-style).
  /// Each domain has its own inbox topic.
  /// </summary>
  /// <param name="suffix">Suffix for domain topics. Default: ".inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  public RoutingOptions UseDomainTopics(string suffix = ".inbox") {
    _parent.SetInboxStrategy(new DomainTopicInboxStrategy(suffix));
    return _parent;
  }

  /// <summary>
  /// Uses a custom inbox routing strategy.
  /// </summary>
  /// <param name="strategy">Custom strategy implementation.</param>
  /// <returns>The parent options for chaining.</returns>
  public RoutingOptions UseCustom(IInboxRoutingStrategy strategy) {
    ArgumentNullException.ThrowIfNull(strategy);
    _parent.SetInboxStrategy(strategy);
    return _parent;
  }
}

/// <summary>
/// Builder for configuring outbox routing strategy.
/// </summary>
public sealed class OutboxRoutingOptionsBuilder {
  private readonly RoutingOptions _parent;

  internal OutboxRoutingOptionsBuilder(RoutingOptions parent) {
    _parent = parent;
  }

  /// <summary>
  /// Uses domain-specific outbox topics (default).
  /// Each domain publishes to its own topic.
  /// </summary>
  /// <returns>The parent options for chaining.</returns>
  public RoutingOptions UseDomainTopics() {
    _parent.SetOutboxStrategy(new DomainTopicOutboxStrategy());
    return _parent;
  }

  /// <summary>
  /// Uses shared topic outbox strategy.
  /// All events publish to a single shared topic with metadata.
  /// </summary>
  /// <param name="topic">Topic name. Default: "whizbang.events".</param>
  /// <returns>The parent options for chaining.</returns>
  public RoutingOptions UseSharedTopic(string topic = "whizbang.events") {
    _parent.SetOutboxStrategy(new SharedTopicOutboxStrategy(topic));
    return _parent;
  }

  /// <summary>
  /// Uses a custom outbox routing strategy.
  /// </summary>
  /// <param name="strategy">Custom strategy implementation.</param>
  /// <returns>The parent options for chaining.</returns>
  public RoutingOptions UseCustom(IOutboxRoutingStrategy strategy) {
    ArgumentNullException.ThrowIfNull(strategy);
    _parent.SetOutboxStrategy(strategy);
    return _parent;
  }
}
