using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whizbang.Core.HealthChecks;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for additional consumer destinations beyond auto-generated ones.
/// </summary>
/// <remarks>
/// Use this to add custom transport destinations that are not automatically
/// derived from <see cref="RoutingOptions"/>. The additional destinations
/// are appended after auto-generated inbox and event subscriptions.
/// </remarks>
/// <docs>messaging/transports/transport-consumer#additional-destinations</docs>
public sealed class TransportConsumerConfiguration {
  /// <summary>
  /// Gets the list of additional destinations to subscribe to beyond auto-generated ones.
  /// </summary>
  /// <remarks>
  /// These destinations are appended after auto-generated destinations from:
  /// <list type="bullet">
  /// <item><description>Inbox subscription (from <see cref="RoutingOptions.OwnedDomains"/>)</description></item>
  /// <item><description>Event subscriptions (from <see cref="RoutingOptions.SubscribedNamespaces"/> and auto-discovery)</description></item>
  /// </list>
  /// </remarks>
  public List<TransportDestination> AdditionalDestinations { get; } = [];

  /// <summary>
  /// Gets the resilience options for subscription retry behavior.
  /// Resilience is always enabled - subscriptions retry with exponential backoff on failure.
  /// </summary>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  public SubscriptionResilienceOptions ResilienceOptions { get; } = new();
}

/// <summary>
/// Extension methods for registering <see cref="TransportConsumerWorker"/> with
/// auto-generated subscriptions from routing configuration.
/// </summary>
/// <remarks>
/// <para>
/// These extensions eliminate manual <see cref="TransportConsumerOptions"/> configuration
/// by automatically generating subscriptions from <see cref="RoutingOptions"/>.
/// </para>
/// <para>
/// Subscriptions are auto-generated from:
/// <list type="bullet">
/// <item><description><see cref="RoutingOptions.OwnedDomains"/> → inbox subscription via <see cref="IInboxRoutingStrategy"/></description></item>
/// <item><description><see cref="RoutingOptions.SubscribedNamespaces"/> → event subscriptions</description></item>
/// <item><description>Auto-discovered event namespaces from <see cref="IEventNamespaceRegistry"/></description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer#auto-configuration</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerBuilderExtensionsTests.cs</tests>
public static class TransportConsumerBuilderExtensions {
  private const string HEALTH_CHECK_NAME = "subscriptions";

