using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

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
        var logger = sp.GetService<ILogger<RabbitMQConnectionRetry>>();
        logger?.LogInformation("Creating RabbitMQ connection with retry (initial {InitialAttempts} attempts, then indefinitely={RetryIndefinitely})", options.InitialRetryAttempts, options.RetryIndefinitely);

        var connectionRetry = new RabbitMQConnectionRetry(options, logger);
        var factory = new ConnectionFactory {
          Uri = new Uri(connectionString),
          AutomaticRecoveryEnabled = true,
          NetworkRecoveryInterval = options.InitialRetryDelay
        };

        var connection = connectionRetry.CreateConnectionWithRetryAsync(factory).GetAwaiter().GetResult();

        // Wire up connection state monitoring for runtime reconnection visibility
        _wireUpConnectionStateMonitoring(connection, logger);

        return connection;
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

    // Register transport readiness check
    services.AddSingleton<ITransportReadinessCheck>(sp => {
      var connection = sp.GetRequiredService<IConnection>();
      return new RabbitMQReadinessCheck(connection);
    });

    // Register message publish strategy
    services.AddSingleton<IMessagePublishStrategy>(sp =>
      new TransportPublishStrategy(
        sp.GetRequiredService<ITransport>(),
        sp.GetRequiredService<ITransportReadinessCheck>()
      )
    );

    return services;
  }

  /// <summary>
  /// Wires up connection state monitoring for runtime reconnection visibility.
  /// RabbitMQ's automatic recovery handles reconnection; this provides logging for observability.
  /// </summary>
  [SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Connection events are infrequent - high-performance logging not justified")]
  private static void _wireUpConnectionStateMonitoring(IConnection connection, ILogger? logger) {
    if (logger == null) {
      return;
    }

    // Log when connection is lost
    connection.ConnectionShutdownAsync += (_, args) => {
      logger.LogWarning(
        "RabbitMQ connection shutdown. Reason: {ReplyCode} - {ReplyText}. Automatic recovery will attempt to reconnect.",
        args.ReplyCode,
        args.ReplyText);
      return Task.CompletedTask;
    };

    // Log when automatic recovery succeeds
    connection.RecoverySucceededAsync += (_, _) => {
      logger.LogInformation("RabbitMQ connection recovered successfully after temporary disconnection");
      return Task.CompletedTask;
    };

    // Log when automatic recovery fails (will continue retrying)
    connection.ConnectionRecoveryErrorAsync += (_, args) => {
      logger.LogError(
        args.Exception,
        "RabbitMQ connection recovery attempt failed. Automatic recovery will continue retrying.");
      return Task.CompletedTask;
    };

    // Log when connection is blocked by broker (resource alarm)
    connection.ConnectionBlockedAsync += (_, args) => {
      logger.LogWarning(
        "RabbitMQ connection blocked by broker. Reason: {Reason}. Publishing may be delayed.",
        args.Reason);
      return Task.CompletedTask;
    };

    // Log when connection is unblocked
    connection.ConnectionUnblockedAsync += (_, _) => {
      logger.LogInformation("RabbitMQ connection unblocked. Normal operation resumed.");
      return Task.CompletedTask;
    };
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
