// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.DependencyInjection;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Routing;

/// <summary>
/// Builds transport destinations for subscribing to commands and events.
/// Uses auto-discovered event namespaces combined with manual routing configuration.
/// </summary>
/// <remarks>
/// <para>
/// This builder integrates:
/// - InboxRoutingStrategy: Determines command subscription (topic + filter)
/// - EventSubscriptionDiscovery: Discovers event namespaces from perspectives/receptors
/// - RoutingOptions: Contains manual subscriptions and owned domains
/// </para>
/// <para>
/// Use this at transport startup to configure TransportConsumerOptions with
/// appropriate subscriptions for both commands (inbox) and events (namespace topics).
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#transport-subscription-builder</docs>
public sealed class TransportSubscriptionBuilder {
  private readonly RoutingOptions _routingOptions;
  private readonly EventSubscriptionDiscovery _discovery;
  private readonly string _serviceName;
  private readonly IInboxRoutingStrategy? _inboxStrategy;
  private readonly Messaging.IReceptorRegistryQuery _receptorRegistry;
  private readonly IMessageTypeCatalog? _messageCatalog;
  private readonly ILogger _logger;

  /// <summary>
  /// Creates a new transport subscription builder.
  /// </summary>
  /// <param name="routingOptions">Routing options containing owned domains and inbox strategy.</param>
  /// <param name="discovery">Event subscription discovery service.</param>
  /// <param name="serviceName">Name of this service (used for subscription naming).</param>
  /// <param name="inboxStrategy">DI-resolved inbox routing strategy; when supplied it takes
  /// precedence over <see cref="RoutingOptions.InboxStrategy"/> (topology arc phase 3 fix —
  /// the builder previously read the strategy off options only, silently ignoring a
  /// DI-registered override). Null falls back to the options strategy.</param>
  /// <param name="receptorRegistry">Receptor registry query; its
  /// <see cref="Messaging.IReceptorRegistryQuery.GetHandledMessages"/> enumeration feeds the
  /// <see cref="InboxSubscriptionContext"/>.</param>
  public TransportSubscriptionBuilder(
      IOptions<RoutingOptions> routingOptions,
      EventSubscriptionDiscovery discovery,
      string serviceName,
      IInboxRoutingStrategy? inboxStrategy,
      Messaging.IReceptorRegistryQuery receptorRegistry)
    : this(routingOptions, discovery, serviceName, inboxStrategy, receptorRegistry, null, null) {
  }

  /// <summary>
  /// Creates a new transport subscription builder that checks the opt-out list of event types (#2175) against the
  /// application's message types and logs what it excludes.
  /// </summary>
  /// <param name="routingOptions">Routing options containing owned domains, inbox strategy and excluded event types.</param>
  /// <param name="discovery">Event subscription discovery service.</param>
  /// <param name="serviceName">Name of this service (used for subscription naming).</param>
  /// <param name="inboxStrategy">DI-resolved inbox routing strategy; null falls back to the options strategy.</param>
  /// <param name="receptorRegistry">Receptor registry query: the handled-message enumeration and what this service
  /// consumes.</param>
  /// <param name="messageCatalog">The application's message types; an excluded type missing from it is a warning.
  /// Null skips that check.</param>
  /// <param name="logger">Receives the excluded-event-types warnings and the effective exclusions; null discards
  /// them.</param>
  /// <docs>fundamentals/dispatcher/routing#excluded-event-types</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderExcludedEventTypesTests.cs:BuildEventDestinations_ExcludedType_IsOnlyOnItsOwnTopicAsync</tests>
  public TransportSubscriptionBuilder(
      IOptions<RoutingOptions> routingOptions,
      EventSubscriptionDiscovery discovery,
      string serviceName,
      IInboxRoutingStrategy? inboxStrategy,
      Messaging.IReceptorRegistryQuery receptorRegistry,
      IMessageTypeCatalog? messageCatalog,
      ILogger? logger) {
    ArgumentNullException.ThrowIfNull(routingOptions);
    ArgumentNullException.ThrowIfNull(discovery);
    ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
    ArgumentNullException.ThrowIfNull(receptorRegistry);

    _routingOptions = routingOptions.Value;
    _discovery = discovery;
    _serviceName = serviceName;
    _inboxStrategy = inboxStrategy;
    _receptorRegistry = receptorRegistry;
    _messageCatalog = messageCatalog;
    _logger = logger ?? NullLogger.Instance;
  }

  /// <summary>
  /// The metadata key every event destination carries (#2175): the subjects (<see cref="EventSubject"/>) its
  /// subscription excludes, as a string array, empty when it excludes nothing. A transport that filters at the broker
  /// converges the subscription to it; an empty list means deliver every event on the topic.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#excluded-event-types</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderExcludedEventTypesTests.cs:BuildEventDestinations_NothingExcluded_EveryDestinationCarriesAnEmptyListAsync</tests>
  public static string ExcludedEventSubjectsMetadataKey => EXCLUDED_EVENT_SUBJECTS_METADATA_KEY;

