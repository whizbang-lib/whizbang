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
/// <docs>fundamentals/dispatcher/routing#routing-options</docs>
public sealed class RoutingOptions {
  private readonly HashSet<string> _ownedDomains = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _subscribedNamespaces = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _absorbedNamespaces = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _commandNamespacesToInbox = new(StringComparer.OrdinalIgnoreCase);
  private bool _routeAllCommandNamespacesToInbox;
  private bool _retireSharedInbox;

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
  /// Gets the event namespaces this service <b>absorbs</b>: every event on these topics is persisted to the
  /// local event store even when no receptor or perspective currently consumes it, so a later-added
  /// perspective/feature can rebuild from data that was already captured. See <see cref="AbsorbNamespaces"/>.
  /// </summary>
  public IReadOnlySet<string> AbsorbedNamespaces => _absorbedNamespaces;

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

    _ownedDomains.UnionWith(
        namespaces.Where(ns => !string.IsNullOrWhiteSpace(ns)).Select(ns => ns.ToLowerInvariant()));

    return this;
  }

  /// <summary>
  /// Declares command namespace ownership using a type from that namespace.
  /// The namespace is extracted from the type at runtime.
  /// </summary>
  /// <typeparam name="T">Any type from the command namespace to own.</typeparam>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="InvalidOperationException">Thrown when the type has no namespace.</exception>
  /// <example>
  /// opts.OwnNamespaceOf&lt;CreateUserCommand&gt;(); // Owns "myapp.users.commands"
  /// </example>
  /// <docs>fundamentals/dispatcher/routing#own-namespace-of</docs>
  /// <tests>Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:OwnNamespaceOf</tests>
  public RoutingOptions OwnNamespaceOf<T>() {
    var ns = typeof(T).Namespace
      ?? throw new InvalidOperationException($"Type {typeof(T).Name} has no namespace");
    return OwnDomains(ns);
  }

  /// <summary>
  /// Subscribes to the audit topic and enables the built-in audit perspective.
  /// The perspective materializes <see cref="SystemEvents.EventAudited"/> into
  /// <see cref="SystemEvents.Audit.AuditEventModel"/> automatically.
  /// </summary>
  /// <param name="autoGeneratePerspective">
  /// When <c>true</c> (default), Whizbang's built-in <see cref="SystemEvents.Audit.AuditEventProjection"/>
  /// is used. Set to <c>false</c> to provide a custom perspective for EventAudited.
  /// </param>
  /// <returns>This options instance for chaining.</returns>
  /// <docs>fundamentals/events/system-events#subscribe-to-audit</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:SubscribeToAudit_EnablesAuditPerspectiveByDefaultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:SubscribeToAudit_ReturnsSelfForChainingAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:SubscribeToAudit_CanChainWithOwnDomainsAsync</tests>
  public RoutingOptions SubscribeToAudit(bool autoGeneratePerspective = true) {
    _subscribedNamespaces.Add(SystemEvents.AuditingEventStoreDecorator.AUDIT_TOPIC_DESTINATION);
    AuditPerspectiveEnabled = autoGeneratePerspective;
    return this;
  }

  /// <summary>
  /// Gets whether the built-in audit perspective is enabled via <see cref="SubscribeToAudit"/>.
  /// </summary>
  public bool AuditPerspectiveEnabled { get; private set; }

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

    _subscribedNamespaces.UnionWith(
        namespaces.Where(ns => !string.IsNullOrWhiteSpace(ns)).Select(ns => ns.ToLowerInvariant()));

    return this;
  }

  /// <summary>
  /// Subscribes to an event namespace using a type from that namespace.
  /// The namespace is extracted from the type at runtime.
  /// </summary>
  /// <typeparam name="T">Any type from the event namespace to subscribe to.</typeparam>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="InvalidOperationException">Thrown when the type has no namespace.</exception>
  /// <example>
  /// opts.SubscribeToNamespaceOf&lt;OrderCreatedEvent&gt;(); // Subscribes to "myapp.orders.events"
  /// </example>
  /// <docs>fundamentals/dispatcher/routing#subscribe-to-namespace-of</docs>
  /// <tests>Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:SubscribeToNamespaceOf</tests>
  public RoutingOptions SubscribeToNamespaceOf<T>() {
    var ns = typeof(T).Namespace
      ?? throw new InvalidOperationException($"Type {typeof(T).Name} has no namespace");
    return SubscribeTo(ns);
  }

  /// <summary>
  /// <b>Absorbs</b> every event on the given topic/namespace into this service's local event store, even when
  /// no receptor or perspective consumes the type. Normally an inbound event with no local consumer is dropped
  /// at the transport receive edge (never stored), so a perspective/feature added later cannot rebuild from it.
  /// Marking a namespace absorbed keeps those events: the subscription binding is created and the receive
  /// discard gates no longer drop unconsumed types on this namespace, so each lands in the inbox and is
  /// persisted (perspective materialization is still gated on there being a perspective — absorbed-only events
  /// simply sit in the store until something rebuilds from them). Namespace-scoped, additive to auto-discovery.
  /// </summary>
  /// <param name="namespaces">Event namespaces to absorb (e.g. <c>"myapp.contracts.job"</c>). Case-insensitive.</param>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="namespaces"/> is null.</exception>
  public RoutingOptions AbsorbNamespaces(params string[] namespaces) {
    ArgumentNullException.ThrowIfNull(namespaces);

    _absorbedNamespaces.UnionWith(
        namespaces.Where(ns => !string.IsNullOrWhiteSpace(ns)).Select(ns => ns.ToLowerInvariant()));

    return this;
  }

  /// <summary>
  /// Gets the contract namespaces whose COMMANDS are flipped to their per-namespace inbox
  /// entity (<c>inbox.&lt;ns&gt;</c>) by <see cref="NamespaceOutboxStrategy"/> — the topology
  /// arc phase-6 publisher flip, migrated namespace-at-a-time. Lowercase-invariant.
  /// Empty (and <see cref="AllCommandNamespacesRouteToInbox"/> false) = no namespace flipped.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  public IReadOnlySet<string> CommandNamespacesToInbox => _commandNamespacesToInbox;

  /// <summary>
  /// Gets whether EVERY command contract namespace routes to its per-namespace inbox —
  /// set by <see cref="RouteAllCommandNamespacesToInbox"/>, the end-state of the
  /// namespace-at-a-time migration.
  /// </summary>
  public bool AllCommandNamespacesRouteToInbox => _routeAllCommandNamespacesToInbox;

  /// <summary>
  /// FLIPS one command contract namespace to its per-namespace inbox entity
  /// (<c>inbox.&lt;ns&gt;</c>): once flipped, <see cref="NamespaceOutboxStrategy"/> (and the
  /// publish-time seam in <c>TransportPublishStrategy</c>) route that namespace's commands to
  /// the entity the handling service dark-provisioned in phase 5, instead of the legacy
  /// shared inbox. Repeatable — flip one namespace per call, migrate namespace-at-a-time.
  /// ROLLBACK = remove the call (or the configuration entry
  /// <c>Whizbang:Routing:CommandNamespacesToInbox</c>): unflipped namespaces route
  /// byte-identically to the legacy shared inbox.
  /// </summary>
  /// <param name="contractNamespace">The command contract namespace to flip
  /// (case-insensitive; stored lowercase-invariant).</param>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="ArgumentException">Thrown when the namespace is null or whitespace.</exception>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceOutboxStrategyTests.cs:RouteCommandNamespaceToInbox_IsRepeatableAndLowercasesAsync</tests>
  public RoutingOptions RouteCommandNamespaceToInbox(string contractNamespace) {
    ArgumentException.ThrowIfNullOrWhiteSpace(contractNamespace);
    _commandNamespacesToInbox.Add(contractNamespace.ToLowerInvariant());
    return this;
  }

  /// <summary>
  /// Flips EVERY command contract namespace to its per-namespace inbox — the end-state of
  /// the migration (also bindable from configuration as the <c>"*"</c> entry in
  /// <c>Whizbang:Routing:CommandNamespacesToInbox</c>). Framework-reserved namespaces
  /// (<c>whizbang.core.*</c>) are never given per-namespace inboxes — under a full flip they
  /// route to the system broadcast inbox instead (see <see cref="NamespaceOutboxStrategy"/>).
  /// </summary>
  /// <returns>This options instance for chaining.</returns>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceOutboxStrategyTests.cs:RouteAllCommandNamespacesToInbox_FlipsEveryNamespaceAsync</tests>
  public RoutingOptions RouteAllCommandNamespacesToInbox() {
    _routeAllCommandNamespacesToInbox = true;
    return this;
  }

  /// <summary>
  /// True when commands in <paramref name="contractNamespace"/> are flipped to their
  /// per-namespace inbox (explicitly or via <see cref="RouteAllCommandNamespacesToInbox"/>).
  /// Consulted LIVE by <see cref="NamespaceOutboxStrategy"/> on every routing decision, so
  /// configuration-bound flips and rollbacks need no strategy re-registration.
  /// </summary>
  /// <param name="contractNamespace">The contract namespace (case-insensitive).</param>
  /// <returns>True when flipped.</returns>
  public bool IsCommandNamespaceRoutedToInbox(string contractNamespace) {
    return _routeAllCommandNamespacesToInbox
      || (!string.IsNullOrWhiteSpace(contractNamespace) && _commandNamespacesToInbox.Contains(contractNamespace));
  }

  /// <summary>
  /// Gets whether the legacy shared inbox is RETIRED (topology arc phase 7, spec migration
  /// step 3): the transitional shared-inbox subscription is dropped, the manifest and both
  /// provisioners exclude the shared entity, and the command topology is exactly
  /// per-namespace inboxes + the one system broadcast inbox. Set via
  /// <see cref="RetireSharedInbox"/> or configuration
  /// (<c>Whizbang:Routing:RetireSharedInbox</c>).
  /// </summary>
  public bool SharedInboxRetired => _retireSharedInbox;

  /// <summary>
  /// RETIRES the legacy shared inbox — the explicit opt-in completing the
  /// per-namespace-command-inbox migration (topology arc phase 7). Under retirement
  /// <see cref="NamespaceInboxStrategy"/> drops its transitional shared-inbox subscription,
  /// so the subscription set becomes exactly per-namespace inboxes + the system broadcast
  /// inbox; the <see cref="TopologyManifest"/> (and therefore both transports' provisioners)
  /// carries zero references to the shared entity.
  /// </summary>
  /// <remarks>
  /// Valid ONLY once EVERY command namespace is flipped
  /// (<see cref="RouteAllCommandNamespacesToInbox"/> / the <c>"*"</c> entry in
  /// <c>Whizbang:Routing:CommandNamespacesToInbox</c>) — a namespace still publishing to the
  /// shared inbox after the subscription is dropped would be SILENT LOSS, so startup
  /// validation throws instead (see the WithRouting options factory). Also bindable from
  /// configuration as <c>Whizbang:Routing:RetireSharedInbox</c> (<c>true</c>/<c>false</c>);
  /// rollback = remove the call/entry (the transitional shared subscription returns).
  /// </remarks>
  /// <returns>This options instance for chaining.</returns>
  /// <docs>fundamentals/dispatcher/routing#namespace-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/SharedInboxRetirementTests.cs:RetireSharedInbox_DefaultsOff_FluentSetsAndChainsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/SharedInboxRetirementTests.cs:WithRouting_RetirementWithoutFullFlip_ThrowsClearStartupErrorAsync</tests>
  public RoutingOptions RetireSharedInbox() {
    _retireSharedInbox = true;
    return this;
  }

  /// <summary>
  /// The retirement guard (topology arc phase 7): throws when <see cref="RetireSharedInbox"/>
  /// is set while the command-namespace flip is incomplete. Called by the WithRouting options
  /// factory at first resolution (startup validation, after configuration binding) and by
  /// <see cref="NamespaceInboxStrategy.GetSubscriptions"/> as defense in depth for manually
  /// constructed options. The error names the unflipped state so the operator can see how far
  /// the migration got.
  /// </summary>
  /// <exception cref="InvalidOperationException">Thrown when retirement is enabled without
  /// <see cref="RouteAllCommandNamespacesToInbox"/>.</exception>
  internal void ThrowIfRetirementIncomplete() {
    if (!_retireSharedInbox || _routeAllCommandNamespacesToInbox) {
      return;
    }

    var explicitFlips = _commandNamespacesToInbox.Count == 0
      ? "none"
      : string.Join(", ", _commandNamespacesToInbox.Order(StringComparer.Ordinal));
    throw new InvalidOperationException(
      "RetireSharedInbox is set, but the command-namespace flip is incomplete: "
      + "RouteAllCommandNamespacesToInbox (configuration: the \"*\" entry in "
      + "Whizbang:Routing:CommandNamespacesToInbox) is not set. Retiring the shared inbox while "
      + "any namespace still publishes to it would leave those commands on a topic with no "
      + "subscriber — silent loss. Unflipped state: AllCommandNamespacesRouteToInbox=false; "
      + $"explicitly flipped namespaces: {explicitFlips}. "
      + "Complete the flip (or remove RetireSharedInbox) before retiring.");
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
  /// <remarks>
  /// The default topic matches <see cref="SharedTopicInboxStrategy"/>'s parameterless
  /// constructor and <see cref="SharedTopicOutboxStrategy.DefaultInboxTopic"/> ("inbox").
  /// It was previously "whizbang.inbox" here — an inconsistency that would silently split
  /// publisher and subscriber onto different topics for anyone relying on this builder's
  /// default (fixed in the topology arc, phase 3; locked by tests).
  /// </remarks>
  /// <param name="topic">Topic name. Default: "inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:Inbox_UseSharedTopic_DefaultTopic_IsInboxMatchingStrategyDefaultAsync</tests>
  public RoutingOptions UseSharedTopic(string topic = "inbox") {
    _parent.SetInboxStrategy(new SharedTopicInboxStrategy(topic));
    return _parent;
  }

  /// <summary>
  /// Uses per-namespace command inboxes (topology arc phase 5): one <c>inbox.&lt;ns&gt;</c>
  /// subscription per handled command contract namespace, plus the system broadcast inbox
  /// (<c>inbox.whizbang</c>), plus — transitionally, until
  /// <see cref="RoutingOptions.RetireSharedInbox"/> — today's shared-inbox subscription
  /// unchanged. The strategy is bound to these options, so the retirement switch (code or
  /// configuration) is consulted LIVE. NOT the default; the default-flip decision rides the
  /// publisher flip (phase 6/7).
  /// </summary>
  /// <param name="sharedInboxTopic">Topic for the transitional shared-inbox subscription.
  /// Default: "inbox" (must match the publishers' shared inbox until the flip).</param>
  /// <returns>The parent options for chaining.</returns>
  /// <docs>fundamentals/dispatcher/routing#namespace-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:Inbox_UseNamespaceInboxes_SetsNamespaceStrategyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceInboxStrategyTests.cs:Inbox_UseNamespaceInboxes_BindsParentOptionsSoRetirementIsConsultedLiveAsync</tests>
  public RoutingOptions UseNamespaceInboxes(string sharedInboxTopic = "inbox") {
    _parent.SetInboxStrategy(new NamespaceInboxStrategy(_parent, sharedInboxTopic));
    return _parent;
  }

  /// <summary>
  /// Uses domain-specific inbox topics (one inbox topic per domain).
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
  /// Uses namespace routing (topology arc phase 6, opt-in): events publish to domain topics
  /// exactly as <see cref="DomainTopicOutboxStrategy"/> (today's default); commands publish
  /// to their per-namespace inbox (<c>inbox.&lt;contract-namespace&gt;</c>) once their
  /// namespace is FLIPPED via <see cref="RoutingOptions.RouteCommandNamespaceToInbox"/> /
  /// <see cref="RoutingOptions.RouteAllCommandNamespacesToInbox"/> (or configuration
  /// <c>Whizbang:Routing:CommandNamespacesToInbox</c>) — unflipped namespaces keep routing
  /// byte-identically to the legacy shared inbox; System traffic publishes to the system
  /// broadcast inbox. NOT the default; pair with
  /// <see cref="InboxRoutingOptionsBuilder.UseNamespaceInboxes"/> on the consuming services
  /// (the flip requires phase-5 dark-provisioned entities to exist).
  /// </summary>
  /// <param name="sharedInboxTopic">The legacy shared inbox topic unflipped commands keep
  /// using. Default: "inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceOutboxStrategyTests.cs:Outbox_UseNamespaceRouting_SetsNamespaceStrategyAsync</tests>
  public RoutingOptions UseNamespaceRouting(string sharedInboxTopic = "inbox") {
    _parent.SetOutboxStrategy(new NamespaceOutboxStrategy(_parent, sharedInboxTopic));
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
