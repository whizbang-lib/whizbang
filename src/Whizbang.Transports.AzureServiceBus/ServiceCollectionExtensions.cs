using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;

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

    // Register ServiceBusClient as singleton (needed by ServiceBusReadinessCheck)
    services.AddSingleton(sp => new Azure.Messaging.ServiceBus.ServiceBusClient(connectionString));

    // Register transport as singleton
    services.AddSingleton<ITransport>(sp => {
      var logger = sp.GetService<ILogger<AzureServiceBusTransport>>();
      var transport = new AzureServiceBusTransport(connectionString, jsonOptions, options, logger);

      // IMPORTANT: Initialize transport during registration to verify connectivity
      // This ensures the application won't start if Service Bus is unreachable
      try {
        transport.InitializeAsync().GetAwaiter().GetResult();
        logger?.LogInformation("Azure Service Bus transport initialized successfully during registration");
      } catch (Exception ex) {
        logger?.LogError(ex, "Failed to initialize Azure Service Bus transport during registration - application startup will fail");
        throw;
      }

      return transport;
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
