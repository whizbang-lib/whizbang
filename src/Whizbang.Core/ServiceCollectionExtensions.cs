using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Lenses;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;
using Whizbang.Core.Workers;

namespace Whizbang.Core;

/// <summary>
/// Extension methods for registering Whizbang services with dependency injection.
/// Provides the unified AddWhizbang() API.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs</tests>
public static class ServiceCollectionExtensions {
  /// <summary>
  /// Registers Whizbang core infrastructure services and returns a builder for storage configuration.
  /// This is the unified entry point for configuring Whizbang.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>A WhizbangBuilder for configuring storage providers.</returns>
  /// <remarks>
  /// <para>
  /// Use this method to register all Whizbang core services in one call.
  /// This includes message security services (IScopeContextAccessor, IMessageSecurityContextProvider)
  /// which enable security context propagation from message envelopes to receptors.
  /// After calling AddWhizbang(), chain storage configuration methods like:
  /// </para>
  /// <para>
  /// <strong>EF Core with Postgres:</strong>
  /// <code>
  /// services
  ///     .AddWhizbang()
  ///     .WithEFCore&lt;MyDbContext&gt;()
  ///     .WithDriver.Postgres;
  /// </code>
  /// </para>
  /// <para>
  /// <strong>EF Core with InMemory (testing):</strong>
  /// <code>
  /// services
  ///     .AddWhizbang()
  ///     .WithEFCore&lt;MyDbContext&gt;()
  ///     .WithDriver.InMemory;
  /// </code>
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_WithValidServices_ReturnsWhizbangBuilderAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_RegistersCoreServices_SuccessfullyAsync</tests>
  public static WhizbangBuilder AddWhizbang(this IServiceCollection services)
      => AddWhizbang(services, configure: null);

