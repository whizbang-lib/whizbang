using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
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

    // Register TracingOptions with IOptions pattern
    _configureTracingOptions(services, coreOptions);

    // Register IConfiguration binding as PostConfigure (IConfiguration is optional)
    services.AddSingleton<IPostConfigureOptions<TracingOptions>>(sp => {
      var config = sp.GetService<IConfiguration>();
      return new TracingOptionsPostConfigure(config);
    });

    // Register hooks with DI (scoped lifetime for access to DbContext, etc.)
    _registerTagHooks(services, coreOptions);

    // Register MessageTagProcessor as Singleton
    services.AddSingleton<IMessageTagProcessor>(sp => {
      var tagOptions = sp.GetRequiredService<TagOptions>();
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new MessageTagProcessor(tagOptions, scopeFactory);
    });

    // Register core infrastructure services
    _registerCoreServices(services);

    // Register perspective synchronization services
    _registerPerspectiveSyncServices(services);

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

    services.TryAddSingleton<IServiceInstanceProvider>(sp => {
      var configuration = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
      return new ServiceInstanceProvider(configuration);
    });

    services.AddWhizbangMessageSecurity();
  }

  /// <summary>
  /// Registers perspective synchronization services.
  /// </summary>
  private static void _registerPerspectiveSyncServices(IServiceCollection services) {
    services.TryAddSingleton<IDebuggerAwareClock, DebuggerAwareClock>();
    services.TryAddSingleton<ITracer, Tracer>();
    services.TryAddSingleton<IPerspectiveSyncSignaler, LocalSyncSignaler>();

    services.TryAddScoped<IScopedEventTracker>(sp => {
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
  private sealed class TracingOptionsPostConfigure : IPostConfigureOptions<TracingOptions> {
    private readonly IConfiguration? _config;

    public TracingOptionsPostConfigure(IConfiguration? config) => _config = config;

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
        return new Messaging.AppendAndWaitEventStoreDecorator(
            withSyncTracking,
            syncAwaiter);
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
        return new Messaging.AppendAndWaitEventStoreDecorator(
            withSyncTracking,
            syncAwaiter);
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
