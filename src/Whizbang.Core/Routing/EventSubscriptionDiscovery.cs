using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Routing;

/// <summary>
/// Service for discovering event namespaces that a service should subscribe to.
/// Combines auto-discovered namespaces (from perspectives/receptors) with manual subscriptions.
/// </summary>
/// <remarks>
/// <para>
/// Event subscriptions are determined by:
/// 1. Auto-discovery: Namespaces from <see cref="EventNamespaceRegistry"/> (populated by module initializers)
/// 2. Manual subscriptions: Namespaces configured via RoutingOptions.SubscribeTo()
/// </para>
/// <para>
/// Use this service at transport startup to determine which event topics to subscribe to.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#event-subscription-discovery</docs>
public sealed class EventSubscriptionDiscovery {
  private readonly IEventNamespaceRegistry? _registry;
  private readonly RoutingOptions _routingOptions;

  /// <summary>
  /// Creates a new event subscription discovery service.
  /// </summary>
  /// <param name="routingOptions">Routing options containing manual subscriptions.</param>
  /// <param name="registry">Event namespace registry for testing (optional). When null, uses static <see cref="EventNamespaceRegistry"/>.</param>
  public EventSubscriptionDiscovery(
      IOptions<RoutingOptions> routingOptions,
      IEventNamespaceRegistry? registry = null) {
    ArgumentNullException.ThrowIfNull(routingOptions);
    _routingOptions = routingOptions.Value;
    _registry = registry;
  }

  /// <summary>
  /// Discovers all event namespaces that this service should subscribe to.
  /// Excludes namespaces that overlap with owned domains (this service publishes those, not subscribes).
  /// </summary>
  /// <returns>Combined set of event namespaces from auto-discovery and manual configuration, excluding owned namespaces.</returns>
  public IReadOnlySet<string> DiscoverEventNamespaces() {
    var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Add auto-discovered namespaces from perspectives and receptors
    // Use injected registry (for testing) or static registry (production)
    var autoNamespaces = _registry?.GetAllEventNamespaces()
        ?? EventNamespaceRegistry.GetAllNamespaces();

    foreach (var ns in autoNamespaces) {
      namespaces.Add(ns);
    }

    // Add manual subscriptions from RoutingOptions
    foreach (var ns in _routingOptions.SubscribedNamespaces) {
      namespaces.Add(ns);
    }

    // Remove namespaces that overlap with owned domains
    // (this service publishes to those, it shouldn't subscribe to them)
    foreach (var ownedDomain in _routingOptions.OwnedDomains) {
      // Remove exact matches
      namespaces.Remove(ownedDomain);

      // Remove namespaces that are children of owned domains
      // e.g., if owned is "jdx.contracts.bff", remove "jdx.contracts.bff.events"
      var ownedPrefix = ownedDomain.EndsWith('.')
        ? ownedDomain
        : ownedDomain + ".";

      namespaces.RemoveWhere(ns =>
        ns.StartsWith(ownedPrefix, StringComparison.OrdinalIgnoreCase));
    }

    return namespaces;
  }

  /// <summary>
  /// Gets only the auto-discovered event namespaces (from perspectives and receptors).
  /// </summary>
  /// <returns>Set of auto-discovered event namespaces.</returns>
  public IReadOnlySet<string> GetAutoDiscoveredNamespaces() {
    // Use injected registry (for testing) or static registry (production)
    return _registry?.GetAllEventNamespaces()
        ?? EventNamespaceRegistry.GetAllNamespaces();
  }

  /// <summary>
  /// Gets only the manually configured event namespaces.
  /// </summary>
  /// <returns>Set of manually configured event namespaces.</returns>
  public IReadOnlySet<string> GetManualSubscriptions() {
    return _routingOptions.SubscribedNamespaces;
  }
}

/// <summary>
/// Extension methods for registering EventSubscriptionDiscovery.
/// </summary>
public static class EventSubscriptionDiscoveryExtensions {
  /// <summary>
  /// Adds the EventSubscriptionDiscovery service to the service collection.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddEventSubscriptionDiscovery(this IServiceCollection services) {
    services.AddSingleton<EventSubscriptionDiscovery>();
    return services;
  }
}
