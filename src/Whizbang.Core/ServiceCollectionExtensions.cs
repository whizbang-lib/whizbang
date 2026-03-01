using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.Configuration;
using Whizbang.Core.Diagnostics;
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
  /// </para>
  /// <example>
  /// <code>
  /// services.AddWhizbang(options => {
  ///     options.Tags.UseHook&lt;NotificationTagAttribute, SignalRNotificationHook&gt;();
  ///     options.TagProcessingMode = TagProcessingMode.AfterReceptorCompletion;
  /// });
  /// </code>
  /// </example>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/ServiceCollectionExtensionsTests.cs</tests>
  public static WhizbangBuilder AddWhizbang(
      this IServiceCollection services,
      Action<WhizbangCoreOptions>? configure) {
    // Create and configure options
    var coreOptions = new WhizbangCoreOptions();
    configure?.Invoke(coreOptions);

    // Register WhizbangCoreOptions as singleton
    services.AddSingleton(coreOptions);

    // Register TagOptions as singleton
    services.AddSingleton(coreOptions.Tags);

    // Register hooks with DI (scoped lifetime for access to DbContext, etc.)
    foreach (var registration in coreOptions.Tags.HookRegistrations) {
      services.TryAddScoped(registration.HookType);
    }

    // Register MessageTagProcessor as Singleton (Dispatcher is Singleton and needs it)
    // Use IServiceScopeFactory to resolve scoped hooks at invocation time
    // A new scope is created for each ProcessTagsAsync call, allowing hooks to be Scoped
    services.AddSingleton<IMessageTagProcessor>(sp => {
      var tagOptions = sp.GetRequiredService<TagOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new MessageTagProcessor(tagOptions, scopeFactory);
    });

    // Register core infrastructure services
    services.AddSingleton<ITimeProvider, SystemTimeProvider>();
    services.AddSingleton<Observability.ITraceStore, Observability.InMemoryTraceStore>();
    services.AddSingleton<Policies.IPolicyEngine, Policies.PolicyEngine>();
    services.AddSingleton<Messaging.ILifecycleReceptorRegistry, Messaging.DefaultLifecycleReceptorRegistry>();
    services.AddSingleton<Messaging.ILifecycleInvoker, Messaging.RuntimeLifecycleInvoker>();
    services.AddSingleton<Messaging.ILifecycleMessageDeserializer>(sp => {
      var jsonOptions = sp.GetService<System.Text.Json.JsonSerializerOptions>();
      return new Messaging.JsonLifecycleMessageDeserializer(jsonOptions);
    });
    services.AddSingleton<Messaging.IEnvelopeSerializer>(sp => {
      var jsonOptions = sp.GetService<System.Text.Json.JsonSerializerOptions>();
      return new Messaging.EnvelopeSerializer(jsonOptions);
    });

    // Register IServiceInstanceProvider - use TryAdd to allow overrides
    services.TryAddSingleton<IServiceInstanceProvider>(sp => {
      var configuration = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
      return new ServiceInstanceProvider(configuration);
    });

    // NOTE: IStreamIdExtractor is registered by the generated AddWhizbangStreamIdExtractor()
    // which is called by AddWhizbangDispatcher(). The generated code registers a composite
    // extractor from StreamIdExtractorRegistry that includes extractors from ALL assemblies
    // (registered via [ModuleInitializer] pattern before Main() runs).
    // DO NOT register a fallback here - it would win over the composite due to TryAddSingleton.

    // Register message security services by default
    // Enables security context propagation from message envelopes to receptors
    services.AddWhizbangMessageSecurity();

    // Register perspective synchronization services
    // Enables read-your-writes consistency for perspectives
    services.TryAddSingleton<IDebuggerAwareClock, DebuggerAwareClock>();
    services.TryAddSingleton<ITracer, Tracer>(); // Handler tracing with OpenTelemetry integration
    services.TryAddSingleton<IPerspectiveSyncSignaler, LocalSyncSignaler>(); // Singleton for cross-scope signaling
    // Register scoped event tracker with factory that sets AsyncLocal for singleton Dispatcher access
    services.TryAddScoped<IScopedEventTracker>(sp => {
      var tracker = new ScopedEventTracker();
      // Set AsyncLocal so singleton Dispatcher can access the scoped tracker
      ScopedEventTrackerAccessor.CurrentTracker = tracker;
      return tracker;
    });
    services.TryAddScoped<IPerspectiveSyncAwaiter, PerspectiveSyncAwaiter>();

    // Register singleton event tracker for cross-scope perspective sync
    // CRITICAL: This enables Route.Local() events to be tracked for sync BEFORE they hit the database
    // Events tracked in Request 1 can be awaited in Request 2 via [AwaitPerspectiveSync]
    services.TryAddSingleton<ISyncEventTracker, SyncEventTracker>();

    // Register tracked event type registry in DYNAMIC mode
    // Uses parameterless constructor which reads from SyncEventTypeRegistrations on each call
    // This supports module initializers that register mappings AFTER the registry is constructed
    // (module initializers run when the assembly is first used, which may be after AddWhizbang())
    services.TryAddSingleton<ITrackedEventTypeRegistry, TrackedEventTypeRegistry>();

    // FUTURE: Register generated services once available in consuming projects
    // services.AddWhizbangDispatcher();  // Generated by ReceptorDiscoveryGenerator
    // services.AddReceptors();  // Generated by ReceptorDiscoveryGenerator
    // services.AddWhizbangStreamIdExtractor();  // Generated by StreamIdGenerator
    // services.AddWhizbangPerspectiveInvoker();  // Generated by PerspectiveDiscoveryGenerator

    return new WhizbangBuilder(services);
  }

  /// <summary>
  /// Decorates an existing <see cref="IEventStore"/> registration with sync tracking.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method uses the decorator pattern to wrap an existing IEventStore
  /// with the <see cref="Messaging.SyncTrackingEventStoreDecorator"/>. The decorator tracks
  /// emitted events for perspective synchronization.
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

      // Register the decorator
      services.AddScoped<Messaging.IEventStore>(sp => {
        var holder = sp.GetRequiredService<InnerEventStoreHolder>();
        var scopedTracker = sp.GetService<IScopedEventTracker>();
        var envelopeRegistry = sp.GetService<Observability.IEnvelopeRegistry>();
        var syncEventTracker = sp.GetService<ISyncEventTracker>();
        var typeRegistry = sp.GetService<ITrackedEventTypeRegistry>();
        return new Messaging.SyncTrackingEventStoreDecorator(
            (Messaging.IEventStore)holder.Instance,
            scopedTracker,
            envelopeRegistry,
            syncEventTracker,
            typeRegistry);
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

      // Register the decorator
      services.AddSingleton<Messaging.IEventStore>(sp => {
        var holder = sp.GetRequiredService<InnerEventStoreHolder>();
        var syncEventTracker = sp.GetService<ISyncEventTracker>();
        var typeRegistry = sp.GetService<ITrackedEventTypeRegistry>();
        return new Messaging.SyncTrackingEventStoreDecorator(
            (Messaging.IEventStore)holder.Instance,
            tracker: null, // Scoped tracker not available in singleton
            envelopeRegistry: null,
            syncEventTracker,
            typeRegistry);
      });
    }

    return services;
  }

  /// <summary>
  /// Holder for the inner event store instance to enable decoration.
  /// </summary>
  private sealed class InnerEventStoreHolder {
    public object Instance { get; }

    public InnerEventStoreHolder(object instance) {
      Instance = instance;
    }
  }
}
