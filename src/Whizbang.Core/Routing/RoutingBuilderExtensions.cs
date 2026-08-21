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
/// <b>The default topology.</b> With no inbox/outbox call at all, a service runs the
/// per-namespace command topology: it subscribes to one <c>inbox.&lt;contract-namespace&gt;</c>
/// entity per command namespace its receptors handle (plus the system broadcast inbox
/// <c>inbox.whizbang</c>), publishes commands to those same entities, publishes events to domain
/// topics, and subscribes to NO catch-all. That is three broker operations per command — send,
/// deliver, settle — instead of a fan-out to every service bound to a shared inbox.
/// <code>
/// services.AddWhizbang()
///     .WithRouting(routing => {
///         routing.OwnDomains("myapp.orders.commands")
///                .SubscribeTo("myapp.payments.events");
///     });
/// </code>
/// </para>
/// <para>
/// <b>Migrating an existing service.</b> A service that explicitly selects a legacy strategy
/// (<c>Inbox.UseSharedTopic</c> / <c>Outbox.UseSharedTopic</c> / <c>UseDomainTopics</c> /
/// <c>UseCustom</c>) keeps working exactly as before: that selection also restores the
/// pre-migration flip and retirement state, so its shared inbox is still named by the topology
/// manifest and still provisioned. To adopt the new topology, migrate one namespace at a time:
/// <list type="number">
/// <item><description>Drop the inbox call on the handling service and add
/// <see cref="RoutingOptions.KeepSharedInbox"/> — it now subscribes to its per-namespace inboxes
/// AND the catch-all, a strict superset, so nothing can be missed while publishers lag.</description></item>
/// <item><description>On each publisher, flip that namespace with
/// <see cref="RoutingOptions.RouteCommandNamespaceToInbox"/> (or the configuration entry
/// <c>Whizbang:Routing:CommandNamespacesToInbox</c>). Rollback is removing the entry.</description></item>
/// <item><description>Once every namespace is flipped, drop <c>KeepSharedInbox()</c> and the
/// explicit strategy calls. The catch-all is then unreferenced and can be deleted at the broker.</description></item>
/// </list>
/// Every step is also configuration-bindable
/// (<c>Whizbang:Routing:RouteAllCommandNamespacesToInbox</c>,
/// <c>Whizbang:Routing:RetireSharedInbox</c>), so a migration step or a rollback needs no redeploy.
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
  ///             .SubscribeTo("myapp.payments.events");
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

    // Register as IOptions<RoutingOptions> (AOT-safe, no reflection). The factory defers to
    // first resolution so the configuration flip set (Whizbang:Routing, topology arc phase 6)
    // can be applied on top of the code callback — Options.Create bypasses the options
    // pipeline entirely, so binding happens HERE, not via IPostConfigureOptions. Explicit
    // per-key reads only (house binder idiom).
    builder.Services.AddSingleton<IOptions<RoutingOptions>>(sp => {
      RoutingOptionsConfigurationBinder.Apply(
        sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>(), options);
      // Control class (topology arc phase 9): adopt the DI-bound instance so the strategy, the
      // mint and the receive boundary all read ONE object — a second copy is how a class ends up
      // provisioned sessionless while the receive path still writes inbox rows for it.
      if (sp.GetService<IOptions<ControlClassOptions>>()?.Value is { } controlClass) {
        options.ControlClass = controlClass;
      }
      // Startup validation (topology arc phase 7): retiring the shared inbox is valid ONLY
      // once every command namespace is flipped — runs AFTER configuration binding so
      // code-callback and configuration-driven retirement are guarded identically.
      options.ThrowIfRetirementIncomplete();
      return Options.Create(options);
    });

    // Register routing strategies from options for use by TransportPublishStrategy
    // These transform outbox destinations (e.g., "createtenant" → "inbox").
    // The outbox factory resolves IOptions<RoutingOptions> first so the configuration flip
    // set is guaranteed applied before any strategy consumer routes a message.
    builder.Services.AddSingleton<IOutboxRoutingStrategy>(sp => {
      _ = sp.GetRequiredService<IOptions<RoutingOptions>>().Value;
      return options.OutboxStrategy;
    });
    builder.Services.AddSingleton<IInboxRoutingStrategy>(options.InboxStrategy);

    // Register EventSubscriptionDiscovery for event namespace discovery
    builder.Services.AddSingleton<EventSubscriptionDiscovery>();

    // Register marker to indicate routing was configured
    builder.Services.AddSingleton<IRoutingConfigured, RoutingConfiguredMarker>();

    return builder;
  }
}