  /// <summary>
  /// Registers Whizbang core infrastructure services with configuration options.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Optional configuration action for Whizbang options.</param>
  /// <returns>A WhizbangBuilder for configuring storage providers.</returns>
  /// <remarks>
  /// <para>
  /// Use this method to configure Whizbang behavior including tag processing.
  /// This method can be called multiple times safely - hook registrations from
  /// all calls are merged together. This enables different parts of your startup
  /// code to register different hooks independently.
  /// </para>
  /// <example>
  /// <code>
  /// // First call registers notification hooks
  /// services.AddWhizbang(options => {
  ///     options.Tags.UseHook&lt;SignalTagAttribute, SignalRNotificationHook&gt;();
  /// });
  ///
  /// // Second call registers telemetry (hooks are merged)
  /// services.AddWhizbang(options => {
  ///     options.Tags.UseHook&lt;TelemetryTagAttribute, OpenTelemetryHook&gt;();
  /// });
  /// </code>
  /// </example>
  /// </remarks>
  /// <docs>operations/configuration/dependency-injection#multiple-addwhizbang-calls</docs>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_CalledMultipleTimes_PreservesHooksFromFirstCall_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_CalledMultipleTimes_MergesHooksFromBothCalls_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_ServiceDescriptor_HasImplementationInstance_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs:AddWhizbang_CalledMultipleTimes_ImplementationInstancePreserved_Async</tests>
  public static WhizbangBuilder AddWhizbang(
      this IServiceCollection services,
      Action<WhizbangCoreOptions>? configure) {
    // Register startup logger (logs Whizbang version via ILogger on first call only)
    if (!services.Any(s => s.ServiceType == typeof(WhizbangCoreOptions))) {
      services.AddSingleton<IHostedService, WhizbangStartupLogger>();
    }

    // Create and configure options
    var coreOptions = new WhizbangCoreOptions();
    configure?.Invoke(coreOptions);

    // Register WhizbangCoreOptions as singleton (only if not already registered)
    // This allows AddWhizbang() to be called multiple times - first call wins for options
    services.TryAddSingleton(coreOptions);

    // Merge tag hooks into existing TagOptions if already registered
    // This allows hooks registered in separate AddWhizbang() calls to be combined
    var existingTagOptions = services.FirstOrDefault(s => s.ServiceType == typeof(TagOptions));
    if (existingTagOptions?.ImplementationInstance is TagOptions existing) {
      // Merge hooks from new options into existing
      // S3267: Loop has side effects (registering hooks via UseHookRegistration) — LINQ not appropriate
#pragma warning disable S3267
      foreach (var hook in coreOptions.Tags.HookRegistrations) {
        if (!existing.HookRegistrations.Any(h => h.AttributeType == hook.AttributeType && h.HookType == hook.HookType)) {
          existing.UseHookRegistration(hook);
        }
      }
#pragma warning restore S3267

      // Merge coalesce bindings too — last-wins per tag applies across AddWhizbang calls,
      // consistent with the single-call Coalesce() semantics.
      foreach (var binding in coreOptions.Tags.CoalesceBindings) {
        existing.UseCoalesceBinding(binding.Key, binding.Value);
      }

      // Same merge rule for the TransportNamespace routing bindings (topology arc phase 8):
      // last-wins per tag across AddWhizbang calls.
      foreach (var binding in coreOptions.Tags.RouteNamespaceBindings) {
        existing.UseRouteNamespaceBinding(binding.Key, binding.Value);
      }
    } else {
      // First registration - add TagOptions
      services.TryAddSingleton(coreOptions.Tags);
    }

    // Tag-policy startup validation: the reserved sys- tag prefix and coalesce-binding
    // ambiguity are checked when the host starts (a hosted service so every assembly's
    // [ModuleInitializer] has populated MessageTagRegistry by then); a violation aborts
    // host.RunAsync() instead of shipping under a policy nobody declared.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TagPolicyStartupValidator>());

    // Inert-concurrency report: ParallelizeStreams defaults to false on BOTH WorkCoordinatorOptions
    // and OrderedStreamProcessorOptions, while the width options default to 16 and 8 — so the
    // shipped default advertises concurrency the runtime will not use, and every deployment
    // inherits that silently. Warning, never a boot failure: serial is slow, not incorrect, and a
    // host that starts today must keep starting after an upgrade.
    services.TryAddEnumerable(
      ServiceDescriptor.Singleton<IHostedService, Whizbang.Core.Diagnostics.InertConcurrencyStartupReporter>());

    // Coalesce-group resolver: the AOT tag lookup the outbox mint seams consult to stamp
    // tag-bound coalesce groups + max-delay floors. Singleton — it caches per-type-name
    // resolution over the (post-startup immutable) tag registry and bindings. Resolution-time
    // is where the built-in audit binding (EnableAudit's knobs -> Coalesce(SystemTags.AUDIT))
    // is applied: all registration ordering has settled by then, and add-if-absent semantics
    // keep any host binding for the tag in charge.
    services.TryAddSingleton(sp => {
      var tagOptions = sp.GetRequiredService<TagOptions>();
      SystemEvents.SystemEventCoalesceDefaults.Apply(
        tagOptions,
        sp.GetService<IOptions<SystemEvents.SystemEventOptions>>()?.Value);
      return new CoalesceGroupResolver(tagOptions, sp.GetService<TimeProvider>());
    });

    // TransportNamespace resolver (topology arc phase 8): the AOT tag lookup the transport
    // boundary consults to map a message type to its broker namespace. Singleton for the same
    // reason as CoalesceGroupResolver — it caches per-type-name resolution over the
    // (post-startup immutable) registry and bindings. The configuration binder is applied at
    // resolution time so `Whizbang:Tags:RouteNamespace:<tag>` wins over the code callback and
    // an operator can re-class traffic without a redeploy.
    services.TryAddSingleton(sp => {
      var tagOptions = sp.GetRequiredService<TagOptions>();
      TagRouteNamespaceConfigurationBinder.Apply(tagOptions, sp.GetService<IConfiguration>());
      return new TransportNamespaceResolver(tagOptions);
    });

    // Control-class resolver (topology arc phase 9): the sibling lookup the RECEIVE boundary
    // consults, answering "is this type-name in the sys-control class?" so the non-durable path is
    // taken for it and for nothing else. Singleton for the same caching reason as above.
    services.TryAddSingleton(_ => new ControlClassResolver());

    // Register TracingOptions with IOptions pattern
    _configureTracingOptions(services, coreOptions);

    // Register IConfiguration binding as PostConfigure (IConfiguration is optional)
    // Use TryAdd to avoid duplicate registrations when AddWhizbang() is called multiple times
    services.TryAddSingleton<IPostConfigureOptions<TracingOptions>>(sp => {
      var config = sp.GetService<IConfiguration>();
      return new TracingOptionsPostConfigure(config);
    });

    // Register hooks with DI (scoped lifetime for access to DbContext, etc.)
    _registerTagHooks(services, coreOptions);

    // Register MessageTagProcessor as Singleton (only if not already registered)
    services.TryAddSingleton<IMessageTagProcessor>(sp => {
      var tagOptions = sp.GetRequiredService<TagOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new MessageTagProcessor(tagOptions, scopeFactory);
    });

    // Register core infrastructure services
    _registerCoreServices(services);

    // Register perspective synchronization services
    _registerPerspectiveSyncServices(services);

    // Register the Phase C work-pump pipeline (heartbeat, claim, inbox-handler,
    // and the four batched flush workers + their channel surfaces).
    // Driver-specific extensions (.WithDriver.Postgres()) wire IWorkCoordinator
    // and may replace IWorkNotificationListener with a real Postgres LISTEN impl.
    // Idempotent: TryAdd / AddOptions inside AddWhizbangWorkers handle repeat calls.
    services.AddWhizbangWorkers();

    // Auto-invoke generated service registration callbacks
    // These are set by source-generated module initializers in consumer assemblies
    ServiceRegistrationCallbacks.InvokeAll(services, coreOptions.Services);

    // Turnkey: if the ASP.NET hosting assembly is loaded, its [ModuleInitializer] set
    // HostingIntegration; fold AddWhizbangAspNet() in automatically (health checks + the
    // schema-availability gate). Opt out via AutoRegisterAspNetHosting = false and call it yourself.
    if (coreOptions.AutoRegisterAspNetHosting) {
      ServiceRegistrationCallbacks.HostingIntegration?.Invoke(services);
    }

    // Auto-invoke WhizbangId provider DI callbacks if any were registered
    WhizbangIdProviderRegistry.InvokeDICallbacks(services);

    return new WhizbangBuilder(services);
  }

