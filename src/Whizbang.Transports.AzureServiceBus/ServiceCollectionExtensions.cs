using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    return _addTransport(services, connectionString, _noNonDefaultNamespaces, configureOptions);
  }

  /// <summary>
  /// Registers Azure Service Bus transport across SEVERAL broker namespaces — one
  /// <see cref="Azure.Messaging.ServiceBus.ServiceBusClient"/> per TransportNamespace (transport
  /// traffic classes, topology arc phase 8 / #424 increment 2). A message whose type carries a
  /// tag bound via <c>TagOptions.RouteNamespace</c> publishes through that namespace's client;
  /// everything else rides <see cref="TransportNamespaces.DefaultKey"/>. The consume side
  /// subscribes its normal entity set in <c>default</c> plus the SAME entity set in every
  /// non-default namespace an actively handled type resolves to.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="namespaceConnectionStrings">
  /// TransportNamespace key → Azure Service Bus connection string. The reserved
  /// <see cref="TransportNamespaces.DefaultKey"/> key is REQUIRED: it is the namespace every
  /// unrouted message — and every routing binding this host has no connection for — falls back
  /// to. Additional keys are the traffic classes.
  /// </param>
  /// <param name="configureOptions">Optional configuration callback for transport options. The
  /// options apply to EVERY namespace: one broker product, one set of knobs.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// <b>Single-namespace guarantee.</b> A map containing only <c>default</c> registers exactly
  /// what <see cref="AddAzureServiceBusTransport(IServiceCollection, string, Action{AzureServiceBusOptions})"/>
  /// registers — same services, same lifetimes, ONE client, and an
  /// <see cref="AzureServiceBusTransport"/> rather than a routing composition. Multi-namespace
  /// support costs a single-namespace deployment nothing.
  /// </para>
  /// <para>
  /// <b>Configuration.</b> Non-default namespaces can also be added or re-pointed from
  /// <c>Whizbang:Transports:AzureServiceBus:Namespaces:&lt;key&gt;</c> (values name a
  /// <c>ConnectionStrings</c> entry) — configuration wins over the code map, per
  /// <see cref="TransportNamespaceConnectionStrings"/>. The DEFAULT connection stays the one
  /// this call was given, because the container's ambient <c>ServiceBusClient</c> (readiness
  /// check, dead-letter drainer) is built from it.
  /// </para>
  /// </remarks>
  /// <exception cref="ArgumentException"><paramref name="namespaceConnectionStrings"/> is empty, lacks the default key, or carries a blank connection string.</exception>
  /// <docs>messaging/transports/azure-service-bus#transport-namespaces</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusNamespaceRoutingRegistrationTests.cs</tests>
  public static IServiceCollection AddAzureServiceBusTransport(
    this IServiceCollection services,
    IReadOnlyDictionary<string, string> namespaceConnectionStrings,
    Action<AzureServiceBusOptions>? configureOptions = null
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

  private static readonly Dictionary<string, string> _noNonDefaultNamespaces = new(StringComparer.Ordinal);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Startup initialization logging - infrequent calls during DI registration")]
  private static IServiceCollection _addTransport(
    IServiceCollection services,
    string connectionString,
    IReadOnlyDictionary<string, string> nonDefaultNamespaces,
    Action<AzureServiceBusOptions>? configureOptions
  ) {
    // Registration-time snapshot: DI-shape decisions (whether the admin client is registered)
    // must be made while the container is still mutable, so they see the code callback only.
    // Runtime knobs are resolved through the options pipeline below, where configuration
    // (Whizbang:Transports:AzureServiceBus) post-configures OVER the callback — an operator
    // can correct any runtime value without a redeploy.
    var registrationOptions = new AzureServiceBusOptions();
    configureOptions?.Invoke(registrationOptions);

    services.AddOptions<AzureServiceBusOptions>();
    if (configureOptions is not null) {
      services.Configure(configureOptions);
    }

    services.TryAddSingleton<Microsoft.Extensions.Options.IPostConfigureOptions<AzureServiceBusOptions>>(sp =>
      new AzureServiceBusOptionsPostConfigure(sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>()));

    // Topology arc phase 8.5 — hand this transport's lock/delivery knobs to Core's poison
    // threshold derivation, so the age default tracks the transport's own configuration.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Microsoft.Extensions.Options.IPostConfigureOptions<Whizbang.Core.Routing.PoisonMessageOptions>,
      AsbPoisonOptionsPostConfigure>());

    // Client factory for the NON-default TransportNamespaces (topology arc phase 8). Registered
    // unconditionally so both overloads share one container shape, and never RESOLVED unless a
    // class namespace is actually configured — a single-namespace host opens exactly one client.
    services.TryAddSingleton<IServiceBusNamespaceClientFactory>(sp =>
      new ServiceBusNamespaceClientFactory(sp.GetService<ILogger<AzureServiceBusConnectionRetry>>()));

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
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AzureServiceBusOptions>>().Value;
        var logger = sp.GetService<ILogger<AzureServiceBusConnectionRetry>>();
        if (logger?.IsEnabled(LogLevel.Information) == true) {
          var initialAttempts = options.InitialRetryAttempts;
          var retryIndefinitely = options.RetryIndefinitely;
          logger.LogInformation("Creating Azure Service Bus client with retry (initial {InitialAttempts} attempts, then indefinitely={RetryIndefinitely})", initialAttempts, retryIndefinitely);
        }

        var connectionRetry = new AzureServiceBusConnectionRetry(options, logger);
        return connectionRetry.CreateClientWithRetryAsync(connectionString).GetAwaiter().GetResult();
      });
    }

    // Auto-register admin client when AutoProvisionInfrastructure is enabled
    // This allows the transport to auto-create topics and subscriptions.
    // Registration-time decision by design: it shapes the container, so only the code
    // callback can influence it — configuration cannot (see AzureServiceBusOptionsPostConfigure).
    if (registrationOptions.AutoProvisionInfrastructure) {
      var hasAdminClient = services.Any(sd => sd.ServiceType == typeof(IServiceBusAdminClient));
      if (!hasAdminClient) {
        services.AddSingleton<IServiceBusAdminClient>(_ => {
          var adminClient = new ServiceBusAdministrationClient(connectionString);
          return new ServiceBusAdminClientWrapper(adminClient);
        });
      }
    }

    // Register transport as singleton, injecting shared client and optional admin client.
    // The NON-default TransportNamespaces (topology arc phase 8) resolve their own clients
    // here too; with no extra namespaces configured this is EXACTLY today's single-client
    // factory — _buildNamespaceRouter short-circuits to the default transport instance.
    services.AddSingleton<ITransport>(sp => {
      var logger = sp.GetService<ILogger<AzureServiceBusTransport>>();
      var client = sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>();

      // Get admin client if available (for auto-provisioning)
      var adminClient = sp.GetService<IServiceBusAdminClient>();

      var transport = _createTransport(sp, jsonOptions, client, adminClient);

      // IMPORTANT: Initialize transport during registration to verify connectivity
      // This ensures the application won't start if Service Bus is unreachable
      try {
        transport.InitializeAsync().GetAwaiter().GetResult();
        logger?.LogInformation("Transport initialized (using shared client)");
#pragma warning disable S2139 // Intentional log-and-rethrow: DI factory exceptions are often swallowed or wrapped, so logging here ensures the root cause is captured for diagnostics.
      } catch (Exception ex) {
        logger?.LogError(ex, "Failed to initialize transport during registration");
        throw;
      }
#pragma warning restore S2139

      return _buildNamespaceRouter(sp, jsonOptions, transport, nonDefaultNamespaces);
    });

    // Managed-resource health: the idle ops-rate self-check DEGRADES the "transport" component
    // while the projected idle broker-op rate exceeds the warning threshold (it recovers when a
    // re-projection lands back under it — adaptive acceptor decay can do that on its own). This
    // aggregates alongside the core connectivity source for the same component; worst status
    // wins, so connectivity faults and idle-churn degradation surface independently.
    services.AddSingleton<Whizbang.Core.Health.IWhizbangHealthSource>(sp =>
      new AsbOpsRateHealthSource(sp.GetRequiredService<ITransport>()));

    // Backlog-age duty's peek (topology arc phase 10): the transport contributes the admin-plane
    // sampler; the duty itself lives in Core and is inert without one. Enumerable so a
    // multi-transport host contributes one peek per transport.
    services.AddSingleton<Whizbang.Core.Transports.IBacklogPeek>(sp =>
      new AsbBacklogPeek(
        sp.GetRequiredService<ITransport>(),
        sp.GetService<Whizbang.Core.Tags.TagOptions>()));

    // Per-class ops-rate gauge feed (topology arc phase 10): the self-check already computes this
    // number to decide whether to degrade health; publishing it per namespace is what makes the
    // failure mode of the motivating incident — invisible while healthy — a graph.
    services.AddSingleton<Whizbang.Core.Transports.ITrafficClassOpsRateSource>(sp =>
      new AsbTrafficClassOpsRateSource(
        sp.GetRequiredService<ITransport>(),
        sp.GetService<Whizbang.Core.Tags.TagOptions>()));

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
      var loggerFactory = sp.GetService<ILoggerFactory>();

      // Post-serialize hook chain + JsonSerializerOptions are optional; when
      // AddWhizbangBodyOffload (or any AddWhizbangPostSerializeHook<T>) is
      // registered AND the transport's JsonSerializerOptions resolver is
      // available, the strategy will JIT-serialize, run the chain, stamp
      // whizbang.body-size, and validate against the 256 KB ASB Standard
      // ceiling. Both null → existing fast-path behavior (no pre-serialize).
      var hookChain = sp.GetService<Whizbang.Core.Offloads.PostSerializeHookChain>();
      var jsonOptions = sp.GetService<System.Text.Json.JsonSerializerOptions>();

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
      // maps the key to a client. Absent (no AddWhizbang, or no routing bindings) it is a
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
  /// Builds one <see cref="AzureServiceBusTransport"/> over <paramref name="client"/> with every
  /// optional collaborator the container offers. Shared by the default namespace and by each
  /// non-default TransportNamespace, so a class namespace behaves exactly like the default one
  /// (same discard policy, same registries, same binder) — only the broker differs.
  /// </summary>
  private static AzureServiceBusTransport _createTransport(
    IServiceProvider sp,
    System.Text.Json.JsonSerializerOptions jsonOptions,
    Azure.Messaging.ServiceBus.ServiceBusClient client,
    IServiceBusAdminClient? adminClient
  ) {
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AzureServiceBusOptions>>().Value;
    var logger = sp.GetService<ILogger<AzureServiceBusTransport>>();

    // Slice 2 — receptor + perspective registries enable receive-time drop of messages
    // this service has no consumer for. Both optional so non-Whizbang-host scenarios
    // (test fakes, custom bootstraps) keep working without the filter.
    var receptorRegistry = sp.GetService<Whizbang.Core.Messaging.IReceptorRegistry>();
    var perspectiveRegistry = sp.GetService<Whizbang.Core.Perspectives.IPerspectiveRunnerRegistry>();

    // Slice 5 — opt-in raw receptor registry. When typed binder misses but a raw receptor
    // is registered for the inner message type, dispatch routes there instead of dropping.
    var rawReceptorRegistry = sp.GetService<Whizbang.Core.Messaging.IRawReceptorRegistry>();

    // Slice 4 — multi-pass type binder fallback for envelope types not in the local
    // JsonContextRegistry. Optional; transport instantiates a default when missing.
    var typeBinder = sp.GetService<Whizbang.Core.Messaging.IMessageTypeBinder>();

    // Shared discard policy — routes NoLocalConsumer drops through structured
    // logging + the whizbang.message.skipped OTel counter (instead of WARN spam).
    var discardPolicy = sp.GetService<Whizbang.Core.Routing.IMessageDiscardPolicy>();

    // Absorbed namespaces — an unconsumed event on one of these is KEPT (persisted) at the
    // ASB receive gate instead of dropped, mirroring the core MessageDiscardPolicy behavior.
    var absorbedNamespaces = sp.GetService<Microsoft.Extensions.Options.IOptions<Whizbang.Core.Routing.RoutingOptions>>()
      ?.Value.AbsorbedNamespaces;

    // Poison detector (topology arc phase 8.5) — Core owns the decision, this transport executes
    // it. Optional: a container without the Whizbang worker pipeline keeps pre-8.5 behavior.
    var poisonDetector = sp.GetService<Whizbang.Core.Routing.IPoisonMessageDetector>();

    return new AzureServiceBusTransport(
      client, jsonOptions, options, logger, adminClient,
      receptorRegistry: receptorRegistry,
      perspectiveRegistry: perspectiveRegistry,
      rawReceptorRegistry: rawReceptorRegistry,
      typeBinder: typeBinder,
      discardPolicy: discardPolicy,
      absorbedNamespaces: absorbedNamespaces,
      timeProvider: null,
      poisonDetector: poisonDetector);
  }

  /// <summary>
  /// Composes the per-TransportNamespace clients behind one <see cref="ITransport"/>. Returns
  /// <paramref name="defaultTransport"/> UNCHANGED when no non-default namespace is configured
  /// — the locked single-namespace guarantee: no wrapper, no extra client, nothing to review
  /// differently in a single-namespace deployment.
  /// </summary>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Startup initialization logging - infrequent calls during DI registration")]
  private static ITransport _buildNamespaceRouter(
    IServiceProvider sp,
    System.Text.Json.JsonSerializerOptions jsonOptions,
    AzureServiceBusTransport defaultTransport,
    IReadOnlyDictionary<string, string> codeNamespaces
  ) {
    // Configuration merges OVER the code map (the post-configure idiom). The 'default' entry is
    // deliberately ignored here: the default client is the container's ambient ServiceBusClient,
    // which the readiness check and dead-letter drainer already share.
    var configuration = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
    var configured = TransportNamespaceConnectionStrings.Read(
      configuration, AzureServiceBusOptionsPostConfigure.CONFIGURATION_SECTION);

    var merged = new Dictionary<string, string>(codeNamespaces, StringComparer.Ordinal);
    foreach (var (key, value) in configured) {
      if (!TransportNamespaces.IsDefault(key)) {
        merged[key] = value;
      }
    }

    if (merged.Count == 0) {
      return defaultTransport;
    }

    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AzureServiceBusOptions>>().Value;
    var clientFactory = sp.GetRequiredService<IServiceBusNamespaceClientFactory>();
    var logger = sp.GetService<ILogger<AzureServiceBusTransport>>();

    var peers = new Dictionary<string, ITransport>(StringComparer.Ordinal);
    foreach (var (namespaceKey, namespaceConnectionString) in merged) {
      var client = clientFactory.CreateClient(namespaceKey, namespaceConnectionString, options);
      var adminClient = clientFactory.CreateAdminClient(namespaceKey, namespaceConnectionString, options);

      var peer = _createTransport(sp, jsonOptions, client, adminClient);
      peer.InitializeAsync().GetAwaiter().GetResult();
      peers[namespaceKey] = peer;

      if (logger?.IsEnabled(LogLevel.Information) == true) {
        logger.LogInformation(
          "Transport initialized for TransportNamespace '{NamespaceKey}'", namespaceKey);
      }
    }

    // The consume-side rule: mirror the normal subscription set into every non-default
    // namespace an actively HANDLED type resolves to. Deferred (a delegate, not a snapshot)
    // because the receptor registry is only complete after every module initializer has run.
    var resolver = sp.GetService<Whizbang.Core.Tags.TransportNamespaceResolver>();
    var registryQuery = sp.GetService<Whizbang.Core.Messaging.IReceptorRegistryQuery>();

    return new NamespaceRoutingTransport(
      defaultTransport, peers, () => _activeConsumeNamespaceKeys(resolver, registryQuery));
  }

  /// <summary>
  /// The non-default TransportNamespaces this service consumes from: the distinct keys its
  /// handled message types resolve to. A namespace this service only PUBLISHES to is never
  /// subscribed, so it costs zero broker entities and zero acceptor slots.
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
  /// Registers infrastructure provisioner for automatic domain topic creation.
  /// This requires a ServiceBusAdministrationClient which needs Manage permissions.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="connectionString">The Azure Service Bus connection string with Manage permissions.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// This is separate from AddAzureServiceBusTransport because topic provisioning
  /// requires elevated permissions (Manage) that may not be available in all environments.
  /// In production, topics are often pre-provisioned via infrastructure-as-code.
  /// </remarks>
  public static IServiceCollection AddAzureServiceBusProvisioner(
    this IServiceCollection services,
    string connectionString
  ) {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    return _addProvisioner(services, connectionString, _noNonDefaultNamespaces);
  }

  /// <summary>
  /// Registers infrastructure provisioning across SEVERAL broker namespaces (transport traffic
  /// classes, topology arc phase 8): the topology manifest is provisioned into EVERY configured
  /// TransportNamespace, because the consume-side mirror needs the same entity set to exist in
  /// each namespace it subscribes.
  /// </summary>
  /// <param name="services">The service collection to register with.</param>
  /// <param name="namespaceConnectionStrings">
  /// TransportNamespace key → connection string with Manage permissions. The reserved
  /// <see cref="TransportNamespaces.DefaultKey"/> key is REQUIRED.
  /// </param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// A <c>default</c>-only map registers exactly what the single-connection-string overload
  /// registers — one admin client, one provisioner, no composition.
  /// </remarks>
  /// <exception cref="ArgumentException"><paramref name="namespaceConnectionStrings"/> is empty, lacks the default key, or carries a blank connection string.</exception>
  /// <docs>messaging/transports/azure-service-bus#transport-namespaces</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusNamespaceRoutingRegistrationTests.cs:AddAzureServiceBusProvisioner_MultiNamespaceMap_ProvisionsEveryNamespaceAsync</tests>
  public static IServiceCollection AddAzureServiceBusProvisioner(
    this IServiceCollection services,
    IReadOnlyDictionary<string, string> namespaceConnectionStrings
  ) {
    ArgumentNullException.ThrowIfNull(namespaceConnectionStrings);

    var validated = TransportNamespaceConnectionStrings.MergeAndValidate(
      namespaceConnectionStrings, _noNonDefaultNamespaces, nameof(namespaceConnectionStrings));

    var nonDefault = validated
      .Where(entry => !TransportNamespaces.IsDefault(entry.Key))
      .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    return _addProvisioner(services, validated[TransportNamespaces.DefaultKey], nonDefault);
  }

  private static IServiceCollection _addProvisioner(
    IServiceCollection services,
    string connectionString,
    IReadOnlyDictionary<string, string> nonDefaultNamespaces
  ) {
    // Register admin client wrapper
    services.AddSingleton<IServiceBusAdminClient>(_ => {
      var adminClient = new ServiceBusAdministrationClient(connectionString);
      return new ServiceBusAdminClientWrapper(adminClient);
    });

    // Topology ownership-drift surface (phase 5): the provisioner records findings, the
    // health source degrades the "topology" component while any stand.
    services.TryAddSingleton<Whizbang.Core.Routing.TopologyDriftState>();
    services.TryAddEnumerable(ServiceDescriptor.Singleton<
      Whizbang.Core.Health.IWhizbangHealthSource,
      Whizbang.Core.Health.TopologyDriftHealthSource>());

    // Register infrastructure provisioner
    services.AddSingleton<IInfrastructureProvisioner>(sp => {
      var adminClient = sp.GetRequiredService<IServiceBusAdminClient>();
      var logger = sp.GetRequiredService<ILogger<ServiceBusInfrastructureProvisioner>>();
      // Manifest-provisioned subscriptions must use the SAME session/lock/delivery settings
      // as subscribe-time auto-provision — resolve the transport options when configured.
      var options = sp.GetService<Microsoft.Extensions.Options.IOptions<AzureServiceBusOptions>>()?.Value;
      var driftState = sp.GetRequiredService<Whizbang.Core.Routing.TopologyDriftState>();
      var @default = new ServiceBusInfrastructureProvisioner(adminClient, logger, options, driftState);

      // Configuration merges OVER the code map, same as the transport registration — an
      // operator adding a traffic-class namespace gets its entities provisioned too.
      var merged = new Dictionary<string, string>(nonDefaultNamespaces, StringComparer.Ordinal);
      foreach (var (key, value) in TransportNamespaceConnectionStrings.Read(
          sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>(),
          AzureServiceBusOptionsPostConfigure.CONFIGURATION_SECTION)) {
        if (!TransportNamespaces.IsDefault(key)) {
          merged[key] = value;
        }
      }

      if (merged.Count == 0) {
        return @default;
      }

      // The manifest is namespace-independent, so the SAME entity set is provisioned into each
      // namespace — that is what makes the consume-side mirror land on real entities.
      var provisioners = new List<IInfrastructureProvisioner>(merged.Count + 1) { @default };
      foreach (var namespaceConnectionString in merged
          .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
          .Select(static entry => entry.Value)) {
        provisioners.Add(new ServiceBusInfrastructureProvisioner(
          new ServiceBusAdminClientWrapper(new ServiceBusAdministrationClient(namespaceConnectionString)),
          logger, options, driftState));
      }

      return new CompositeInfrastructureProvisioner(provisioners);
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
