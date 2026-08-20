using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Extension methods for registering RabbitMQ transport with dependency injection.
/// </summary>
/// <docs>messaging/transports/rabbitmq</docs>
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
        if (logger?.IsEnabled(LogLevel.Information) == true) {
          var initialAttempts = options.InitialRetryAttempts;
          var retryIndefinitely = options.RetryIndefinitely;
          logger.LogInformation("Creating RabbitMQ connection with retry (initial {InitialAttempts} attempts, then indefinitely={RetryIndefinitely})", initialAttempts, retryIndefinitely);
        }

        var connectionRetry = new RabbitMQConnectionRetry(options, logger);
        var factory = new ConnectionFactory {
          Uri = new Uri(connectionString),
          AutomaticRecoveryEnabled = true,
          NetworkRecoveryInterval = options.InitialRetryDelay,
          ConsumerDispatchConcurrency = 200 // Allow concurrent ReceivedAsync dispatch for batch collection
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

    // Topology ownership-drift surface (phase 5): the provisioner records findings, the
    // health source degrades the "topology" component while any stand.
    services.TryAddSingleton<Whizbang.Core.Routing.TopologyDriftState>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Health.IWhizbangHealthSource,
      Whizbang.Core.Health.TopologyDriftHealthSource>());

    // Register infrastructure provisioner for domain topic auto-provisioning + the
    // manifest-driven DARK provisioning (phase 5) — queue args must mirror the transport's,
    // so the SAME options instance flows in.
    services.AddSingleton<IInfrastructureProvisioner>(sp => {
      var pool = sp.GetRequiredService<RabbitMQChannelPool>();
      var logger = sp.GetRequiredService<ILogger<RabbitMQInfrastructureProvisioner>>();
      var driftState = sp.GetRequiredService<Whizbang.Core.Routing.TopologyDriftState>();
      return new RabbitMQInfrastructureProvisioner(pool, logger, options, driftState);
    });

    // Register transport
    services.AddSingleton<ITransport>(sp => {
      var connection = sp.GetRequiredService<IConnection>();
      var pool = sp.GetRequiredService<RabbitMQChannelPool>();
      var logger = sp.GetService<ILogger<RabbitMQTransport>>();
      var discardPolicy = sp.GetService<Whizbang.Core.Routing.IMessageDiscardPolicy>();

      var transport = new RabbitMQTransport(connection, jsonOptions, pool, options, logger, discardPolicy);

      // Initialize during registration
      transport.InitializeAsync().GetAwaiter().GetResult();
      logger?.LogInformation("RabbitMQ transport initialized");

      return transport;
    });

    // Register transport readiness check
    services.AddSingleton<ITransportReadinessCheck>(sp => {
      var connection = sp.GetRequiredService<IConnection>();
      var logger = sp.GetService<ILogger<RabbitMQReadinessCheck>>();
      return new RabbitMQReadinessCheck(connection, logger);
    });

    // Register message publish strategy
    // Commands are AUTOMATICALLY routed to shared inbox topic
    // If IOutboxRoutingStrategy is configured (via WithRouting), use its inbox topic
    services.AddSingleton<IMessagePublishStrategy>(sp => {
      var transport = sp.GetRequiredService<ITransport>();
      var readinessCheck = sp.GetRequiredService<ITransportReadinessCheck>();
      var loggerFactory = sp.GetService<ILoggerFactory>();

      // Post-serialize hook chain + JsonSerializerOptions are optional; when
      // AddWhizbangBodyOffload (or any AddWhizbangPostSerializeHook<T>) is
      // registered AND the transport's JsonSerializerOptions resolver is
      // available, the strategy will JIT-serialize, run the chain, stamp
      // whizbang.body-size, and validate against MaxMessageSizeBytes.
      // Both null → existing fast-path behavior (no pre-serialize).
      var hookChain = sp.GetService<Whizbang.Core.Offloads.PostSerializeHookChain>();
      var jsonOptions = sp.GetService<JsonSerializerOptions>();

      // Try to get inbox topic from registered outbox routing strategy
      // WithRouting() registers IOutboxRoutingStrategy directly
      var outboxStrategy = sp.GetService<IOutboxRoutingStrategy>();

      // Strategy-agnostic command-inbox seam (topology arc phase 7): both built-in
      // command-routing strategies implement ICommandInboxAddressResolver — the default
      // (shared) inbox address plus the publish-time flip hook (phase 6). The factory
      // consumes the interface, never concrete strategy types: SharedTopic rides the seam
      // with a resolver that never flips (byte-identical wiring), Namespace consults its
      // live flip set, and a strategy outside the seam falls back to the default topic
      // with no flip hook — all three locked by registration tests.
      var commandInboxResolver = outboxStrategy as ICommandInboxAddressResolver;
      return new TransportPublishStrategy(
        transport, readinessCheck,
        commandInboxResolver?.DefaultCommandInboxAddress ?? SharedTopicOutboxStrategy.DefaultInboxTopic,
        loggerFactory,
        throttleRetryOptions: null, metrics: null,
        postSerializeHookChain: hookChain, jsonOptions: jsonOptions,
        namespaceRouting: commandInboxResolver);
    });

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