  /// <summary>
  /// Registers <see cref="TransportConsumerWorker"/> with auto-generated subscriptions
  /// from routing configuration.
  /// </summary>
  /// <param name="builder">The <see cref="WhizbangBuilder"/> to configure.</param>
  /// <param name="configure">Optional action to add custom destinations.</param>
  /// <returns>The builder for method chaining.</returns>
  /// <exception cref="ArgumentNullException">
  /// Thrown when <paramref name="builder"/> is null.
  /// </exception>
  /// <exception cref="InvalidOperationException">
  /// Thrown at resolution time when <see cref="WithRouting"/> was not called before this method.
  /// </exception>
  /// <remarks>
  /// <para>
  /// This method registers:
  /// <list type="bullet">
  /// <item><description><see cref="TransportConsumerOptions"/> as a singleton (auto-populated)</description></item>
  /// <item><description><see cref="TransportConsumerWorker"/> as a hosted service</description></item>
  /// </list>
  /// </para>
  /// <para>
  /// <b>Prerequisites:</b> <see cref="WithRouting"/> must be called before this method
  /// to register <see cref="IOptions{RoutingOptions}"/> and <see cref="EventSubscriptionDiscovery"/>.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// services.AddWhizbang()
  ///     .WithRouting(routing => {
  ///         routing.OwnDomains("myapp.orders.commands")
  ///                .SubscribeTo("myapp.payments.events");
  ///     })
  ///     .AddTransportConsumer(); // Auto-generates subscriptions!
  /// </code>
  /// </example>
  /// <example>
  /// With additional custom destinations:
  /// <code>
  /// services.AddWhizbang()
  ///     .WithRouting(routing => {
  ///         routing.OwnDomains("myapp.orders.commands");
  ///     })
  ///     .AddTransportConsumer(config => {
  ///         config.AdditionalDestinations.Add(
  ///             new TransportDestination("custom-topic", "my-subscription"));
  ///     });
  /// </code>
  /// </example>
  /// <docs>messaging/transports/transport-consumer#auto-configuration</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerBuilderExtensionsTests.cs:AddTransportConsumer_AutoPopulatesInboxDestination_FromOwnDomainsAsync</tests>
  public static WhizbangBuilder AddTransportConsumer(
      this WhizbangBuilder builder,
      Action<TransportConsumerConfiguration>? configure = null) {
    ArgumentNullException.ThrowIfNull(builder);

    // Apply custom configuration if provided
    var config = new TransportConsumerConfiguration();
    configure?.Invoke(config);

    // Capture configuration in closures for factory patterns
    var additionalDestinations = config.AdditionalDestinations.ToList();
    var resilienceOptions = config.ResilienceOptions;

    // Register SubscriptionResilienceOptions as singleton
    builder.Services.AddSingleton(resilienceOptions);

    // Register TransportConsumerOptions as singleton using factory pattern (AOT-safe)
    // The factory resolves dependencies at runtime and populates destinations
    builder.Services.AddSingleton<TransportConsumerOptions>(sp => {
      var options = new TransportConsumerOptions();

      // Check if WithRouting() was called by looking for the marker
      if (sp.GetService<IRoutingConfigured>() is null) {
        throw new InvalidOperationException(
            "WithRouting() must be called before AddTransportConsumer(). " +
            "Call builder.WithRouting(routing => ...) to configure routing options first.");
      }

      // Get routing options - guaranteed to exist since WithRouting was called
      var routingOptions = sp.GetRequiredService<IOptions<RoutingOptions>>();

      // Get event subscription discovery (may be null if not registered)
      var discovery = sp.GetService<EventSubscriptionDiscovery>()
          ?? new EventSubscriptionDiscovery(routingOptions, sp.GetService<IEventNamespaceRegistry>());

      // Get service name from provider or use fallback
      var serviceName = _getServiceName(sp);

      // Build and populate destinations using TransportSubscriptionBuilder
      var subscriptionBuilder = new TransportSubscriptionBuilder(
          routingOptions,
          discovery,
          serviceName);

      subscriptionBuilder.ConfigureOptions(options);

      // Add any additional custom destinations
      foreach (var destination in additionalDestinations) {
        options.Destinations.Add(destination);
      }

      return options;
    });

    // Register OrderedStreamProcessor (required by TransportConsumerWorker)
    builder.Services.TryAddSingleton<OrderedStreamProcessor>();

    // Register IEventCascader for cascading messages returned from receptors
    // Uses IServiceProvider to lazily resolve IDispatcher (avoids circular dependency:
    // IDispatcher → IReceptorInvoker → IEventCascader → IDispatcher)
    builder.Services.TryAddSingleton<IEventCascader>(sp => new DispatcherEventCascader(sp));

    // Register IReceptorInvoker as scoped (required by TransportConsumerWorker)
    // Uses TryAdd to avoid overwriting if AddWhizbangReceptorRegistry() was already called
    // Scoped registration follows industry patterns (MediatR, MassTransit) - workers
    // create a scope per message and resolve the invoker from that scope.
    builder.Services.TryAddScoped<IReceptorInvoker>(sp => {
      // Try to get the generated registry (if AddWhizbangReceptorRegistry was called)
      var registry = sp.GetService<IReceptorRegistry>();
      if (registry is not null) {
        var eventCascader = sp.GetService<IEventCascader>();
        var syncAwaiter = sp.GetService<IPerspectiveSyncAwaiter>();
        return new ReceptorInvoker(registry, sp, eventCascader, syncAwaiter);
      }

      // Fallback: return a no-op invoker if no registry is available
      return new NullReceptorInvoker();
    });

    // Register MessageProcessingOptions (consumer can override by registering before this)
    builder.Services.TryAddSingleton(new MessageProcessingOptions());

    // Register TransportBatchOptions (consumer can override by registering before this)
    builder.Services.TryAddSingleton(new TransportBatchOptions());

    // Register default IInboxBatchStrategy (consumer can override by registering before this)
    builder.Services.TryAddSingleton<IInboxBatchStrategy>(sp => {
      var options = sp.GetRequiredService<MessageProcessingOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      var metrics = sp.GetService<TransportMetrics>();
      return new SlidingWindowInboxBatchStrategy(options, scopeFactory, metrics);
    });

    // Register TransportConsumerWorker as hosted service (always with resilience)
    builder.Services.AddHostedService<TransportConsumerWorker>();

    // Register health check for subscription monitoring
    builder.Services.AddHealthChecks()
      .Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
        HEALTH_CHECK_NAME,
        sp => {
          var worker = sp.GetService<TransportConsumerWorker>();
          var states = worker?.SubscriptionStates
            ?? new Dictionary<TransportDestination, SubscriptionState>();
          return new SubscriptionHealthCheck(states);
        },
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
        tags: ["transport", HEALTH_CHECK_NAME]));

    return builder;
  }

  /// <summary>
  /// Registers <see cref="TransportConsumerWorker"/> with auto-generated subscriptions
  /// from routing configuration.
  /// </summary>
  /// <param name="builder">The <see cref="WhizbangPerspectiveBuilder"/> to configure.</param>
  /// <param name="configure">Optional action to add custom destinations.</param>
  /// <returns>The builder for method chaining.</returns>
  /// <remarks>
  /// This overload allows calling <see cref="AddTransportConsumer"/> at the end of a
  /// fluent chain that includes <c>WithEFCore&lt;T&gt;().WithDriver.Postgres</c>.
  /// </remarks>
  /// <example>
  /// <code>
  /// services.AddWhizbang()
  ///     .WithRouting(routing => {
  ///         routing.OwnDomains("myapp.orders.commands");
  ///     })
  ///     .WithEFCore&lt;MyDbContext&gt;()
  ///     .WithDriver.Postgres
  ///     .AddTransportConsumer();
  /// </code>
  /// </example>
  /// <docs>messaging/transports/transport-consumer#auto-configuration</docs>
  public static WhizbangPerspectiveBuilder AddTransportConsumer(
      this WhizbangPerspectiveBuilder builder,
      Action<TransportConsumerConfiguration>? configure = null) {
    ArgumentNullException.ThrowIfNull(builder);

    // Apply custom configuration if provided
    var config = new TransportConsumerConfiguration();
    configure?.Invoke(config);

    // Capture configuration in closures for factory patterns
    var additionalDestinations = config.AdditionalDestinations.ToList();
    var resilienceOptions = config.ResilienceOptions;

    // Register SubscriptionResilienceOptions as singleton
    builder.Services.AddSingleton(resilienceOptions);

    // Register TransportConsumerOptions as singleton using factory pattern (AOT-safe)
    builder.Services.AddSingleton<TransportConsumerOptions>(sp => {
      var options = new TransportConsumerOptions();

      // Check if WithRouting() was called by looking for the marker
      if (sp.GetService<IRoutingConfigured>() is null) {
        throw new InvalidOperationException(
            "WithRouting() must be called before AddTransportConsumer(). " +
            "Call builder.WithRouting(routing => ...) to configure routing options first.");
      }

      // Get routing options - guaranteed to exist since WithRouting was called
      var routingOptions = sp.GetRequiredService<IOptions<RoutingOptions>>();

      // Get event subscription discovery (may be null if not registered)
      var discovery = sp.GetService<EventSubscriptionDiscovery>()
          ?? new EventSubscriptionDiscovery(routingOptions, sp.GetService<IEventNamespaceRegistry>());

      // Get service name from provider or use fallback
      var serviceName = _getServiceName(sp);

      // Build and populate destinations using TransportSubscriptionBuilder
      var subscriptionBuilder = new TransportSubscriptionBuilder(
          routingOptions,
          discovery,
          serviceName);

      subscriptionBuilder.ConfigureOptions(options);

      // Add any additional custom destinations
      foreach (var destination in additionalDestinations) {
        options.Destinations.Add(destination);
      }

      return options;
    });

    // Register OrderedStreamProcessor (required by TransportConsumerWorker)
    builder.Services.TryAddSingleton<OrderedStreamProcessor>();

    // Register IEventCascader for cascading messages returned from receptors
    // Uses IServiceProvider to lazily resolve IDispatcher (avoids circular dependency:
    // IDispatcher → IReceptorInvoker → IEventCascader → IDispatcher)
    builder.Services.TryAddSingleton<IEventCascader>(sp => new DispatcherEventCascader(sp));

    // Register IReceptorInvoker as scoped (required by TransportConsumerWorker)
    // Uses TryAdd to avoid overwriting if AddWhizbangReceptorRegistry() was already called
    // Scoped registration follows industry patterns (MediatR, MassTransit) - workers
    // create a scope per message and resolve the invoker from that scope.
    builder.Services.TryAddScoped<IReceptorInvoker>(sp => {
      // Try to get the generated registry (if AddWhizbangReceptorRegistry was called)
      var registry = sp.GetService<IReceptorRegistry>();
      if (registry is not null) {
        var eventCascader = sp.GetService<IEventCascader>();
        var syncAwaiter = sp.GetService<IPerspectiveSyncAwaiter>();
        return new ReceptorInvoker(registry, sp, eventCascader, syncAwaiter);
      }

      // Fallback: return a no-op invoker if no registry is available
      return new NullReceptorInvoker();
    });

    // Register MessageProcessingOptions (consumer can override by registering before this)
    builder.Services.TryAddSingleton(new MessageProcessingOptions());

    // Register TransportBatchOptions (consumer can override by registering before this)
    builder.Services.TryAddSingleton(new TransportBatchOptions());

    // Register default IInboxBatchStrategy (consumer can override by registering before this)
    builder.Services.TryAddSingleton<IInboxBatchStrategy>(sp => {
      var options = sp.GetRequiredService<MessageProcessingOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      var metrics = sp.GetService<TransportMetrics>();
      return new SlidingWindowInboxBatchStrategy(options, scopeFactory, metrics);
    });

    // Register TransportConsumerWorker as hosted service (always with resilience)
    builder.Services.AddHostedService<TransportConsumerWorker>();

    // Register health check for subscription monitoring
    builder.Services.AddHealthChecks()
      .Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
        HEALTH_CHECK_NAME,
        sp => {
          var worker = sp.GetService<TransportConsumerWorker>();
          var states = worker?.SubscriptionStates
            ?? new Dictionary<TransportDestination, SubscriptionState>();
          return new SubscriptionHealthCheck(states);
        },
        failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
        tags: ["transport", HEALTH_CHECK_NAME]));

    return builder;
  }

  /// <summary>
  /// Gets the service name from <see cref="IServiceInstanceProvider"/> or falls back to assembly name.
  /// </summary>
  private static string _getServiceName(IServiceProvider sp) {
    // Try to get from IServiceInstanceProvider first
    var instanceProvider = sp.GetService<IServiceInstanceProvider>();
    if (instanceProvider is not null) {
      return instanceProvider.ServiceName;
    }

    // Fall back to entry assembly name
    var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
    if (!string.IsNullOrWhiteSpace(assemblyName)) {
      return assemblyName;
    }

    // Ultimate fallback
    return "UnknownService";
  }
}
