using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    } else {
      // First registration - add TagOptions
      services.TryAddSingleton(coreOptions.Tags);
    }

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

    // Auto-invoke generated service registration callbacks
    // These are set by source-generated module initializers in consumer assemblies
    ServiceRegistrationCallbacks.InvokeAll(services, coreOptions.Services);

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
    services.TryAddSingleton<LifecycleCoordinatorMetrics>();
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
  }

  /// <summary>
  /// PostConfigure implementation for TracingOptions that binds from IConfiguration.
  /// Extracted to reduce cognitive complexity of AddWhizbang.
  /// </summary>
  private sealed class TracingOptionsPostConfigure(IConfiguration? config) : IPostConfigureOptions<TracingOptions> {
    private readonly IConfiguration? _config = config;

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

        // Layer 1: SecurityContext (innermost - propagates security context)
        var withSecurityContext = new Messaging.SecurityContextEventStoreDecorator(
            (Messaging.IEventStore)holder.Instance);

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

        // Layer 1: SecurityContext (innermost - propagates security context)
        var withSecurityContext = new Messaging.SecurityContextEventStoreDecorator(
            (Messaging.IEventStore)holder.Instance);

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