  /// <summary>
  /// Configures TracingOptions with programmatic defaults.
  /// </summary>
  private static void _configureTracingOptions(IServiceCollection services, WhizbangCoreOptions coreOptions) {
    services.AddOptions<TracingOptions>()
      .Configure(tracingOptions => {
        tracingOptions.Verbosity = coreOptions.Tracing.Verbosity;
        tracingOptions.Components = coreOptions.Tracing.Components;
        tracingOptions.EnableOpenTelemetry = coreOptions.Tracing.EnableOpenTelemetry;
        tracingOptions.EnableStructuredLogging = coreOptions.Tracing.EnableStructuredLogging;

        foreach (var kvp in coreOptions.Tracing.TracedHandlers) {
          tracingOptions.TracedHandlers[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in coreOptions.Tracing.TracedMessages) {
          tracingOptions.TracedMessages[kvp.Key] = kvp.Value;
        }
      });
  }

  /// <summary>
  /// Registers tag hooks with DI.
  /// </summary>
  private static void _registerTagHooks(IServiceCollection services, WhizbangCoreOptions coreOptions) {
    foreach (var registration in coreOptions.Tags.HookRegistrations) {
      services.TryAddScoped(registration.HookType);
    }
  }

  /// <summary>
  /// Registers core infrastructure services.
  /// </summary>
  private static void _registerCoreServices(IServiceCollection services) {
    services.AddSingleton<ITimeProvider, SystemTimeProvider>();
    services.AddSingleton<Observability.ITraceStore, Observability.InMemoryTraceStore>();
    services.AddSingleton<Policies.IPolicyEngine, Policies.PolicyEngine>();
    services.TryAddScoped<Messaging.ILifecycleContextAccessor, Messaging.AsyncLocalLifecycleContextAccessor>();
    services.TryAddSingleton<ILifecycleCoordinator, LifecycleCoordinator>();

    // Deferred outbox channel for events published outside transaction context
    // Events queued here are drained by the work coordinator in the next lifecycle loop
    services.TryAddSingleton<Messaging.IDeferredOutboxChannel, Messaging.DeferredOutboxChannel>();

    // Inbox channel for routing claimed inbox work to the publisher worker
    services.TryAddSingleton<Messaging.IInboxChannelWriter, Messaging.InboxChannelWriter>();

    // Shared completion counter. The claim loop sizes its outstanding budget from this; the
    // dispatch and publish workers feed it. Registered unconditionally because a MISSING meter
    // silently disables the bound rather than failing loudly — see ClaimWorker, which declines to
    // engage the budget at all without measurement rather than throttle on a rate it cannot read.
    services.TryAddSingleton<Workers.WorkCompletionMeter>();

    // Shared re-claim churn seam. The claim loop sizes its window from re-claim churn, but on the
    // stream-id path the claim returns stream ids and never sees a row's attempt count — only the
    // inbox drain, which fetches the rows, does. Without this seam the window observes zero churn
    // for the life of the process and never adapts: a deployment using stream parallelism logged
    // not one window resize while rows in the same inboxes reached attempt twenty-one.
    //
    // Registered unconditionally and consumed as OPTIONAL by both workers, so a host that
    // constructs them directly still starts — it simply falls back to the unmeasured behavior
    // rather than failing.
    services.TryAddSingleton<Workers.ClaimChurnFeedback>();

    // Housekeeping arbitration. The heavy maintenance sweep runs on a fixed timer and takes locks
    // the completion path also needs, so a sweep landing mid-drain queues every worker's commit
    // behind it — throughput collapses until it finishes, then recovers in a burst. This gates the
    // sweep on SERVICE-wide settledness and keeps it from overlapping integrity work.
    //
    // Registered unconditionally and consumed as OPTIONAL, so a host that constructs the worker
    // directly still starts — it simply keeps the ungated behavior it has today.
    services.TryAddSingleton<Workers.HousekeepingCoordinator>();

    // Register IWorkFlusher - resolves to the same strategy instance for manual flush support
    // IWorkCoordinatorStrategy is registered later by the storage provider (EFCore/Dapper),
    // but the factory lambda resolves at runtime so ordering is fine.
    services.TryAddScoped<Messaging.IWorkFlusher>(sp =>
      (Messaging.IWorkFlusher)sp.GetRequiredService<Messaging.IWorkCoordinatorStrategy>());

    services.AddSingleton<Messaging.ILifecycleMessageDeserializer>(sp => {
      var jsonOptions = sp.GetService<System.Text.Json.JsonSerializerOptions>();
      return new Messaging.JsonLifecycleMessageDeserializer(jsonOptions);
    });

    services.AddSingleton<Messaging.IEnvelopeSerializer>(sp => {
      var jsonOptions = sp.GetService<System.Text.Json.JsonSerializerOptions>();
      return new Messaging.EnvelopeSerializer(jsonOptions);
    });

    services.TryAddSingleton<IServiceInstanceProvider>(sp => {
      var configuration = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
      return new ServiceInstanceProvider(configuration);
    });

    services.AddWhizbangMessageSecurity();

    // The event mint (topology arc phase 4): the composite splitter + the family facade.
    // Turnkey in the core pipeline — never a per-assembly generated registration, which
    // multi-assembly hosts can strip (the silent worker-never-starts signature). TryAdd keeps
    // repeat AddWhizbang() calls idempotent and lets a host substitute its own families first.
    services.TryAddSingleton<Minting.ICompositeFactory, Minting.CompositeFactory>();
    services.TryAddSingleton<Minting.ICollectiveMint, Minting.CollectiveMint>();
    // The checkpoint family reads the control-class options for its TTL derivation (phase 9);
    // register them here too so `AddWhizbang()` alone still yields a mint that derives correctly.
    services.AddOptions<Routing.ControlClassOptions>();
    services.TryAddSingleton<Minting.ICheckpointMint, Minting.CheckpointMint>();
    services.TryAddSingleton<Minting.IEventMint, Minting.EventMint>();

    // Register lens infrastructure
    services.TryAddSingleton<LensOptions>();
    services.TryAddSingleton<SystemEvents.ISystemEventEmitter, SystemEvents.NullSystemEventEmitter>();
    services.TryAddScoped<IScopedLensFactory, ScopedLensFactory>();

    // Register observability metrics (near-zero cost when no OTEL exporter is attached)
    services.TryAddSingleton<WhizbangMetrics>();
    services.TryAddSingleton<WorkCoordinatorMetrics>();
    services.TryAddSingleton<DispatcherMetrics>();
    services.TryAddSingleton<TransportMetrics>();
    services.TryAddSingleton<PerspectiveMetrics>();
    services.TryAddSingleton<LifecycleMetrics>();
    services.TryAddSingleton<InboxMetrics>();
    services.TryAddSingleton<LifecycleCoordinatorMetrics>();
    // Turn-key: registered here so a governor's decisions and the evidence behind them reach
    // OpenTelemetry with no consumer wiring. A component that silently changes concurrency and
    // cannot be observed doing it is undebuggable in production, so the observable path must be
    // the DEFAULT rather than something a host remembers to opt into.
    services.TryAddSingleton<GovernorMetrics>();
    services.TryAddSingleton<EventCategoryMetrics>();
    services.TryAddSingleton<TypeRegistryMetrics>();

    // Cross-worker dedup: prevents same message+stage from firing twice
    services.TryAddSingleton<Messaging.LifecycleStageTracker>();

    // Register IPerspectiveRebuilder so callers (PerspectiveMigrationWorker, the
    // RebuildPerspectiveCommand receptor) can resolve it. The rebuilder itself only depends
    // on IServiceScopeFactory + ILogger — Core-level services — so registration lives here.
    // Cursor persistence is routed through IPerspectiveCheckpointCompleter, which is
    // registered by the storage driver extension (.WithDriver.Postgres or AddWhizbangPostgres).
    services.TryAddSingleton<Perspectives.IPerspectiveRebuilder, Perspectives.PerspectiveRebuilder>();
  }

  /// <summary>
  /// Registers perspective synchronization services.
  /// </summary>
  private static void _registerPerspectiveSyncServices(IServiceCollection services) {
    services.TryAddSingleton<IDebuggerAwareClock, DebuggerAwareClock>();
    services.TryAddSingleton<ITracer, Tracer>();
    services.TryAddSingleton<IPerspectiveSyncSignaler, LocalSyncSignaler>();

    services.TryAddScoped<IScopedEventTracker>(_ => {
      var tracker = new ScopedEventTracker();
      ScopedEventTrackerAccessor.CurrentTracker = tracker;
      return tracker;
    });

    services.TryAddScoped<IPerspectiveSyncAwaiter, PerspectiveSyncAwaiter>();
    services.TryAddSingleton<ISyncEventTracker, SyncEventTracker>();
    services.TryAddSingleton<IEventCompletionAwaiter, EventCompletionAwaiter>();
    services.TryAddSingleton<ITrackedEventTypeRegistry, TrackedEventTypeRegistry>();
    // Temporal engine (F2): the home-grown recurrence next-fire factory. TryAdd so a developer's
    // own IRecurrenceRuleFactory (the override hook — e.g. a different cron parser) takes precedence.
    services.TryAddSingleton<Temporal.IRecurrenceRuleFactory, Temporal.DefaultRecurrenceRuleFactory>();
  }

  /// <summary>
  /// PostConfigure implementation for TracingOptions that binds from IConfiguration.
  /// Extracted to reduce cognitive complexity of AddWhizbang.
  /// </summary>
  private sealed class TracingOptionsPostConfigure(IConfiguration? config) : IPostConfigureOptions<TracingOptions> {
    private readonly IConfiguration? _config = config;

    /// <inheritdoc/>
    public void PostConfigure(string? name, TracingOptions options) {
      if (_config == null) {
        return;
      }

      var section = _config.GetSection("Whizbang:Tracing");
      if (!section.Exists()) {
        return;
      }

      _bindVerbosity(section, options);
      _bindComponents(section, options);
      _bindBooleans(section, options);
      _bindTracedHandlers(section, options);
      _bindTracedMessages(section, options);
    }

    private static void _bindVerbosity(IConfigurationSection section, TracingOptions options) {
      var value = section["Verbosity"];
      if (!string.IsNullOrEmpty(value) &&
          Enum.TryParse<TraceVerbosity>(value, ignoreCase: true, out var verbosity)) {
        options.Verbosity = verbosity;
      }
    }

    private static void _bindComponents(IConfigurationSection section, TracingOptions options) {
      var value = section["Components"];
      if (!string.IsNullOrEmpty(value) &&
          Enum.TryParse<TraceComponents>(value, ignoreCase: true, out var components)) {
        options.Components = components;
      }
    }

    private static void _bindBooleans(IConfigurationSection section, TracingOptions options) {
      var enableOtelValue = section["EnableOpenTelemetry"];
      if (!string.IsNullOrEmpty(enableOtelValue) && bool.TryParse(enableOtelValue, out var enableOtel)) {
        options.EnableOpenTelemetry = enableOtel;
      }

      var enableLoggingValue = section["EnableStructuredLogging"];
      if (!string.IsNullOrEmpty(enableLoggingValue) && bool.TryParse(enableLoggingValue, out var enableLogging)) {
        options.EnableStructuredLogging = enableLogging;
      }
    }

    private static void _bindTracedHandlers(IConfigurationSection section, TracingOptions options) {
      var handlersSection = section.GetSection("TracedHandlers");
      if (!handlersSection.Exists()) {
        return;
      }

      foreach (var child in handlersSection.GetChildren()) {
        if (!string.IsNullOrEmpty(child.Value) &&
            Enum.TryParse<TraceVerbosity>(child.Value, ignoreCase: true, out var handlerVerbosity)) {
          options.TracedHandlers[child.Key] = handlerVerbosity;
        }
      }
    }

    private static void _bindTracedMessages(IConfigurationSection section, TracingOptions options) {
      var messagesSection = section.GetSection("TracedMessages");
      if (!messagesSection.Exists()) {
        return;
      }

      foreach (var child in messagesSection.GetChildren()) {
        if (!string.IsNullOrEmpty(child.Value) &&
            Enum.TryParse<TraceVerbosity>(child.Value, ignoreCase: true, out var messageVerbosity)) {
          options.TracedMessages[child.Key] = messageVerbosity;
        }
      }
    }
  }

  /// <summary>
  /// Decorates an existing <see cref="IEventStore"/> registration with Whizbang decorators.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method uses the decorator pattern to wrap an existing IEventStore with:
  /// <list type="number">
  /// <item><see cref="Messaging.SecurityContextEventStoreDecorator"/> - propagates security context</item>
  /// <item><see cref="Messaging.SyncTrackingEventStoreDecorator"/> - tracks events for sync</item>
  /// <item><see cref="Messaging.AppendAndWaitEventStoreDecorator"/> - enables AppendAndWaitAsync</item>
  /// </list>
  /// </para>
  /// <para>
  /// Call this method AFTER registering your IEventStore implementation.
  /// This is typically called automatically by the data provider (EF Core, Dapper).
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs</tests>
  public static IServiceCollection DecorateEventStoreWithSyncTracking(
      this IServiceCollection services) {
    // Find existing IEventStore registration
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(Messaging.IEventStore));
    if (descriptor == null) {
      // No IEventStore registered yet - skip decoration silently
      // This supports scenarios where decoration is called before the event store is registered
      return services;
    }

    // Remove existing registration
    services.Remove(descriptor);

    // Re-register with the decorator wrapping the original
    // Use the same lifetime as the original registration (typically Scoped for EF Core)
    if (descriptor.Lifetime == ServiceLifetime.Scoped) {
      // Register the inner store factory
      if (descriptor.ImplementationFactory != null) {
        services.AddScoped<InnerEventStoreHolder>(sp =>
            new InnerEventStoreHolder(descriptor.ImplementationFactory(sp)));
      } else if (descriptor.ImplementationType != null) {
        services.AddScoped<InnerEventStoreHolder>(sp =>
            new InnerEventStoreHolder(ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType)));
      }

      // Register the decorator stack
      services.AddScoped<Messaging.IEventStore>(sp => {
        var holder = sp.GetRequiredService<InnerEventStoreHolder>();

        // Layer 0: Upcasting (innermost - transforms stored events to their current shape on
        // read, so every outer decorator and consumer sees upcasted events). Only wrapped when
        // upcasters are registered, so non-upcasting consumers pay nothing.
        var upcasterPipeline = sp.GetService<Messaging.EventUpcasterPipeline>();
        var innerStore = upcasterPipeline is { HasAny: true }
            ? new Messaging.UpcastingEventStoreDecorator((Messaging.IEventStore)holder.Instance, upcasterPipeline)
            : (Messaging.IEventStore)holder.Instance;

        // Layer 1: SecurityContext (propagates security context)
        var withSecurityContext = new Messaging.SecurityContextEventStoreDecorator(innerStore);

        // Layer 2: SyncTracking (tracks events for perspective sync)
        var scopedTracker = sp.GetService<IScopedEventTracker>();
        var envelopeRegistry = sp.GetService<Observability.IEnvelopeRegistry>();
        var syncEventTracker = sp.GetService<ISyncEventTracker>();
        var typeRegistry = sp.GetService<ITrackedEventTypeRegistry>();
        var withSyncTracking = new Messaging.SyncTrackingEventStoreDecorator(
            withSecurityContext,
            scopedTracker,
            envelopeRegistry,
            syncEventTracker,
            typeRegistry);

        // Layer 3: AppendAndWait (outermost - enables AppendAndWaitAsync)
        var syncAwaiter = sp.GetRequiredService<IPerspectiveSyncAwaiter>();
        var eventCompletionAwaiter = sp.GetService<IEventCompletionAwaiter>();
        return new Messaging.AppendAndWaitEventStoreDecorator(
            withSyncTracking,
            syncAwaiter,
            eventCompletionAwaiter,
            scopedTracker);
      });
    } else {
      // Singleton lifetime
      if (descriptor.ImplementationInstance != null) {
        services.AddSingleton(new InnerEventStoreHolder(descriptor.ImplementationInstance));
      } else if (descriptor.ImplementationFactory != null) {
        services.AddSingleton(sp => new InnerEventStoreHolder(descriptor.ImplementationFactory(sp)));
      } else if (descriptor.ImplementationType != null) {
        services.AddSingleton(sp => new InnerEventStoreHolder(
            ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType)));
      }