  /// <summary>Backing constant for <see cref="ExcludedEventSubjectsMetadataKey"/>.</summary>
  private const string EXCLUDED_EVENT_SUBJECTS_METADATA_KEY = "ExcludedEventSubjects";

  /// <summary>
  /// Builds all transport destinations for subscription.
  /// Includes inbox (command) and event namespace subscriptions.
  /// </summary>
  /// <returns>List of transport destinations to subscribe to.</returns>
  public IReadOnlyList<TransportDestination> BuildDestinations() {
    var destinations = new List<TransportDestination>();

    // Add inbox subscriptions for commands (plural seam — one destination per subscription)
    destinations.AddRange(BuildInboxDestinations());

    // Add event namespace subscriptions
    var eventDestinations = BuildEventDestinations();
    destinations.AddRange(eventDestinations);

    return destinations;
  }

  /// <summary>
  /// Builds the inbox destination for receiving commands.
  /// </summary>
  /// <remarks>Legacy singular surface — returns the first destination of
  /// <see cref="BuildInboxDestinations"/> (both built-in strategies produce exactly one;
  /// multi-subscription strategies should be consumed via the plural method).</remarks>
  /// <returns>Inbox transport destination, or null if no inbox subscription needed.</returns>
  public TransportDestination? BuildInboxDestination() {
    var destinations = BuildInboxDestinations();
    return destinations.Count > 0 ? destinations[0] : null;
  }

  /// <summary>
  /// Builds one transport destination per inbox subscription returned by the strategy's
  /// plural <see cref="IInboxRoutingStrategy.GetSubscriptions"/> seam (topology arc
  /// phase 3). The strategy is resolved from DI when one was supplied, falling back to
  /// <see cref="RoutingOptions.InboxStrategy"/>; the subscription context carries the
  /// receptor registry's handled-message enumeration when the registry is resolvable.
  /// </summary>
  /// <returns>All inbox transport destinations; empty if no inbox strategy is configured.</returns>
  /// <docs>fundamentals/dispatcher/routing#transport-subscription-builder</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderTests.cs:BuildInboxDestinations_SharedTopicStrategy_BitIdenticalToSingularPathAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderTests.cs:BuildInboxDestinations_MultiSubscriptionStrategy_BuildsOneDestinationPerSubscriptionAsync</tests>
  public IReadOnlyList<TransportDestination> BuildInboxDestinations() {
    // DI-resolved strategy wins; options is the fallback (plan-flagged fix: the strategy
    // was read off options only, silently ignoring a DI-registered override). The fallback is
    // never null — RoutingOptions sets it in its constructor and SetInboxStrategy rejects null —
    // so the coalesce always answers and there is nothing left to guard against.
    var inboxStrategy = _inboxStrategy ?? _routingOptions.InboxStrategy;

    var context = new InboxSubscriptionContext(
        _serviceName,
        _routingOptions.OwnedDomains,
        _receptorRegistry.GetHandledMessages()) {
      // Consumed event namespaces reuse EventSubscriptionDiscovery (perspectives + event
      // receptors + manual subscriptions) — the composite/raw-carry surface for strategies
      // computing "namespaces this service consumes ANY constituent from" (phase 5).
      ConsumedEventNamespaces = _discovery.DiscoverEventNamespaces()
    };

    var subscriptions = inboxStrategy.GetSubscriptions(context);
    var destinations = new List<TransportDestination>(subscriptions.Count);
    foreach (var subscription in subscriptions) {
      // Build metadata dictionary from InboxSubscription metadata
      var metadata = _buildMetadata(subscription);

      // Add SubscriberName for deterministic queue naming (critical for competing consumers)
      metadata ??= [];
      metadata["SubscriberName"] = JsonElementHelper.FromString(_serviceName);

      destinations.Add(new TransportDestination(
          Address: subscription.Topic,
          RoutingKey: subscription.FilterExpression,
          Metadata: metadata));
    }

    return destinations;
  }

  /// <summary>
  /// Builds destinations for all event namespaces.
  /// Combines auto-discovered and manually configured namespaces.
  /// </summary>
  /// <returns>List of event transport destinations.</returns>
  /// <exception cref="InvalidOperationException">Thrown when an excluded event type is one this service consumes
  /// (#2175).</exception>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderExcludedEventTypesTests.cs:BuildEventDestinations_ExcludedType_IsOnlyOnItsOwnTopicAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderExcludedEventTypesTests.cs:BuildDestinations_ExcludedTypeThisServiceConsumes_FailsAsync</tests>
  public IReadOnlyList<TransportDestination> BuildEventDestinations() {
    var eventNamespaces = _discovery.DiscoverEventNamespaces();
    var destinations = new List<TransportDestination>();

    // The opt-out list (#2175), checked here so every path that builds event destinations refuses an excluded type
    // this service consumes. Each destination carries its subjects, empty when it excludes nothing, so a transport
    // converges a subscription back to delivering everything when a type leaves the list.
    var excluded = ExcludedEventTypePlanner.Plan(
      _routingOptions, eventNamespaces, _receptorRegistry, _discovery.RuntimeEventTypes, _messageCatalog, _logger);

    foreach (var ns in eventNamespaces) {
      // Add SubscriberName for deterministic queue naming (critical for competing consumers)
      var metadata = new Dictionary<string, System.Text.Json.JsonElement> {
        ["SubscriberName"] = JsonElementHelper.FromString(_serviceName),
        [EXCLUDED_EVENT_SUBJECTS_METADATA_KEY] = JsonElementHelper.FromStringArray(excluded[ns])
      };

      destinations.Add(new TransportDestination(
          Address: ns,
          RoutingKey: "#", // Subscribe to all messages in namespace
          Metadata: metadata));
    }

    return destinations;
  }

