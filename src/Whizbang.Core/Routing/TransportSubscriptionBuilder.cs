using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// <docs>core-concepts/routing#transport-subscription-builder</docs>
public sealed class TransportSubscriptionBuilder {
  private readonly RoutingOptions _routingOptions;
  private readonly EventSubscriptionDiscovery _discovery;
  private readonly string _serviceName;

  /// <summary>
  /// Creates a new transport subscription builder.
  /// </summary>
  /// <param name="routingOptions">Routing options containing owned domains and inbox strategy.</param>
  /// <param name="discovery">Event subscription discovery service.</param>
  /// <param name="serviceName">Name of this service (used for subscription naming).</param>
  public TransportSubscriptionBuilder(
      IOptions<RoutingOptions> routingOptions,
      EventSubscriptionDiscovery discovery,
      string serviceName) {
    ArgumentNullException.ThrowIfNull(routingOptions);
    ArgumentNullException.ThrowIfNull(discovery);
    ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

    _routingOptions = routingOptions.Value;
    _discovery = discovery;
    _serviceName = serviceName;
  }

  /// <summary>
  /// Builds all transport destinations for subscription.
  /// Includes inbox (command) and event namespace subscriptions.
  /// </summary>
  /// <returns>List of transport destinations to subscribe to.</returns>
  public IReadOnlyList<TransportDestination> BuildDestinations() {
    var destinations = new List<TransportDestination>();

    // Add inbox subscription for commands
    var inboxDestination = BuildInboxDestination();
    if (inboxDestination is not null) {
      destinations.Add(inboxDestination);
    }

    // Add event namespace subscriptions
    var eventDestinations = BuildEventDestinations();
    destinations.AddRange(eventDestinations);

    return destinations;
  }

  /// <summary>
  /// Builds the inbox destination for receiving commands.
  /// </summary>
  /// <returns>Inbox transport destination, or null if no inbox subscription needed.</returns>
  public TransportDestination? BuildInboxDestination() {
    var inboxStrategy = _routingOptions.InboxStrategy;
    if (inboxStrategy is null) {
      return null;
    }

    var subscription = inboxStrategy.GetSubscription(
        _routingOptions.OwnedDomains,
        _serviceName,
        MessageKind.Command);

    // Build metadata dictionary from InboxSubscription metadata
    var metadata = _buildMetadata(subscription);

    // Add SubscriberName for deterministic queue naming (critical for competing consumers)
    metadata ??= new Dictionary<string, System.Text.Json.JsonElement>();
    metadata["SubscriberName"] = JsonElementHelper.FromString(_serviceName);

    return new TransportDestination(
        Address: subscription.Topic,
        RoutingKey: subscription.FilterExpression,
        Metadata: metadata);
  }

  /// <summary>
  /// Builds destinations for all event namespaces.
  /// Combines auto-discovered and manually configured namespaces.
  /// </summary>
  /// <returns>List of event transport destinations.</returns>
  public IReadOnlyList<TransportDestination> BuildEventDestinations() {
    var eventNamespaces = _discovery.DiscoverEventNamespaces();
    var destinations = new List<TransportDestination>();

    foreach (var ns in eventNamespaces) {
      // Add SubscriberName for deterministic queue naming (critical for competing consumers)
      var metadata = new Dictionary<string, System.Text.Json.JsonElement> {
        ["SubscriberName"] = JsonElementHelper.FromString(_serviceName)
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
    foreach (var destination in destinations) {
      options.Destinations.Add(destination);
    }
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

    services.AddSingleton(sp => new TransportSubscriptionBuilder(
        sp.GetRequiredService<IOptions<RoutingOptions>>(),
        sp.GetRequiredService<EventSubscriptionDiscovery>(),
        serviceName));

    return services;
  }
}
