namespace Whizbang.Core.Routing;

/// <summary>
/// Configuration options for message routing strategies.
/// Supports fluent API for configuring domain ownership and inbox/outbox routing.
/// </summary>
/// <remarks>
/// <para>
/// Key configuration methods:
/// - <see cref="OwnDomains"/>: Command namespaces this service handles (the broker-side filter on
///   a shared inbox; on the default per-namespace topology the entity itself IS the filter)
/// - <see cref="SubscribeTo"/>: Event namespaces to subscribe to (manual override, adds to auto-discovered)
/// </para>
/// <para>
/// Event subscriptions are typically auto-discovered from registered perspectives and receptors.
/// Use SubscribeTo() for additional manual subscriptions beyond auto-discovery.
/// </para>
/// <para>
/// <b>Defaults.</b> Constructed options describe the per-namespace command topology: commands are
/// published to, and received on, one <c>inbox.&lt;contract-namespace&gt;</c> entity per namespace
/// (plus the system broadcast inbox), and the legacy catch-all <c>inbox</c> is retired. Selecting a
/// legacy or custom strategy on either side restores the pre-migration state so that topology stays
/// coherent — see <see cref="SelectLegacyTopology"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#routing-options</docs>
public sealed class RoutingOptions {
  private readonly HashSet<string> _ownedDomains = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _subscribedNamespaces = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _absorbedNamespaces = new(StringComparer.OrdinalIgnoreCase);
  private readonly HashSet<string> _commandNamespacesToInbox = new(StringComparer.OrdinalIgnoreCase);

  // Tri-state: null = "the consumer never said", so the DEFAULT rule below decides. Explicit
  // true/false always win, whatever order the fluent calls arrive in.
  private bool? _routeAllCommandNamespacesToInbox;
  private bool? _retireSharedInbox;
  private bool _legacyTopologySelected;

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
  /// Gets or sets the inbox routing strategy — where this service receives commands.
  /// Default: <see cref="NamespaceInboxStrategy"/> (one <c>inbox.&lt;contract-namespace&gt;</c>
  /// entity per handled command namespace + the system broadcast inbox, no catch-all).
  /// </summary>
  public IInboxRoutingStrategy InboxStrategy { get; private set; }

  /// <summary>
  /// Gets or sets the outbox routing strategy — where this service publishes.
  /// Default: <see cref="NamespaceOutboxStrategy"/> (events to domain topics exactly as
  /// <see cref="DomainTopicOutboxStrategy"/>; commands to their per-namespace inbox).
  /// </summary>
  public IOutboxRoutingStrategy OutboxStrategy { get; private set; }

  /// <summary>
  /// Gets the inbox options for fluent configuration.
  /// </summary>
  public InboxRoutingOptionsBuilder Inbox { get; }

  /// <summary>
  /// Gets the outbox options for fluent configuration.
  /// </summary>
  public OutboxRoutingOptionsBuilder Outbox { get; }