      // Register the decorator stack
      services.AddSingleton<Messaging.IEventStore>(sp => {
        var holder = sp.GetRequiredService<InnerEventStoreHolder>();

        // Layer 0: Upcasting (innermost - transforms stored events to their current shape on
        // read, so every outer decorator and consumer sees upcasted events). Only wrapped when
        // upcasters are registered, so non-upcasting consumers pay nothing.
        var upcasterPipeline = sp.GetService<Messaging.EventUpcasterPipeline>();
        var innerStore = upcasterPipeline is { HasAny: true }
            ? new Messaging.UpcastingEventStoreDecorator((Messaging.IEventStore)holder.Instance, upcasterPipeline)
            : (Messaging.IEventStore)holder.Instance;

        // Layer 1: SecurityContext (propagates security context)
        var withSecurityContext = new Messaging.SecurityContextEventStoreDecorator(innerStore);

        // Layer 2: SyncTracking (tracks events for perspective sync)
        var syncEventTracker = sp.GetService<ISyncEventTracker>();
        var typeRegistry = sp.GetService<ITrackedEventTypeRegistry>();
        var withSyncTracking = new Messaging.SyncTrackingEventStoreDecorator(
            withSecurityContext,
            tracker: null, // Scoped tracker not available in singleton
            envelopeRegistry: null,
            syncEventTracker,
            typeRegistry);

        // Layer 3: AppendAndWait (outermost - enables AppendAndWaitAsync)
        var syncAwaiter = sp.GetRequiredService<IPerspectiveSyncAwaiter>();
        var eventCompletionAwaiter = sp.GetService<IEventCompletionAwaiter>();
        return new Messaging.AppendAndWaitEventStoreDecorator(
            withSyncTracking,
            syncAwaiter,
            eventCompletionAwaiter,
            scopedEventTracker: null); // Scoped tracker not available in singleton
      });
    }

    return services;
  }

  /// <summary>
  /// Holder for the inner event store instance to enable decoration.
  /// </summary>
  private sealed class InnerEventStoreHolder(object instance) {
    public object Instance { get; } = instance;
  }
}
