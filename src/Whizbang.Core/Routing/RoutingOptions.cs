namespace Whizbang.Core.Routing;

/// <summary>
/// Configuration options for message routing strategies.
/// Supports fluent API for configuring domain ownership and inbox/outbox routing.
/// </summary>
/// <remarks>
/// <para>
/// Key configuration methods:
/// - <see cref="OwnDomains"/>: Command namespaces this service handles (filters on shared inbox)
/// - <see cref="SubscribeTo"/>: Event namespaces to subscribe to (manual override, adds to auto-discovered)
/// </para>
/// <para>
/// Event subscriptions are typically auto-discovered from registered perspectives and receptors.
/// Use SubscribeTo() for additional manual subscriptions beyond auto-discovery.
/// </para>
/// </remarks>
/// <docs>core-concepts/routing#routing-options</docs>
public sealed class RoutingOptions {
  private readonly HashSet<string> _ownedDomains = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _subscribedNamespaces = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Gets the command namespaces owned by this service.
  /// Commands matching these namespaces are filtered from the shared inbox to this service.
  /// </summary>
  /// <example>
  /// opts.OwnDomains("myapp.users.commands"); // This service handles user commands
  /// opts.OwnDomains("myapp.users.*"); // Wildcard: handles all myapp.users.* namespaces
  /// </example>
  public IReadOnlySet<string> OwnedDomains => _ownedDomains;

  /// <summary>
  /// Gets the event namespaces this service subscribes to (manual subscriptions).
  /// These are combined with auto-discovered subscriptions from perspectives/receptors.
  /// </summary>
  public IReadOnlySet<string> SubscribedNamespaces => _subscribedNamespaces;

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
  /// Declares command namespaces owned by this service.
  /// Commands matching these namespaces are filtered from the shared inbox to this service.
  /// </summary>
  /// <param name="namespaces">Command namespace patterns (case-insensitive).
  /// Use ".*" suffix for wildcards (e.g., "myapp.users.*" matches all myapp.users.* namespaces).</param>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="ArgumentNullException">Thrown when namespaces is null.</exception>
  /// <example>
  /// opts.OwnDomains("myapp.users.commands"); // Exact namespace
  /// opts.OwnDomains("myapp.users.*"); // Wildcard: all myapp.users.* namespaces
  /// opts.OwnDomains("myapp.users.commands", "myapp.users.queries"); // Multiple
  /// </example>
  public RoutingOptions OwnDomains(params string[] namespaces) {
    ArgumentNullException.ThrowIfNull(namespaces);

    foreach (var ns in namespaces) {
      if (!string.IsNullOrWhiteSpace(ns)) {
        _ownedDomains.Add(ns.ToLowerInvariant());
      }
    }

    return this;
  }

  /// <summary>
  /// Subscribes to event namespaces for receiving events from other services.
  /// These are combined with auto-discovered subscriptions from perspectives/receptors.
  /// </summary>
  /// <param name="namespaces">Event namespace patterns (case-insensitive).
  /// Use ".*" suffix for wildcards (e.g., "myapp.orders.*" matches all myapp.orders.* namespaces).</param>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="ArgumentNullException">Thrown when namespaces is null.</exception>
  /// <remarks>
  /// Event subscriptions are typically auto-discovered from registered perspectives and receptors.
  /// Use this method for additional subscriptions beyond auto-discovery, or to ensure
  /// subscriptions are created before perspective/receptor registration.
  /// </remarks>
  /// <example>
  /// opts.SubscribeTo("myapp.orders.events"); // Subscribe to order events
  /// opts.SubscribeTo("myapp.orders.*"); // Wildcard: all myapp.orders.* namespaces
  /// opts.SubscribeTo("myapp.orders.events", "myapp.payments.events"); // Multiple
  /// </example>
  public RoutingOptions SubscribeTo(params string[] namespaces) {
    ArgumentNullException.ThrowIfNull(namespaces);

    foreach (var ns in namespaces) {
      if (!string.IsNullOrWhiteSpace(ns)) {
        _subscribedNamespaces.Add(ns.ToLowerInvariant());
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
  /// Uses shared topic outbox strategy for namespace-based routing.
  /// Commands route to a shared inbox topic with namespace-based routing keys.
  /// Events route to namespace-specific topics for pub/sub.
  /// </summary>
  /// <param name="inboxTopic">The shared inbox topic name for commands. Default: "inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  /// <remarks>
  /// <para>
  /// Command flow: All commands → shared inbox topic → services filter by owned namespaces.
  /// Routing key format: "{namespace}.{typename}" (e.g., "myapp.users.commands.createtenantcommand").
  /// </para>
  /// <para>
  /// Event flow: Events → namespace-specific topics → services subscribe to namespaces they care about.
  /// Topic is the full namespace (e.g., "myapp.users.events"), routing key is the type name.
  /// </para>
  /// </remarks>
  public RoutingOptions UseSharedTopic(string inboxTopic = "inbox") {
    _parent.SetOutboxStrategy(new SharedTopicOutboxStrategy(inboxTopic));
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