  /// <summary>
  /// Creates routing options on the DEFAULT per-namespace command topology: per-namespace
  /// inboxes on both sides, every command namespace flipped, and the legacy catch-all
  /// <c>inbox</c> retired. Both strategies are bound to THIS instance, so the flip set and the
  /// retirement switch are consulted live (configuration binding needs no re-registration).
  /// </summary>
  public RoutingOptions() {
    Inbox = new InboxRoutingOptionsBuilder(this);
    Outbox = new OutboxRoutingOptionsBuilder(this);
    InboxStrategy = new NamespaceInboxStrategy(this);
    OutboxStrategy = new NamespaceOutboxStrategy(this);
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
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:OwnNamespaceOf</tests>
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
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:SubscribeToNamespaceOf</tests>
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
  /// Gets the contract namespaces EXPLICITLY named as flipped to their per-namespace inbox
  /// entity (<c>inbox.&lt;ns&gt;</c>) by <see cref="NamespaceOutboxStrategy"/> — the
  /// publisher flip, migrated namespace-at-a-time. Lowercase-invariant.
  /// </summary>
  /// <remarks>
  /// This set is only half the answer: consult <see cref="IsCommandNamespaceRoutedToInbox"/>, which
  /// also honors <see cref="AllCommandNamespacesRouteToInbox"/>. An EMPTY set is the normal
  /// default state — every namespace is flipped without naming any.
  /// </remarks>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  public IReadOnlySet<string> CommandNamespacesToInbox => _commandNamespacesToInbox;

  /// <summary>
  /// Gets whether EVERY command contract namespace routes to its per-namespace inbox — the
  /// end-state of the namespace-at-a-time migration, and the DEFAULT.
  /// </summary>
  /// <remarks>
  /// Resolution order: an explicit <see cref="RouteAllCommandNamespacesToInbox"/> /
  /// <see cref="RouteNoCommandNamespacesToInbox"/> (or the matching configuration key) always
  /// wins; naming a single namespace via <see cref="RouteCommandNamespaceToInbox"/> steps the
  /// default aside (that API IS the migrate-one-at-a-time statement); otherwise the answer is
  /// "every namespace", unless the consumer explicitly selected a legacy/custom routing
  /// strategy, in which case the pre-migration state is restored so their topology stays
  /// coherent.
  /// </remarks>
  public bool AllCommandNamespacesRouteToInbox =>
    _routeAllCommandNamespacesToInbox ?? !_legacyTopologySelected;

  /// <summary>
  /// FLIPS one command contract namespace to its per-namespace inbox entity
  /// (<c>inbox.&lt;ns&gt;</c>): <see cref="NamespaceOutboxStrategy"/> (and the publish-time seam
  /// in <c>TransportPublishStrategy</c>) route that namespace's commands to the entity the
  /// handling service provisioned, instead of the legacy shared inbox. Repeatable — flip one
  /// namespace per call, migrate namespace-at-a-time.
  /// </summary>
  /// <remarks>
  /// Calling this NARROWS the default: every namespace is flipped out of the box, so naming one
  /// is the statement "I am managing the flip set myself" — the unnamed namespaces then keep
  /// routing byte-identically to the legacy shared inbox, and the shared inbox stays subscribed
  /// (retirement follows the flip). ROLLBACK = remove the call, or the configuration entry
  /// <c>Whizbang:Routing:CommandNamespacesToInbox</c>. To roll the publisher side back
  /// completely, use <see cref="RouteNoCommandNamespacesToInbox"/>.
  /// </remarks>
  /// <param name="contractNamespace">The command contract namespace to flip
  /// (case-insensitive; stored lowercase-invariant).</param>
  /// <returns>This options instance for chaining.</returns>
  /// <exception cref="ArgumentException">Thrown when the namespace is null or whitespace.</exception>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/NamespaceOutboxStrategyTests.cs:RouteCommandNamespaceToInbox_IsRepeatableAndLowercasesAsync</tests>
  public RoutingOptions RouteCommandNamespaceToInbox(string contractNamespace) {
    ArgumentException.ThrowIfNullOrWhiteSpace(contractNamespace);
    _commandNamespacesToInbox.Add(contractNamespace.ToLowerInvariant());
    // Naming ONE namespace is the migrate-one-at-a-time statement: the consumer is managing the
    // flip set by hand, so the all-namespaces DEFAULT steps aside. An explicit
    // RouteAllCommandNamespacesToInbox (before or after) still wins — only the default yields.
    _routeAllCommandNamespacesToInbox ??= false;
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
  /// The explicit inverse of <see cref="RouteAllCommandNamespacesToInbox"/>: NO command
  /// namespace is flipped except those named individually via
  /// <see cref="RouteCommandNamespaceToInbox"/> — the pre-migration publisher state. Since the
  /// full flip is now the default, this (or the configuration key
  /// <c>Whizbang:Routing:RouteAllCommandNamespacesToInbox</c> set to <c>false</c>) is how a
  /// consumer rolls the publisher side all the way back without changing strategies.
  /// </summary>
  /// <returns>This options instance for chaining.</returns>
  /// <docs>fundamentals/dispatcher/routing#namespace-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:MidMigration_ExplicitInverses_RollBackToThePreMigrationStateAsync</tests>
  public RoutingOptions RouteNoCommandNamespacesToInbox() {
    _routeAllCommandNamespacesToInbox = false;
    return this;
  }

  /// <summary>
  /// True when commands in <paramref name="contractNamespace"/> are flipped to their
  /// per-namespace inbox — by <see cref="AllCommandNamespacesRouteToInbox"/> (the default) or by
  /// an explicit <see cref="RouteCommandNamespaceToInbox"/> entry. Consulted LIVE by
  /// <see cref="NamespaceOutboxStrategy"/> on every routing decision, so configuration-bound
  /// flips and rollbacks need no strategy re-registration.
  /// </summary>
  /// <param name="contractNamespace">The contract namespace (case-insensitive).</param>
  /// <returns>True when flipped.</returns>
  public bool IsCommandNamespaceRoutedToInbox(string contractNamespace) {
    return AllCommandNamespacesRouteToInbox
      || (!string.IsNullOrWhiteSpace(contractNamespace) && _commandNamespacesToInbox.Contains(contractNamespace));
  }

  /// <summary>
  /// Gets whether the legacy shared inbox is RETIRED: the transitional shared-inbox
  /// subscription is dropped, the <see cref="TopologyManifest"/> and both provisioners exclude
  /// the shared entity, and the command topology is exactly per-namespace inboxes + the one
  /// system broadcast inbox. This is the DEFAULT.
  /// </summary>
  /// <remarks>
  /// Absent an explicit <see cref="RetireSharedInbox"/> / <see cref="KeepSharedInbox"/> (or the
  /// configuration key <c>Whizbang:Routing:RetireSharedInbox</c>), retirement FOLLOWS
  /// <see cref="AllCommandNamespacesRouteToInbox"/>. That coupling is what keeps the default
  /// internally consistent: retiring the catch-all is only ever valid under a full flip, so a
  /// partial flip (mid-migration, or a legacy strategy selection) can never leave retirement
  /// stranded in the combination <see cref="ThrowIfRetirementIncomplete"/> rejects.
  /// </remarks>
  public bool SharedInboxRetired => _retireSharedInbox ?? AllCommandNamespacesRouteToInbox;

  /// <summary>
  /// The control class's delivery semantics (topology arc phase 9). Consulted LIVE by
  /// <see cref="NamespaceInboxStrategy"/>, so a configuration-driven change to the sessionless /
  /// non-durable migration steps needs no strategy re-registration — the same treatment
  /// <see cref="SharedInboxRetired"/> gets. Replaced at first options resolution with the
  /// DI-bound instance, so there is exactly ONE control-class options object per host.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#control-class</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/ControlClassSubscriptionSplitTests.cs</tests>
  public ControlClassOptions ControlClass { get; set; } = new();

  /// <summary>
  /// RETIRES the legacy shared inbox, completing the per-namespace-command-inbox migration —
  /// the DEFAULT state, so this is only needed to re-assert retirement after a legacy strategy
  /// selection or a <see cref="KeepSharedInbox"/> call. Under retirement
  /// <see cref="NamespaceInboxStrategy"/> drops its transitional shared-inbox subscription,
  /// so the subscription set becomes exactly per-namespace inboxes + the system broadcast
  /// inbox; the <see cref="TopologyManifest"/> (and therefore both transports' provisioners)
  /// carries zero references to the shared entity.
  /// </summary>
  /// <remarks>
  /// Valid ONLY once EVERY command namespace is flipped
  /// (<see cref="AllCommandNamespacesRouteToInbox"/>) — a namespace still publishing to the
  /// shared inbox after the subscription is dropped would be SILENT LOSS, so startup
  /// validation throws instead (see the WithRouting options factory). Because the implicit
  /// default FOLLOWS the flip, that combination is only reachable by asserting both halves
  /// explicitly. Also bindable from configuration as
  /// <c>Whizbang:Routing:RetireSharedInbox</c> (<c>true</c>/<c>false</c>); rollback is
  /// <see cref="KeepSharedInbox"/> or the <c>false</c> entry.
  /// </remarks>
  /// <returns>This options instance for chaining.</returns>
  /// <docs>fundamentals/dispatcher/routing#namespace-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/SharedInboxRetirementTests.cs:RetireSharedInbox_DefaultsOn_AndBothFluentSwitchesChainAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/SharedInboxRetirementTests.cs:WithRouting_RetirementWithoutFullFlip_ThrowsClearStartupErrorAsync</tests>
  public RoutingOptions RetireSharedInbox() {
    _retireSharedInbox = true;
    return this;
  }

  /// <summary>
  /// The explicit inverse of <see cref="RetireSharedInbox"/>: KEEPS the transitional
  /// shared-inbox subscription (and therefore the shared entity in the
  /// <see cref="TopologyManifest"/>, and therefore in both transports' provisioning) while the
  /// per-namespace inboxes run alongside it. Since retirement is now the default, this (or the
  /// configuration key <c>Whizbang:Routing:RetireSharedInbox</c> set to <c>false</c>) is the
  /// dual-delivery rollback rung: publishers stay flipped, but a receiver that still needs the
  /// catch-all keeps it.
  /// </summary>
  /// <returns>This options instance for chaining.</returns>
  /// <docs>fundamentals/dispatcher/routing#namespace-inbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:Configuration_RetireSharedInboxFalse_KeepsTheTransitionalSubscriptionAsync</tests>
  public RoutingOptions KeepSharedInbox() {
    _retireSharedInbox = false;
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
    if (!SharedInboxRetired || AllCommandNamespacesRouteToInbox) {
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

  /// <summary>
  /// LEGACY COHERENCE: records that the consumer explicitly chose a routing strategy outside
  /// the per-namespace family (shared topic, domain topics, or a custom implementation). Such a
  /// strategy cannot be assumed to subscribe to per-namespace inboxes or to honor the publisher
  /// flip, so the migration DEFAULTS step aside and the pre-migration state is restored: the
  /// shared inbox keeps being named by the <see cref="TopologyManifest"/> and therefore keeps
  /// being provisioned by both transports. Without this, an existing consumer would upgrade into
  /// a host that silently stops creating the very entity it receives on.
  /// </summary>
  /// <remarks>
  /// Only the DEFAULT yields — an explicit <see cref="RouteAllCommandNamespacesToInbox"/> /
  /// <see cref="RetireSharedInbox"/> still wins in either call order, so a consumer running a
  /// legacy subscription while flipping publishers can still say exactly that.
  /// </remarks>
  internal void SelectLegacyTopology() {
    _legacyTopologySelected = true;
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
  /// Uses the LEGACY shared topic inbox strategy: all commands arrive on a single shared topic
  /// with broker-side filtering. Superseded by the default
  /// <see cref="UseNamespaceInboxes"/> topology; kept for consumers mid-migration.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Choosing this strategy also restores the PRE-MIGRATION defaults on these options (no
  /// namespace flipped, shared inbox kept) — see <see cref="RoutingOptions.SelectLegacyTopology"/>.
  /// Without that, the manifest would stop naming this very topic and the transports would stop
  /// provisioning it. Explicit <see cref="RoutingOptions.RouteAllCommandNamespacesToInbox"/> /
  /// <see cref="RoutingOptions.RetireSharedInbox"/> calls still win.
  /// </para>
  /// <para>
  /// The default topic matches <see cref="SharedTopicInboxStrategy"/>'s parameterless
  /// constructor and <see cref="SharedTopicOutboxStrategy.DefaultInboxTopic"/> ("inbox").
  /// It was previously "whizbang.inbox" here — an inconsistency that would silently split
  /// publisher and subscriber onto different topics for anyone relying on this builder's
  /// default (fixed in the topology arc, phase 3; locked by tests).
  /// </para>
  /// </remarks>
  /// <param name="topic">Topic name. Default: "inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingOptionsTests.cs:Inbox_UseSharedTopic_DefaultTopic_IsInboxMatchingStrategyDefaultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:ExplicitLegacyStrategies_AreByteIdenticalToTodaysSharedTopologyAsync</tests>
  public RoutingOptions UseSharedTopic(string topic = "inbox") {
    _parent.SelectLegacyTopology();
    _parent.SetInboxStrategy(new SharedTopicInboxStrategy(topic));
    return _parent;
  }

  /// <summary>
  /// Uses per-namespace command inboxes — THE DEFAULT, so calling this is only needed to name a
  /// non-standard transitional shared topic or to re-select the default after a legacy call.
  /// One <c>inbox.&lt;ns&gt;</c> subscription per handled command contract namespace, plus the
  /// system broadcast inbox (<c>inbox.whizbang</c>), plus — only while the shared inbox is NOT
  /// retired (mid-migration) — today's shared-inbox subscription unchanged. The strategy is
  /// bound to these options, so the retirement switch (code or configuration) is consulted LIVE.
  /// </summary>
  /// <param name="sharedInboxTopic">Topic for the transitional shared-inbox subscription, used
  /// only while retirement is off. Default: "inbox" (must match the publishers' shared inbox
  /// until the flip completes).</param>
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
  /// <remarks>Outside the per-namespace family, so it also restores the pre-migration flip and
  /// retirement defaults — see <see cref="RoutingOptions.SelectLegacyTopology"/>.</remarks>
  /// <param name="suffix">Suffix for domain topics. Default: ".inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:UseDomainTopics_EitherSide_ClearsFlipAndRetirementAsync</tests>
  public RoutingOptions UseDomainTopics(string suffix = ".inbox") {
    _parent.SelectLegacyTopology();
    _parent.SetInboxStrategy(new DomainTopicInboxStrategy(suffix));
    return _parent;
  }

  /// <summary>
  /// Uses a custom inbox routing strategy.
  /// </summary>
  /// <remarks>A custom strategy cannot be assumed to subscribe to per-namespace inboxes, so it
  /// also restores the pre-migration flip and retirement defaults — see
  /// <see cref="RoutingOptions.SelectLegacyTopology"/>. Re-assert
  /// <see cref="RoutingOptions.RouteAllCommandNamespacesToInbox"/> /
  /// <see cref="RoutingOptions.RetireSharedInbox"/> explicitly when the custom strategy does
  /// cover the per-namespace entities.</remarks>
  /// <param name="strategy">Custom strategy implementation.</param>
  /// <returns>The parent options for chaining.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:UseCustom_EitherSide_ClearsFlipAndRetirementAsync</tests>
  public RoutingOptions UseCustom(IInboxRoutingStrategy strategy) {
    ArgumentNullException.ThrowIfNull(strategy);
    _parent.SelectLegacyTopology();
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
  /// Uses domain-specific outbox topics: every message — commands included — publishes to its
  /// own namespace topic. Superseded by the default <see cref="UseNamespaceRouting"/>, whose
  /// EVENT routing is byte-identical to this strategy.
  /// </summary>
  /// <remarks>This strategy has no command-inbox seam at all, so it also restores the
  /// pre-migration flip and retirement defaults — a flipped set it cannot honor would be a lie.
  /// See <see cref="RoutingOptions.SelectLegacyTopology"/>.</remarks>
  /// <returns>The parent options for chaining.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:UseDomainTopics_EitherSide_ClearsFlipAndRetirementAsync</tests>
  public RoutingOptions UseDomainTopics() {
    _parent.SelectLegacyTopology();
    _parent.SetOutboxStrategy(new DomainTopicOutboxStrategy());
    return _parent;
  }

  /// <summary>
  /// Uses the LEGACY shared topic outbox strategy: commands route to one shared inbox topic
  /// with namespace-based routing keys; events route to namespace-specific topics. Superseded
  /// by the default <see cref="UseNamespaceRouting"/>; kept for consumers mid-migration.
  /// </summary>
  /// <param name="inboxTopic">The shared inbox topic name for commands. Default: "inbox".</param>
  /// <returns>The parent options for chaining.</returns>
  /// <remarks>
  /// <para>
  /// Choosing this strategy also restores the PRE-MIGRATION defaults on these options (no
  /// namespace flipped, shared inbox kept) so the whole topology stays coherent — see
  /// <see cref="RoutingOptions.SelectLegacyTopology"/>.
  /// </para>
  /// <para>
  /// Command flow: All commands → shared inbox topic → services filter by owned namespaces.
  /// Routing key format: "{namespace}.{typename}" (e.g., "myapp.users.commands.createtenantcommand").
  /// </para>
  /// <para>
  /// Event flow: Events → namespace-specific topics → services subscribe to namespaces they care about.
  /// Topic is the full namespace (e.g., "myapp.users.events"), routing key is the type name.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:ExplicitLegacyStrategies_AreByteIdenticalToTodaysSharedTopologyAsync</tests>
  public RoutingOptions UseSharedTopic(string inboxTopic = "inbox") {
    _parent.SelectLegacyTopology();
    _parent.SetOutboxStrategy(new SharedTopicOutboxStrategy(inboxTopic));
    return _parent;
  }

  /// <summary>
  /// Uses namespace routing — THE DEFAULT, so calling this is only needed to name a
  /// non-standard legacy shared topic or to re-select the default after a legacy call. Events
  /// publish to domain topics exactly as <see cref="DomainTopicOutboxStrategy"/>; commands
  /// publish to their per-namespace inbox (<c>inbox.&lt;contract-namespace&gt;</c>) while their
  /// namespace is FLIPPED (every namespace, by default; narrow it with
  /// <see cref="RoutingOptions.RouteCommandNamespaceToInbox"/> or configuration
  /// <c>Whizbang:Routing:CommandNamespacesToInbox</c>) — unflipped namespaces route
  /// byte-identically to the legacy shared inbox; System traffic publishes to the system
  /// broadcast inbox.
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
  /// <remarks>A custom strategy cannot be assumed to honor the publisher flip, so it also
  /// restores the pre-migration flip and retirement defaults — see
  /// <see cref="RoutingOptions.SelectLegacyTopology"/>. Re-assert
  /// <see cref="RoutingOptions.RouteAllCommandNamespacesToInbox"/> /
  /// <see cref="RoutingOptions.RetireSharedInbox"/> explicitly when it does.</remarks>
  /// <param name="strategy">Custom strategy implementation.</param>
  /// <returns>The parent options for chaining.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Routing/DefaultNamespaceTopologyTests.cs:UseCustom_EitherSide_ClearsFlipAndRetirementAsync</tests>
  public RoutingOptions UseCustom(IOutboxRoutingStrategy strategy) {
    ArgumentNullException.ThrowIfNull(strategy);
    _parent.SelectLegacyTopology();
    _parent.SetOutboxStrategy(strategy);
    return _parent;
  }
}
