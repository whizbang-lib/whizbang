using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Routing;

/// <summary>
/// Marker interface to indicate that <see cref="RoutingBuilderExtensions.WithRouting"/> was called.
/// Used by <c>AddTransportConsumer</c> to verify routing is configured.
/// </summary>
internal interface IRoutingConfigured;

/// <summary>
/// Internal implementation of <see cref="IRoutingConfigured"/> marker.
/// </summary>
internal sealed class RoutingConfiguredMarker : IRoutingConfigured;

/// <summary>
/// Extension methods for configuring message routing on <see cref="WhizbangBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// These extensions allow fluent configuration of message routing including:
/// <list type="bullet">
/// <item><description>Command routing via <see cref="RoutingOptions.OwnDomains"/></description></item>
/// <item><description>Event subscriptions via <see cref="RoutingOptions.SubscribeTo"/></description></item>
/// <item><description>Inbox/Outbox routing strategies</description></item>
/// </list>
/// </para>
/// <para>
/// Example usage:
/// <code>
/// services.AddWhizbang()
///     .WithRouting(routing => {
///         routing.OwnDomains("myapp.orders.commands")
///                .SubscribeTo("myapp.payments.events")
///                .Inbox.UseSharedTopic("inbox");
///     });
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#with-routing</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/RoutingBuilderExtensionsTests.cs</tests>
public static class RoutingBuilderExtensions {
  /// <summary>
  /// Configures message routing options including inbox/outbox strategies and event subscriptions.
  /// </summary>
  /// <param name="builder">The <see cref="WhizbangBuilder"/> to configure.</param>
  /// <param name="configure">Action to configure routing options.</param>
  /// <returns>The builder for method chaining.</returns>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="builder"/> or <paramref name="configure"/> is null.
  /// </exception>
  /// <remarks>
  /// <para>
  /// This method registers:
  /// <list type="bullet">
  /// <item><description><see cref="IOptions{RoutingOptions}"/> as a singleton</description></item>
  /// <item><description><see cref="EventSubscriptionDiscovery"/> as a singleton</description></item>
  /// </list>
  /// </para>
  /// <para>
  /// The routing configuration is used by <see cref="TransportSubscriptionBuilder"/> to
  /// auto-generate transport subscriptions when <c>AddTransportConsumer()</c> is called.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// services.AddWhizbang()
  ///     .WithRouting(routing => {
  ///         routing
  ///             .OwnDomains("myapp.orders.commands", "myapp.users.commands")
  ///             .SubscribeTo("myapp.payments.events")
  ///             .Inbox.UseSharedTopic("whizbang.inbox");
  ///     })
  ///     .AddTransportConsumer(); // Auto-generates subscriptions from routing config
  /// </code>
  /// </example>
  /// <docs>fundamentals/dispatcher/routing#with-routing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Routing/RoutingBuilderExtensionsTests.cs:WithRouting_RegistersRoutingOptionsAsync</tests>
  public static WhizbangBuilder WithRouting(
      this WhizbangBuilder builder,
      Action<RoutingOptions> configure) {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(configure);

    // Create and configure options
    var options = new RoutingOptions();
    configure(options);

    // Register as IOptions<RoutingOptions> using Options.Create (AOT-safe, no reflection)
    builder.Services.AddSingleton(Options.Create(options));

    // Register routing strategies from options for use by TransportPublishStrategy
    // These transform outbox destinations (e.g., "createtenant" → "inbox")
    builder.Services.AddSingleton<IOutboxRoutingStrategy>(options.OutboxStrategy);
    builder.Services.AddSingleton<IInboxRoutingStrategy>(options.InboxStrategy);

    // Register EventSubscriptionDiscovery for event namespace discovery
    builder.Services.AddSingleton<EventSubscriptionDiscovery>();

    // Register marker to indicate routing was configured
    builder.Services.AddSingleton<IRoutingConfigured, RoutingConfiguredMarker>();

    return builder;
  }
}
