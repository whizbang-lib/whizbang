using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbangPostgres_InitializeSchemaFalse_DoesNotInitializeAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbangPostgres_InitializeSchemaTrue_NoPerspective_InitializesInfraOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbangPostgres_InitializeSchemaTrue_WithPerspective_InitializesBothAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_WithValidServices_ReturnsWhizbangBuilderAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_ReturnedBuilder_HasSameServicesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_RegistersCoreServices_SuccessfullyAsync</tests>
/// Extension methods for registering Azure Service Bus transport with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers Azure Service Bus transport as the ITransport implementation.
  /// Uses JsonContextRegistry for AOT-compatible serialization.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="connectionString">The Azure Service Bus connection string.</param>
  /// <param name="configureOptions">Optional configuration callback for transport options.</param>
  /// <returns>The service collection for chaining.</returns>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Startup initialization logging - infrequent calls during DI registration")]
  public static IServiceCollection AddAzureServiceBusTransport(
    this IServiceCollection services,
    string connectionString,
    Action<AzureServiceBusOptions>? configureOptions = null
  ) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

    // Configure options
    var options = new AzureServiceBusOptions();
    configureOptions?.Invoke(options);

    // Get JSON options from registry (includes all registered contexts via ModuleInitializer)
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    // Register JsonSerializerOptions for ServiceBusConsumerWorker and other components
    // This allows workers to deserialize messages using the same JSON context
    services.AddSingleton(jsonOptions);

    // Register ServiceBusClient as singleton (shared by transport and readiness check)
    // ONLY if not already registered (allows tests to provide shared client)
    var existingRegistration = services.Any(sd => sd.ServiceType == typeof(Azure.Messaging.ServiceBus.ServiceBusClient));
    if (!existingRegistration) {
      services.AddSingleton(sp => {
        var logger = sp.GetService<ILogger<AzureServiceBusConnectionRetry>>();
        logger?.LogInformation("Creating Azure Service Bus client with retry (initial {InitialAttempts} attempts, then indefinitely={RetryIndefinitely})", options.InitialRetryAttempts, options.RetryIndefinitely);

        var connectionRetry = new AzureServiceBusConnectionRetry(options, logger);
        return connectionRetry.CreateClientWithRetryAsync(connectionString).GetAwaiter().GetResult();
      });
    }

    // Register transport as singleton, injecting shared client
    services.AddSingleton<ITransport>(sp => {
      var logger = sp.GetService<ILogger<AzureServiceBusTransport>>();
      var client = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();

      var transport = new AzureServiceBusTransport(client, jsonOptions, options, logger);

      // IMPORTANT: Initialize transport during registration to verify connectivity
      // This ensures the application won't start if Service Bus is unreachable
      try {
        transport.InitializeAsync().GetAwaiter().GetResult();
        logger?.LogInformation("Transport initialized (using shared client)");
      } catch (Exception ex) {
        logger?.LogError(ex, "Failed to initialize transport during registration");
        throw;
      }

      return transport;
    });

    // Register transport readiness check
    services.AddSingleton<ITransportReadinessCheck>(sp => {
      var transport = sp.GetRequiredService<ITransport>();
      var client = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();
      var logger = sp.GetRequiredService<ILogger<ServiceBusReadinessCheck>>();
      return new ServiceBusReadinessCheck(transport, client, logger);
    });

    // Register message publish strategy
    // Commands are AUTOMATICALLY routed to shared inbox topic
    // If IOutboxRoutingStrategy is configured (via WithRouting), use its inbox topic
    services.AddSingleton<IMessagePublishStrategy>(sp => {
      var transport = sp.GetRequiredService<ITransport>();
      var readinessCheck = sp.GetRequiredService<ITransportReadinessCheck>();

      // Try to get inbox topic from registered outbox routing strategy
      // WithRouting() registers IOutboxRoutingStrategy directly
      var outboxStrategy = sp.GetService<IOutboxRoutingStrategy>();
      if (outboxStrategy is SharedTopicOutboxStrategy sharedStrategy) {
        // Use the configured inbox topic from outbox strategy
        return new TransportPublishStrategy(transport, readinessCheck, sharedStrategy.InboxTopic);
      }

      // Fall back to default inbox topic
      return new TransportPublishStrategy(transport, readinessCheck);
    });

    return services;
  }

  /// <summary>
  /// Registers health checks for Azure Service Bus connectivity.
  /// Requires Microsoft.Extensions.Diagnostics.HealthChecks package.
  /// </summary>
  /// <param name="services">The service collection to register health checks with.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddAzureServiceBusHealthChecks(this IServiceCollection services) {
    services.AddHealthChecks()
      .AddCheck<AzureServiceBusHealthCheck>("azure_servicebus");

    return services;
  }
}
