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
  public static IServiceCollection AddRabbitMQTransport(
    this IServiceCollection services,
    string connectionString,
    Action<RabbitMQOptions>? configureOptions = null
  ) {
    ArgumentException.ThrowIfNullOrEmpty(connectionString);
    return _addTransport(services, connectionString, _noNonDefaultNamespaces, configureOptions);
  }

  /// <summary>
  /// Registers RabbitMQ transport across SEVERAL broker namespaces — one
  /// <see cref="IConnection"/> and channel pool per TransportNamespace (transport traffic
  /// classes, topology arc phase 8 / plan resolution 7). A message whose type carries a tag
  /// bound via <c>TagOptions.RouteNamespace</c> publishes through that namespace's connection;
  /// everything else rides <see cref="TransportNamespaces.DefaultKey"/>. The consume side
  /// subscribes its normal entity set in <c>default</c> plus the SAME entity set in every
  /// non-default namespace an actively handled type resolves to.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="namespaceConnectionStrings">
  /// TransportNamespace key → AMQP URI. The reserved <see cref="TransportNamespaces.DefaultKey"/>
  /// key is REQUIRED: it is the namespace every unrouted message — and every routing binding
  /// this host has no connection for — falls back to.
  /// </param>
  /// <param name="configureOptions">Optional configuration callback. The options apply to EVERY
  /// namespace: one broker product, one set of knobs.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// <b>Vhosts are the recommended isolation.</b> A RabbitMQ TransportNamespace is a whole
  /// connection, and the cheapest way to give a traffic class its own exchange/queue namespace,
  /// permissions and resource alarms is a separate vhost on the same broker — one AMQP URI per
  /// vhost (<c>amqp://host/</c> for default, <c>amqp://host/bulk</c> for a bulk class). A
  /// separate cluster works identically; only the URI differs. Entity NAMES are unchanged across
  /// namespaces, so the topology manifest is vhost-independent.
  /// </para>
  /// <para>
  /// <b>Single-namespace guarantee.</b> A map containing only <c>default</c> registers exactly
  /// what <see cref="AddRabbitMQTransport(IServiceCollection, string, Action{RabbitMQOptions})"/>
  /// registers — same services, same lifetimes, ONE connection, and a
  /// <see cref="RabbitMQTransport"/> rather than a routing composition.
  /// </para>
  /// <para>
  /// <b>Configuration.</b> Non-default namespaces can also be added or re-pointed from
  /// <c>Whizbang:Transports:RabbitMQ:Namespaces:&lt;key&gt;</c> (values name a
  /// <c>ConnectionStrings</c> entry) — configuration wins over the code map, per
  /// <see cref="TransportNamespaceConnectionStrings"/>.
  /// </para>
  /// </remarks>
  /// <exception cref="ArgumentException"><paramref name="namespaceConnectionStrings"/> is empty, lacks the default key, or carries a blank connection string.</exception>
  /// <docs>messaging/transports/rabbitmq#transport-namespaces</docs>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQNamespaceRoutingRegistrationTests.cs</tests>
  public static IServiceCollection AddRabbitMQTransport(
    this IServiceCollection services,
    IReadOnlyDictionary<string, string> namespaceConnectionStrings,
    Action<RabbitMQOptions>? configureOptions = null
  ) {
    ArgumentNullException.ThrowIfNull(namespaceConnectionStrings);

    var validated = TransportNamespaceConnectionStrings.MergeAndValidate(
      namespaceConnectionStrings, _noNonDefaultNamespaces, nameof(namespaceConnectionStrings));

    var nonDefault = validated
      .Where(entry => !TransportNamespaces.IsDefault(entry.Key))
      .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    return _addTransport(
      services, validated[TransportNamespaces.DefaultKey], nonDefault, configureOptions);
  }

  /// <summary>
  /// The RabbitMQ transport's configuration section. Its <c>Namespaces</c> child carries the
  /// TransportNamespace connection map (see <see cref="TransportNamespaceConnectionStrings"/>).
  /// </summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Transports:RabbitMQ";

  private static readonly Dictionary<string, string> _noNonDefaultNamespaces = new(StringComparer.Ordinal);

  [SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Startup logging doesn't need high performance optimization")]
  private static IServiceCollection _addTransport(
    IServiceCollection services,
    string connectionString,
    IReadOnlyDictionary<string, string> nonDefaultNamespaces,
    Action<RabbitMQOptions>? configureOptions
  ) {
    // Configure options
    var options = new RabbitMQOptions();
    configureOptions?.Invoke(options);

    // Topology arc phase 8.5 — hand this transport's delivery cap to Core's poison threshold
    // derivation. RabbitMQ supplies no lock-renewal term (no per-delivery lock exists), so that
    // half of the derivation keeps the framework default; see RabbitMQPoisonOptionsPostConfigure.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Microsoft.Extensions.Options.IPostConfigureOptions<Whizbang.Core.Routing.PoisonMessageOptions>,
      RabbitMQPoisonOptionsPostConfigure>(
        _ => new RabbitMQPoisonOptionsPostConfigure(Microsoft.Extensions.Options.Options.Create(options))));

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

    // Connection factory for the NON-default TransportNamespaces (topology arc phase 8).
    // Registered unconditionally so both overloads share one container shape, and never
    // RESOLVED unless a class namespace is configured — a single-namespace host opens exactly
    // one connection.
    services.TryAddSingleton<IRabbitMQNamespaceConnectionFactory>(sp =>
      new RabbitMQNamespaceConnectionFactory(sp.GetService<ILogger<RabbitMQConnectionRetry>>()));

    // ONE connection + channel pool per class namespace, shared by the transport and the
    // provisioner (two views of the same broker). Lazily built, container-disposed.
    services.TryAddSingleton(sp => new RabbitMQNamespaceResources(
      sp.GetRequiredService<IRabbitMQNamespaceConnectionFactory>(),
      options,
      _mergedNamespaces(sp, nonDefaultNamespaces)));

    // Topology ownership-drift surface (phase 5): the provisioner records findings, the
    // health source degrades the "topology" component while any stand.
    services.TryAddSingleton<Whizbang.Core.Routing.TopologyDriftState>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Health.IWhizbangHealthSource,
      Whizbang.Core.Health.TopologyDriftHealthSource>());

    // Backlog-age duty's peek (topology arc phase 10): queue depth from a passive declare over the
    // queues this instance actually consumes from. Depth only — see RabbitMQBacklogPeek for why
    // an age on this transport would cost more than it is worth.
    services.AddSingleton<Whizbang.Core.Transports.IBacklogPeek>(sp =>
      new RabbitMQBacklogPeek(
        sp.GetRequiredService<RabbitMQChannelPool>(),
        () => _consumedQueueNames(sp)));

    // Register infrastructure provisioner for domain topic auto-provisioning + the
    // manifest-driven DARK provisioning (phase 5) — queue args must mirror the transport's,
    // so the SAME options instance flows in.
    services.AddSingleton<IInfrastructureProvisioner>(sp => {
      var pool = sp.GetRequiredService<RabbitMQChannelPool>();
      var logger = sp.GetRequiredService<ILogger<RabbitMQInfrastructureProvisioner>>();
      var driftState = sp.GetRequiredService<Whizbang.Core.Routing.TopologyDriftState>();
      var @default = new RabbitMQInfrastructureProvisioner(pool, logger, options, driftState);

      var resources = sp.GetRequiredService<RabbitMQNamespaceResources>();
      if (resources.Keys.Count == 0) {
        return @default;
      }

      // The manifest is namespace-independent, so the SAME exchange/queue/binding set is
      // declared in each namespace (vhost) — that is what makes the consume-side mirror land
      // on real entities.
      var provisioners = new List<IInfrastructureProvisioner>(resources.Keys.Count + 1) { @default };
      foreach (var namespaceKey in resources.Keys) {
        provisioners.Add(new RabbitMQInfrastructureProvisioner(
          resources.Get(namespaceKey).Pool, logger, options, driftState));
      }

      return new CompositeInfrastructureProvisioner(provisioners);
    });

    // Register transport
    // Broker DLQ import (issue #514 / broker-dlq-import proposal): the ONE
    // ITransportDeadLetterDrainer this hosting registration contributes. Everything resolves
    // LAZILY — constructing the fleet drainer never dials the broker; the import seam resolves
    // IWorkCoordinator per call from a fresh scope, and the absence of a capable coordinator
    // THROWS so the message stays on the broker DLQ instead of being silently lost.
    services.AddSingleton<Whizbang.Core.Transports.ITransportDeadLetterDrainer>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new RabbitMqFleetDeadLetterDrainer(
        connectionFactory: () => sp.GetRequiredService<global::RabbitMQ.Client.IConnection>(),
        activeDeadLetterQueues: () =>
          sp.GetRequiredService<ITransport>() is RabbitMQTransport rmq
            ? rmq.ActiveDeadLetterQueues
            : [],
        importAsync: async (import, ct) => {
          using var scope = scopeFactory.CreateScope();
          var coordinator = scope.ServiceProvider.GetService<Whizbang.Core.Messaging.IWorkCoordinator>()
            ?? throw new InvalidOperationException(
              "Broker DLQ import requires an IWorkCoordinator; none is registered — message stays on the broker DLQ.");
          return await coordinator.ImportBrokerDeadLetterAsync(import, ct).ConfigureAwait(false);
        },
        loggerFactory: sp.GetRequiredService<ILoggerFactory>());
    });

    // Register transport. The NON-default TransportNamespaces (topology arc phase 8) open their
    // own connection + channel pool here; with none configured this is EXACTLY today's
    // single-connection factory — _buildNamespaceRouter returns the transport unchanged.
    services.AddSingleton<ITransport>(sp => {
      var connection = sp.GetRequiredService<IConnection>();
      var pool = sp.GetRequiredService<RabbitMQChannelPool>();
      var logger = sp.GetService<ILogger<RabbitMQTransport>>();
      var discardPolicy = sp.GetService<Whizbang.Core.Routing.IMessageDiscardPolicy>();
      // Poison detector (topology arc phase 8.5) — Core owns the decision, this transport
      // executes it. Optional: a container without the Whizbang worker pipeline keeps pre-8.5
      // behavior. Shared with every namespace peer so one policy governs the whole fleet.
      var poisonDetector = sp.GetService<Whizbang.Core.Routing.IPoisonMessageDetector>();

      var transport = new RabbitMQTransport(
        connection, jsonOptions, pool, options, logger, discardPolicy, poisonDetector);

      // Initialize during registration
      transport.InitializeAsync().GetAwaiter().GetResult();
      logger?.LogInformation("RabbitMQ transport initialized");

      var resources = sp.GetRequiredService<RabbitMQNamespaceResources>();
      if (resources.Keys.Count == 0) {
        return transport;
      }

      var peers = new Dictionary<string, ITransport>(StringComparer.Ordinal);
      foreach (var namespaceKey in resources.Keys) {
        var (peerConnection, peerPool) = resources.Get(namespaceKey);
        var peer = new RabbitMQTransport(
          peerConnection, jsonOptions, peerPool, options, logger, discardPolicy, poisonDetector);
        peer.InitializeAsync().GetAwaiter().GetResult();
        peers[namespaceKey] = peer;

        if (logger?.IsEnabled(LogLevel.Information) == true) {
          logger.LogInformation(
            "RabbitMQ transport initialized for TransportNamespace '{NamespaceKey}'", namespaceKey);
        }
      }

      // The consume-side rule: mirror the normal subscription set into every non-default
      // namespace an actively HANDLED type resolves to. Deferred (a delegate, not a snapshot)
      // because the receptor registry is only complete after every module initializer has run.
      var resolver = sp.GetService<Whizbang.Core.Tags.TransportNamespaceResolver>();
      var registryQuery = sp.GetService<Whizbang.Core.Messaging.IReceptorRegistryQuery>();

      return new NamespaceRoutingTransport(
        transport, peers, () => _activeConsumeNamespaceKeys(resolver, registryQuery));
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

      // TransportNamespace seam (topology arc phase 8): the strategy resolves the message
      // type's tag-bound broker namespace and stamps it on destination metadata; the transport
      // maps the key to a connection. Absent (no AddWhizbang, or no routing bindings) it is a
      // no-op — the destination is byte-identical to today's.
      var transportNamespaces = sp.GetService<Whizbang.Core.Tags.TransportNamespaceResolver>();

      return new TransportPublishStrategy(
        transport, readinessCheck,
        commandInboxResolver?.DefaultCommandInboxAddress ?? SharedTopicOutboxStrategy.DefaultInboxTopic,
        loggerFactory,
        throttleRetryOptions: null, metrics: null,
        postSerializeHookChain: hookChain, jsonOptions: jsonOptions,
        namespaceRouting: commandInboxResolver,
        transportNamespaces: transportNamespaces);
    });

    return services;
  }

  /// <summary>
  /// The effective NON-default TransportNamespace map: the registration callback's map with
  /// <c>Whizbang:Transports:RabbitMQ:Namespaces</c> merged OVER it (configuration wins, the
  /// post-configure idiom). The <c>default</c> entry is deliberately ignored — the default
  /// connection is the container's ambient <see cref="IConnection"/>, which the readiness check
  /// already shares.
  /// </summary>
  private static Dictionary<string, string> _mergedNamespaces(
    IServiceProvider sp,
    IReadOnlyDictionary<string, string> codeNamespaces
  ) {
    var merged = new Dictionary<string, string>(codeNamespaces, StringComparer.Ordinal);
    var configured = TransportNamespaceConnectionStrings.Read(
      sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>(), CONFIGURATION_SECTION);

    foreach (var (key, value) in configured) {
      if (!TransportNamespaces.IsDefault(key)) {
        merged[key] = value;
      }
    }

    return merged;
  }

  /// <summary>
  /// The non-default TransportNamespaces this service consumes from: the distinct keys its
  /// handled message types resolve to. A namespace this service only PUBLISHES to is never
  /// subscribed, so it costs zero broker entities.
  /// </summary>
  private static IReadOnlyList<string> _activeConsumeNamespaceKeys(
    Whizbang.Core.Tags.TransportNamespaceResolver? resolver,
    Whizbang.Core.Messaging.IReceptorRegistryQuery? registryQuery
  ) {
    if (resolver is not { HasBindings: true }) {
      return [];
    }

    var handled = registryQuery?.GetHandledMessages() ?? [];
    return resolver.ResolveConsumeNamespaceKeys(handled.Select(static h => h.MessageTypeName));
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
  /// <summary>
  /// The queue names this instance consumes from, derived from the same subscription set the
  /// consumer worker subscribes with — so the backlog duty samples what this service actually
  /// listens on and never drifts into sampling entities it does not own.
  /// </summary>
  private static IReadOnlyList<string> _consumedQueueNames(IServiceProvider services) {
    var manifest = services.GetService<Whizbang.Core.Routing.TopologyManifest>();
    if (manifest is null) {
      return [];
    }

    return [.. manifest.Subscriptions.Select(s => $"{manifest.ServiceName}-{s.Topic.ToLowerInvariant()}")];
  }

}
