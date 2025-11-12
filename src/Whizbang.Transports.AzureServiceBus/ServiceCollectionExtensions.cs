using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Extension methods for registering Azure Service Bus transport with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers Azure Service Bus transport as the ITransport implementation.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="connectionString">The Azure Service Bus connection string.</param>
  /// <param name="configureOptions">Optional configuration callback for transport options.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddAzureServiceBusTransport(
    this IServiceCollection services,
    string connectionString,
    Action<AzureServiceBusOptions>? configureOptions = null
  ) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

    // Configure options
    var options = new AzureServiceBusOptions();
    configureOptions?.Invoke(options);

    // Register transport as singleton
    services.AddSingleton<ITransport>(sp => {
      var logger = sp.GetService<ILogger<AzureServiceBusTransport>>();
      return new AzureServiceBusTransport(connectionString, options, logger);
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