  /// <summary>
  /// Configures TransportConsumerOptions with all discovered destinations.
  /// </summary>
  /// <param name="options">Options to configure.</param>
  public void ConfigureOptions(TransportConsumerOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    var destinations = BuildDestinations();
    options.Destinations.AddRange(destinations);
  }

  /// <summary>
  /// Builds metadata dictionary from InboxSubscription metadata,
  /// converting to JsonElement format required by TransportDestination.
  /// </summary>
  private static Dictionary<string, System.Text.Json.JsonElement>? _buildMetadata(
      InboxSubscription subscription) {
    if (subscription.Metadata is null || subscription.Metadata.Count == 0) {
      return null;
    }

    var result = new Dictionary<string, System.Text.Json.JsonElement>();
    foreach (var kvp in subscription.Metadata) {
      // Convert common metadata types to JsonElement
      result[kvp.Key] = kvp.Value switch {
        string s => JsonElementHelper.FromString(s),
        int i => JsonElementHelper.FromInt32(i),
        bool b => JsonElementHelper.FromBoolean(b),
        IEnumerable<string> strings => JsonElementHelper.FromStringArray(strings),
        _ => JsonElementHelper.FromString(kvp.Value?.ToString() ?? "")
      };
    }

    return result;
  }
}

/// <summary>
/// Extension methods for registering TransportSubscriptionBuilder.
/// </summary>
public static class TransportSubscriptionBuilderExtensions {
  /// <summary>
  /// Adds the TransportSubscriptionBuilder to the service collection.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="serviceName">Name of this service.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddTransportSubscriptionBuilder(
      this IServiceCollection services,
      string serviceName) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
    services.TryAddWhizbangDefaults();

    services.AddSingleton(sp => new TransportSubscriptionBuilder(
        sp.GetRequiredService<IOptions<RoutingOptions>>(),
        sp.GetRequiredService<EventSubscriptionDiscovery>(),
        serviceName,
        sp.GetService<IInboxRoutingStrategy>(),
        sp.GetRequiredService<Messaging.IReceptorRegistryQuery>(),
        sp.GetService<IMessageTypeCatalog>(),
        sp.GetService<ILogger<TransportSubscriptionBuilder>>()));

    TryAddTopologyManifest(services, _ => serviceName);

    return services;
  }

  /// <summary>
  /// TryAdds a <see cref="TopologyManifest"/> factory (topology arc phase 5): the manifest is
  /// built from the registered strategies + receptor registry + message catalog at FIRST
  /// resolve, so <c>TransportConsumerWorker</c> can run manifest-driven DARK provisioning
  /// without any consumer opt-in. When routing is not configured the factory yields an empty
  /// manifest — manifest provisioning then names nothing and provisions nothing.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="serviceNameResolver">Resolves the service name (broker subscription/queue
  /// names derive from it).</param>
  /// <docs>fundamentals/dispatcher/routing#topology-manifest</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/TransportSubscriptionBuilderTests.cs:AddTransportSubscriptionBuilder_MakesTopologyManifestResolvableAsync</tests>
  internal static void TryAddTopologyManifest(
      IServiceCollection services,
      Func<IServiceProvider, string> serviceNameResolver) {
    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
      .TryAddSingleton(services, sp => {
        var serviceName = serviceNameResolver(sp);
        var routingOptions = sp.GetOptionsValue<RoutingOptions>();
        var inboxStrategy = sp.GetService<IInboxRoutingStrategy>() ?? routingOptions?.InboxStrategy;
        var outboxStrategy = sp.GetService<IOutboxRoutingStrategy>() ?? routingOptions?.OutboxStrategy;
        if (routingOptions is null || inboxStrategy is null || outboxStrategy is null) {
          // No routing configured — an empty manifest keeps manifest provisioning a no-op.
          return new TopologyManifest(serviceName, [], []);
        }

        var registry = sp.GetRequiredService<Messaging.IReceptorRegistryQuery>();
        var discovery = sp.GetService<EventSubscriptionDiscovery>();
        var context = new InboxSubscriptionContext(
            serviceName,
            routingOptions.OwnedDomains,
            registry.GetHandledMessages()) {
          ConsumedEventNamespaces = discovery?.DiscoverEventNamespaces()
            ?? System.Collections.Frozen.FrozenSet<string>.Empty
        };
        var catalog = sp.GetService<IMessageTypeCatalog>()?.GetAll()
          ?? (IReadOnlyList<MessageTypeCatalogEntry>)[];

        return TopologyManifestBuilder.Build(outboxStrategy, inboxStrategy, context, catalog);
      });
  }
}
