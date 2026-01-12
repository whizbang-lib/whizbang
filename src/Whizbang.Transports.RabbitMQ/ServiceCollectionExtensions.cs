using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Extension methods for registering RabbitMQ transport with dependency injection.
/// </summary>
/// <docs>components/transports/rabbitmq</docs>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers RabbitMQ transport as the ITransport implementation.
  /// Uses JsonContextRegistry for AOT-compatible serialization.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="connectionString">The RabbitMQ connection string.</param>
  /// <param name="configureOptions">Optional configuration callback for transport options.</param>
  /// <returns>The service collection for chaining.</returns>
  [SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Startup logging doesn't need high performance optimization")]
  public static IServiceCollection AddRabbitMQTransport(
    this IServiceCollection services,
    string connectionString,
    Action<RabbitMQOptions>? configureOptions = null
  ) {
    ArgumentException.ThrowIfNullOrEmpty(connectionString);

    // Configure options
    var options = new RabbitMQOptions();
    configureOptions?.Invoke(options);

    // Get JSON options from JsonContextRegistry
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);

    // Register IConnection as singleton (ONLY if not already registered)
    var existingConn = services.Any(sd => sd.ServiceType == typeof(IConnection));
    if (!existingConn) {
      services.AddSingleton<IConnection>(sp => {
        var logger = sp.GetService<ILogger<RabbitMQTransport>>();
        logger?.LogInformation("Creating RabbitMQ connection");
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        return factory.CreateConnectionAsync().GetAwaiter().GetResult();
      });
    }

    // Register channel pool
    services.AddSingleton(sp => new RabbitMQChannelPool(
      sp.GetRequiredService<IConnection>(),
      options.MaxChannels
    ));

    // Register transport
    services.AddSingleton<ITransport>(sp => {
      var connection = sp.GetRequiredService<IConnection>();
      var pool = sp.GetRequiredService<RabbitMQChannelPool>();
      var logger = sp.GetService<ILogger<RabbitMQTransport>>();

      var transport = new RabbitMQTransport(connection, jsonOptions, pool, options, logger);

      // Initialize during registration
      transport.InitializeAsync().GetAwaiter().GetResult();
      logger?.LogInformation("RabbitMQ transport initialized");

      return transport;
    });

    return services;
  }

  /// <summary>
  /// Registers health checks for RabbitMQ connectivity.
  /// Requires Microsoft.Extensions.Diagnostics.HealthChecks package.
  /// </summary>
  /// <param name="services">The service collection to register health checks with.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddRabbitMQHealthChecks(this IServiceCollection services) {
    services.AddHealthChecks()
      .Add(new HealthCheckRegistration(
        name: "rabbitmq",
        factory: sp => new RabbitMQHealthCheck(
          sp.GetRequiredService<ITransport>(),
          sp.GetRequiredService<IConnection>()
        ),
        failureStatus: HealthStatus.Unhealthy,
        tags: null
      ));

    return services;
  }
}
