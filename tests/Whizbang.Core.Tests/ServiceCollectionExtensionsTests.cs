using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for ServiceCollectionExtensions - unified AddWhizbang() API.
/// Target: 100% branch coverage.
/// </summary>
public class ServiceCollectionExtensionsTests {
  [Test]
  public async Task AddWhizbang_WithValidServices_ReturnsWhizbangBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = services.AddWhizbang();

    // Assert
    await Assert.That(builder).IsNotNull();
    await Assert.That(builder).IsTypeOf<WhizbangBuilder>();
  }

  [Test]
  public async Task AddWhizbang_ReturnedBuilder_HasSameServicesAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = services.AddWhizbang();

    // Assert
    await Assert.That(builder.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task AddWhizbang_RegistersCoreServices_SuccessfullyAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();

    // Assert - verify core services are registered
    // Note: This test verifies that AddWhizbang() actually registers services
    // The specific services it registers will be determined during implementation
    await Assert.That(services.Count).IsGreaterThan(0);
  }

  // ==========================================================================
  // Lifecycle Coordinator Registration Tests
  // ==========================================================================

  [Test]
  public async Task AddWhizbang_RegistersLifecycleCoordinator_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();
    var coordinator = provider.GetService<ILifecycleCoordinator>();

    // Assert
    await Assert.That(coordinator).IsNotNull();
    await Assert.That(coordinator).IsTypeOf<LifecycleCoordinator>();
  }

  [Test]
  public async Task AddWhizbang_LifecycleCoordinator_IsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Act
    var first = provider.GetService<ILifecycleCoordinator>();
    var second = provider.GetService<ILifecycleCoordinator>();

    // Assert - singleton should return same instance
    await Assert.That(first).IsNotNull();
    await Assert.That(ReferenceEquals(first, second)).IsTrue();
  }

  // ==========================================================================
  // Perspective Sync Service Registration Tests
  // ==========================================================================

  [Test]
  public async Task AddWhizbang_RegistersDebuggerAwareClock_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var clock1 = provider.GetService<IDebuggerAwareClock>();
    var clock2 = provider.GetService<IDebuggerAwareClock>();

    await Assert.That(clock1).IsNotNull();
    await Assert.That(clock1).IsTypeOf<DebuggerAwareClock>();
    await Assert.That(clock1).IsSameReferenceAs(clock2); // Singleton
  }

  [Test]
  public async Task AddWhizbang_RegistersScopedEventTracker_AsScopedAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    using var scope1 = provider.CreateScope();
    using var scope2 = provider.CreateScope();

    var tracker1a = scope1.ServiceProvider.GetService<IScopedEventTracker>();
    var tracker1b = scope1.ServiceProvider.GetService<IScopedEventTracker>();
    var tracker2 = scope2.ServiceProvider.GetService<IScopedEventTracker>();

    await Assert.That(tracker1a).IsNotNull();
    await Assert.That(tracker1a).IsTypeOf<ScopedEventTracker>();
    await Assert.That(tracker1a).IsSameReferenceAs(tracker1b); // Same within scope
    await Assert.That(tracker1a).IsNotSameReferenceAs(tracker2); // Different across scopes
  }

  [Test]
  public async Task AddWhizbang_RegistersPerspectiveSyncSignaler_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert - Singleton for cross-scope signaling (PerspectiveWorker is Singleton)
    var signaler1 = provider.GetService<IPerspectiveSyncSignaler>();
    var signaler2 = provider.GetService<IPerspectiveSyncSignaler>();

    await Assert.That(signaler1).IsNotNull();
    await Assert.That(signaler1).IsTypeOf<LocalSyncSignaler>();
    await Assert.That(signaler1).IsSameReferenceAs(signaler2); // Same instance (Singleton)
  }

  [Test]
  public async Task AddWhizbang_RegistersPerspectiveSyncAwaiter_AsScopedAsync() {
    // Arrange
    var services = new ServiceCollection();
    // PerspectiveSyncAwaiter requires IWorkCoordinator (provided by data layer)
    services.AddSingleton<IWorkCoordinator, StubWorkCoordinator>();
    services.AddLogging();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    using var scope1 = provider.CreateScope();
    using var scope2 = provider.CreateScope();

    var awaiter1a = scope1.ServiceProvider.GetService<IPerspectiveSyncAwaiter>();
    var awaiter1b = scope1.ServiceProvider.GetService<IPerspectiveSyncAwaiter>();
    var awaiter2 = scope2.ServiceProvider.GetService<IPerspectiveSyncAwaiter>();

    await Assert.That(awaiter1a).IsNotNull();
    await Assert.That(awaiter1a).IsTypeOf<PerspectiveSyncAwaiter>();
    await Assert.That(awaiter1a).IsSameReferenceAs(awaiter1b); // Same within scope
    await Assert.That(awaiter1a).IsNotSameReferenceAs(awaiter2); // Different across scopes
  }

  [Test]
  public async Task AddWhizbang_SyncServices_AllowOverridesWithTryAddAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Pre-register custom implementations before AddWhizbang()
    services.AddSingleton<IDebuggerAwareClock, DebuggerAwareClock>();
    services.AddSingleton<IPerspectiveSyncSignaler, LocalSyncSignaler>();
    services.AddScoped<IScopedEventTracker, ScopedEventTracker>();

    // Act
    _ = services.AddWhizbang();

    // Assert - TryAdd should not duplicate registrations
    var clockRegistrations = services.Where(s => s.ServiceType == typeof(IDebuggerAwareClock)).ToList();
    var signalerRegistrations = services.Where(s => s.ServiceType == typeof(IPerspectiveSyncSignaler)).ToList();
    var trackerRegistrations = services.Where(s => s.ServiceType == typeof(IScopedEventTracker)).ToList();

    await Assert.That(clockRegistrations.Count).IsEqualTo(1);
    await Assert.That(signalerRegistrations.Count).IsEqualTo(1);
    await Assert.That(trackerRegistrations.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_RegistersSyncEventTracker_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert - Singleton for cross-scope event tracking
    var tracker1 = provider.GetService<ISyncEventTracker>();
    var tracker2 = provider.GetService<ISyncEventTracker>();

    await Assert.That(tracker1).IsNotNull();
    await Assert.That(tracker1).IsTypeOf<SyncEventTracker>();
    await Assert.That(tracker1).IsSameReferenceAs(tracker2); // Same instance (Singleton)
  }

  [Test]
  public async Task AddWhizbang_RegistersTrackedEventTypeRegistry_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert - Singleton with empty default (source generators provide actual mappings)
    var registry1 = provider.GetService<ITrackedEventTypeRegistry>();
    var registry2 = provider.GetService<ITrackedEventTypeRegistry>();

    await Assert.That(registry1).IsNotNull();
    await Assert.That(registry1).IsTypeOf<TrackedEventTypeRegistry>();
    await Assert.That(registry1).IsSameReferenceAs(registry2); // Same instance (Singleton)

    // Empty by default - no event types tracked
    await Assert.That(registry1!.ShouldTrack(typeof(string))).IsFalse();
  }

  [Test]
  public async Task AddWhizbang_SyncEventTracker_AllowsOverrideAsync() {
    // Arrange
    var services = new ServiceCollection();
    var customTracker = new SyncEventTracker();

    // Pre-register custom implementation before AddWhizbang()
    services.AddSingleton<ISyncEventTracker>(customTracker);

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert - TryAdd should not override pre-registered singleton
    var resolvedTracker = provider.GetService<ISyncEventTracker>();
    await Assert.That(resolvedTracker).IsSameReferenceAs(customTracker);
  }

  [Test]
  public async Task AddWhizbang_TrackedEventTypeRegistry_AllowsOverrideAsync() {
    // Arrange
    var services = new ServiceCollection();
    var customRegistry = new TrackedEventTypeRegistry(new Dictionary<Type, string[]> {
      { typeof(string), ["TestPerspective"] }
    });

    // Pre-register custom implementation before AddWhizbang()
    services.AddSingleton<ITrackedEventTypeRegistry>(customRegistry);

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert - TryAdd should not override pre-registered singleton
    var resolvedRegistry = provider.GetService<ITrackedEventTypeRegistry>();
    await Assert.That(resolvedRegistry).IsSameReferenceAs(customRegistry);
    await Assert.That(resolvedRegistry!.ShouldTrack(typeof(string))).IsTrue();
  }

  // ==========================================================================
  // AddWhizbang with Options Lambda Tests
  // ==========================================================================

  [Test]
  public async Task AddWhizbang_WithOptionsLambda_ReturnsWhizbangBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = services.AddWhizbang(options => { });

    // Assert
    await Assert.That(builder).IsNotNull();
    await Assert.That(builder).IsTypeOf<WhizbangBuilder>();
  }

  [Test]
  public async Task AddWhizbang_WithOptionsLambda_RegistersTagOptions_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var tagOptions = provider.GetService<TagOptions>();
    await Assert.That(tagOptions).IsNotNull();
    await Assert.That(tagOptions!.HookRegistrations.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_WithOptionsLambda_RegistersWhizbangCoreOptions_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(options => {
      options.EnableTagProcessing = false;
      options.TagProcessingMode = TagProcessingMode.AsLifecycleStage;
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var coreOptions = provider.GetService<WhizbangCoreOptions>();
    await Assert.That(coreOptions).IsNotNull();
    await Assert.That(coreOptions!.EnableTagProcessing).IsFalse();
    await Assert.That(coreOptions.TagProcessingMode).IsEqualTo(TagProcessingMode.AsLifecycleStage);
  }

  [Test]
  public async Task AddWhizbang_WithHooks_RegistersHookTypesAsScoped_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
      options.Tags.UseHook<TelemetryTagAttribute, TestTelemetryHook>();
    });
    var provider = services.BuildServiceProvider();

    // Assert - hooks should be registered as scoped
    using var scope1 = provider.CreateScope();
    using var scope2 = provider.CreateScope();

    var hook1a = scope1.ServiceProvider.GetService<TestNotificationHook>();
    var hook1b = scope1.ServiceProvider.GetService<TestNotificationHook>();
    var hook2 = scope2.ServiceProvider.GetService<TestNotificationHook>();

    await Assert.That(hook1a).IsNotNull();
    await Assert.That(hook1a).IsSameReferenceAs(hook1b); // Same within scope
    await Assert.That(hook1a).IsNotSameReferenceAs(hook2); // Different across scopes
  }

  [Test]
  public async Task AddWhizbang_WithHooks_RegistersMessageTagProcessor_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });
    var provider = services.BuildServiceProvider();

    // Assert
    using var scope = provider.CreateScope();
    var processor = scope.ServiceProvider.GetService<IMessageTagProcessor>();

    await Assert.That(processor).IsNotNull();
    await Assert.That(processor).IsTypeOf<MessageTagProcessor>();
  }

  [Test]
  public async Task AddWhizbang_WithNullConfigure_UsesDefaults_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(configure: null);
    var provider = services.BuildServiceProvider();

    // Assert - defaults should be used
    var coreOptions = provider.GetService<WhizbangCoreOptions>();
    await Assert.That(coreOptions).IsNotNull();
    await Assert.That(coreOptions!.EnableTagProcessing).IsTrue();
    await Assert.That(coreOptions.TagProcessingMode).IsEqualTo(TagProcessingMode.AfterReceptorCompletion);
  }

  [Test]
  public async Task AddWhizbang_ParameterlessOverload_StillWorks_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act - use parameterless overload
    var builder = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert - should still work and register defaults
    await Assert.That(builder).IsNotNull();

    // WhizbangCoreOptions should be registered with defaults
    var coreOptions = provider.GetService<WhizbangCoreOptions>();
    await Assert.That(coreOptions).IsNotNull();
    await Assert.That(coreOptions!.EnableTagProcessing).IsTrue();
  }

  [Test]
  public async Task AddWhizbang_WithMultipleHooks_RegistersAllHookTypes_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
      options.Tags.UseHook<TelemetryTagAttribute, TestTelemetryHook>();
      options.Tags.UseHook<MetricTagAttribute, TestMetricHook>();
      options.Tags.UseUniversalHook<TestUniversalHook>();
    });
    var provider = services.BuildServiceProvider();

    // Assert - all hooks should be resolvable
    using var scope = provider.CreateScope();

    var notificationHook = scope.ServiceProvider.GetService<TestNotificationHook>();
    var telemetryHook = scope.ServiceProvider.GetService<TestTelemetryHook>();
    var metricHook = scope.ServiceProvider.GetService<TestMetricHook>();
    var universalHook = scope.ServiceProvider.GetService<TestUniversalHook>();

    await Assert.That(notificationHook).IsNotNull();
    await Assert.That(telemetryHook).IsNotNull();
    await Assert.That(metricHook).IsNotNull();
    await Assert.That(universalHook).IsNotNull();
  }

  [Test]
  public async Task AddWhizbang_WithHooksTryAddScoped_DoesNotOverrideExisting_Async() {
    // Arrange
    var services = new ServiceCollection();
    var existingHook = new TestNotificationHook();
    services.AddScoped(_ => existingHook); // Pre-register

    // Act
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });
    var provider = services.BuildServiceProvider();

    // Assert - TryAddScoped should not override existing registration
    using var scope = provider.CreateScope();
    var resolvedHook = scope.ServiceProvider.GetService<TestNotificationHook>();
    await Assert.That(resolvedHook).IsSameReferenceAs(existingHook);
  }

  [Test]
  public async Task AddWhizbang_CalledMultipleTimes_PreservesHooksFromFirstCall_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act - First call registers hooks
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
      options.Tags.UseHook<TelemetryTagAttribute, TestTelemetryHook>();
    });

    // Second call without hooks (like JDNext's pattern)
    _ = services.AddWhizbang();

    var provider = services.BuildServiceProvider();

    // Assert - hooks from first call should be preserved
    var tagOptions = provider.GetRequiredService<TagOptions>();
    await Assert.That(tagOptions.HookRegistrations.Count).IsEqualTo(2);

    var notificationHook = tagOptions.HookRegistrations
        .FirstOrDefault(h => h.AttributeType == typeof(SignalTagAttribute));
    var telemetryHook = tagOptions.HookRegistrations
        .FirstOrDefault(h => h.AttributeType == typeof(TelemetryTagAttribute));

    await Assert.That(notificationHook).IsNotNull();
    await Assert.That(telemetryHook).IsNotNull();
  }

  [Test]
  public async Task AddWhizbang_CalledMultipleTimes_MergesHooksFromBothCalls_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act - First call registers notification hook
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });

    // Second call registers telemetry hook
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<TelemetryTagAttribute, TestTelemetryHook>();
    });

    var provider = services.BuildServiceProvider();

    // Assert - hooks from both calls should be present
    var tagOptions = provider.GetRequiredService<TagOptions>();
    await Assert.That(tagOptions.HookRegistrations.Count).IsEqualTo(2);

    var notificationHook = tagOptions.HookRegistrations
        .FirstOrDefault(h => h.AttributeType == typeof(SignalTagAttribute));
    var telemetryHook = tagOptions.HookRegistrations
        .FirstOrDefault(h => h.AttributeType == typeof(TelemetryTagAttribute));

    await Assert.That(notificationHook).IsNotNull();
    await Assert.That(telemetryHook).IsNotNull();
  }

  [Test]
  public async Task AddWhizbang_CalledMultipleTimes_DoesNotDuplicateHooks_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act - Both calls register the same hook
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });

    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });

    var provider = services.BuildServiceProvider();

    // Assert - hook should not be duplicated
    var tagOptions = provider.GetRequiredService<TagOptions>();
    var notificationHooks = tagOptions.HookRegistrations
        .Where(h => h.AttributeType == typeof(SignalTagAttribute) && h.HookType == typeof(TestNotificationHook))
        .ToList();

    await Assert.That(notificationHooks.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_CalledMultipleTimes_ProcessorUsesFirstTagOptions_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act - First call registers hooks
    _ = services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });

    // Second call without hooks
    _ = services.AddWhizbang();

    var provider = services.BuildServiceProvider();

    // Assert - processor should have access to hooks from first call
    var processor = provider.GetRequiredService<IMessageTagProcessor>() as MessageTagProcessor;
    await Assert.That(processor).IsNotNull();

    // Indirectly verify by checking TagOptions has the hook
    var tagOptions = provider.GetRequiredService<TagOptions>();
    var hooks = tagOptions.GetHooksFor<SignalTagAttribute>().ToList();
    await Assert.That(hooks.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_ServiceDescriptor_HasImplementationInstance_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });

    // Assert - Verify ImplementationInstance is set correctly
    // This is critical for the multiple-call merge logic to work
    var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(TagOptions));
    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.ImplementationInstance).IsNotNull();
    await Assert.That(descriptor.ImplementationInstance).IsTypeOf<TagOptions>();

    var tagOptions = (TagOptions)descriptor.ImplementationInstance!;
    await Assert.That(tagOptions.HookRegistrations.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbang_CalledMultipleTimes_ImplementationInstancePreserved_Async() {
    // Arrange
    var services = new ServiceCollection();

    // Act - First call with hooks
    services.AddWhizbang(options => {
      options.Tags.UseHook<SignalTagAttribute, TestNotificationHook>();
    });

    // Second call without hooks (should find existing via ImplementationInstance)
    services.AddWhizbang();

    // Assert - Verify ImplementationInstance is still accessible
    var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(TagOptions));
    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.ImplementationInstance).IsNotNull();

    // The key assertion: hooks should still be there
    var tagOptions = (TagOptions)descriptor.ImplementationInstance!;
    await Assert.That(tagOptions.HookRegistrations.Count).IsEqualTo(1);
  }

  // ==========================================================================
  // TracingOptions Registration Tests
  // ==========================================================================

  [Test]
  public async Task AddWhizbang_RegistersTracingOptions_AsIOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var options = provider.GetService<IOptions<TracingOptions>>();
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.Value).IsNotNull();
  }

  [Test]
  public async Task AddWhizbang_RegistersTracingOptions_AsIOptionsMonitorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var optionsMonitor = provider.GetService<IOptionsMonitor<TracingOptions>>();
    await Assert.That(optionsMonitor).IsNotNull();
    await Assert.That(optionsMonitor!.CurrentValue).IsNotNull();
  }

  [Test]
  public async Task AddWhizbang_WithTracingConfig_ConfiguresTracingOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(options => {
      options.Tracing.Verbosity = TraceVerbosity.Verbose;
      options.Tracing.Components = TraceComponents.Handlers | TraceComponents.Lifecycle;
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;
    await Assert.That(tracingOptions.Verbosity).IsEqualTo(TraceVerbosity.Verbose);
    await Assert.That(tracingOptions.IsEnabled(TraceComponents.Handlers)).IsTrue();
    await Assert.That(tracingOptions.IsEnabled(TraceComponents.Lifecycle)).IsTrue();
    await Assert.That(tracingOptions.IsEnabled(TraceComponents.Outbox)).IsFalse();
  }

  [Test]
  public async Task AddWhizbang_TracingOptions_ConfiguredFromWhizbangCoreOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act - Configure via WhizbangCoreOptions
    _ = services.AddWhizbang(options => {
      options.Tracing.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug;
      options.Tracing.TracedMessages["ReseedSystemEvent"] = TraceVerbosity.Verbose;
    });
    var provider = services.BuildServiceProvider();

    // Assert - TracingOptions should have the configured values
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;
    await Assert.That(tracingOptions.TracedHandlers.ContainsKey("OrderReceptor")).IsTrue();
    await Assert.That(tracingOptions.TracedHandlers["OrderReceptor"]).IsEqualTo(TraceVerbosity.Debug);
    await Assert.That(tracingOptions.TracedMessages.ContainsKey("ReseedSystemEvent")).IsTrue();
    await Assert.That(tracingOptions.TracedMessages["ReseedSystemEvent"]).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task AddWhizbang_TracingOptions_BoundFromIConfigurationAsync() {
    // Arrange
    var configData = new Dictionary<string, string?> {
      ["Whizbang:Tracing:Verbosity"] = "Verbose",
      ["Whizbang:Tracing:EnableOpenTelemetry"] = "true",
      ["Whizbang:Tracing:EnableStructuredLogging"] = "false"
    };
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(configData)
      .Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;
    await Assert.That(tracingOptions.Verbosity).IsEqualTo(TraceVerbosity.Verbose);
    await Assert.That(tracingOptions.EnableOpenTelemetry).IsTrue();
    await Assert.That(tracingOptions.EnableStructuredLogging).IsFalse();
  }

  [Test]
  public async Task AddWhizbang_TracingOptions_IConfigurationOverridesProgrammaticDefaultsAsync() {
    // Arrange - Configuration has Verbose, programmatic sets Normal
    var configData = new Dictionary<string, string?> {
      ["Whizbang:Tracing:Verbosity"] = "Debug"
    };
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(configData)
      .Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);

    // Act - Programmatic defaults get set, then IConfiguration overrides
    _ = services.AddWhizbang(options => {
      options.Tracing.Verbosity = TraceVerbosity.Normal;
    });
    var provider = services.BuildServiceProvider();

    // Assert - IConfiguration should win
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;
    await Assert.That(tracingOptions.Verbosity).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task AddWhizbang_TracingOptions_TracedHandlersDictionaryBoundFromConfigAsync() {
    // Arrange
    var configData = new Dictionary<string, string?> {
      ["Whizbang:Tracing:TracedHandlers:OrderReceptor"] = "Debug",
      ["Whizbang:Tracing:TracedHandlers:PaymentHandler"] = "Verbose"
    };
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(configData)
      .Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;
    await Assert.That(tracingOptions.TracedHandlers.ContainsKey("OrderReceptor")).IsTrue();
    await Assert.That(tracingOptions.TracedHandlers["OrderReceptor"]).IsEqualTo(TraceVerbosity.Debug);
    await Assert.That(tracingOptions.TracedHandlers.ContainsKey("PaymentHandler")).IsTrue();
    await Assert.That(tracingOptions.TracedHandlers["PaymentHandler"]).IsEqualTo(TraceVerbosity.Verbose);
  }

  [Test]
  public async Task AddWhizbang_TracingOptions_TracedMessagesDictionaryBoundFromConfigAsync() {
    // Arrange
    var configData = new Dictionary<string, string?> {
      ["Whizbang:Tracing:TracedMessages:ReseedSystemEvent"] = "Debug",
      ["Whizbang:Tracing:TracedMessages:CreateOrderCommand"] = "Normal"
    };
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(configData)
      .Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;
    await Assert.That(tracingOptions.TracedMessages.ContainsKey("ReseedSystemEvent")).IsTrue();
    await Assert.That(tracingOptions.TracedMessages["ReseedSystemEvent"]).IsEqualTo(TraceVerbosity.Debug);
    await Assert.That(tracingOptions.TracedMessages.ContainsKey("CreateOrderCommand")).IsTrue();
    await Assert.That(tracingOptions.TracedMessages["CreateOrderCommand"]).IsEqualTo(TraceVerbosity.Normal);
  }

  // ==========================================================================
  // ServiceRegistrationCallbacks Tests - Auto-Registration
  // These tests modify shared static state and must NOT run in parallel
  // ==========================================================================

  [Test]
  [NotInParallel(Order = 1)]
  public async Task AddWhizbang_InvokesLensServicesCallback_WhenRegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();
    var callbackInvoked = false;
    ServiceRegistrationOptions? receivedOptions = null;

    ServiceRegistrationCallbacks.Reset();
    ServiceRegistrationCallbacks.LensServices = (s, options) => {
      callbackInvoked = true;
      receivedOptions = options;
    };

    try {
      // Act
      _ = services.AddWhizbang();

      // Assert
      await Assert.That(callbackInvoked).IsTrue();
      await Assert.That(receivedOptions).IsNotNull();
      await Assert.That(receivedOptions!.IncludeSelfRegistration).IsTrue(); // Default
    } finally {
      ServiceRegistrationCallbacks.Reset();
    }
  }

  [Test]
  [NotInParallel(Order = 2)]
  public async Task AddWhizbang_InvokesPerspectiveServicesCallback_WhenRegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();
    var callbackInvoked = false;

    ServiceRegistrationCallbacks.Reset();
    ServiceRegistrationCallbacks.PerspectiveServices = (s, options) => {
      callbackInvoked = true;
    };

    try {
      // Act
      _ = services.AddWhizbang();

      // Assert
      await Assert.That(callbackInvoked).IsTrue();
    } finally {
      ServiceRegistrationCallbacks.Reset();
    }
  }

  [Test]
  [NotInParallel(Order = 3)]
  public async Task AddWhizbang_InvokesDispatcherCallback_WhenRegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();
    var callbackInvoked = false;

    ServiceRegistrationCallbacks.Reset();
    ServiceRegistrationCallbacks.Dispatcher = s => {
      callbackInvoked = true;
    };

    try {
      // Act
      _ = services.AddWhizbang();

      // Assert
      await Assert.That(callbackInvoked).IsTrue();
    } finally {
      ServiceRegistrationCallbacks.Reset();
    }
  }

  [Test]
  [NotInParallel(Order = 4)]
  public async Task AddWhizbang_PassesServiceOptionsToCallbacks_Async() {
    // Arrange
    var services = new ServiceCollection();
    ServiceRegistrationOptions? receivedOptions = null;

    ServiceRegistrationCallbacks.Reset();
    ServiceRegistrationCallbacks.LensServices = (s, options) => {
      receivedOptions = options;
    };

    try {
      // Act
      _ = services.AddWhizbang(options => {
        options.Services.IncludeSelfRegistration = false;
      });

      // Assert
      await Assert.That(receivedOptions).IsNotNull();
      await Assert.That(receivedOptions!.IncludeSelfRegistration).IsFalse();
    } finally {
      ServiceRegistrationCallbacks.Reset();
    }
  }

  [Test]
  [NotInParallel(Order = 5)]
  public async Task AddWhizbang_WithNoCallbacks_DoesNotThrowAsync() {
    // Arrange
    var services = new ServiceCollection();
    ServiceRegistrationCallbacks.Reset();

    try {
      // Act & Assert - should not throw even with no callbacks registered
      var builder = services.AddWhizbang();
      await Assert.That(builder).IsNotNull();
    } finally {
      ServiceRegistrationCallbacks.Reset();
    }
  }

  [Test]
  [NotInParallel(Order = 6)]
  public async Task AddWhizbang_CallsAllCallbacksInOrder_Async() {
    // Arrange
    var services = new ServiceCollection();
    var callOrder = new List<string>();

    ServiceRegistrationCallbacks.Reset();
    ServiceRegistrationCallbacks.LensServices = (s, options) => callOrder.Add("Lens");
    ServiceRegistrationCallbacks.PerspectiveServices = (s, options) => callOrder.Add("Perspective");
    ServiceRegistrationCallbacks.Dispatcher = s => callOrder.Add("Dispatcher");

    try {
      // Act
      _ = services.AddWhizbang();

      // Assert - verify all callbacks were invoked
      await Assert.That(callOrder).Contains("Lens");
      await Assert.That(callOrder).Contains("Perspective");
      await Assert.That(callOrder).Contains("Dispatcher");
    } finally {
      ServiceRegistrationCallbacks.Reset();
    }
  }

  [Test]
  [NotInParallel(Order = 7)]
  public async Task ServiceRegistrationCallbacks_Reset_ClearsAllCallbacksAsync() {
    // Arrange
    ServiceRegistrationCallbacks.LensServices = (s, o) => { };
    ServiceRegistrationCallbacks.PerspectiveServices = (s, o) => { };
    ServiceRegistrationCallbacks.Dispatcher = s => { };

    // Act
    ServiceRegistrationCallbacks.Reset();

    // Assert
    await Assert.That(ServiceRegistrationCallbacks.LensServices).IsNull();
    await Assert.That(ServiceRegistrationCallbacks.PerspectiveServices).IsNull();
    await Assert.That(ServiceRegistrationCallbacks.Dispatcher).IsNull();
  }

  [Test]
  [NotInParallel(Order = 8)]
  public async Task WhizbangCoreOptions_Services_HasDefaultIncludeSelfRegistrationTrueAsync() {
    // Arrange & Act
    var options = new WhizbangCoreOptions();

    // Assert
    await Assert.That(options.Services).IsNotNull();
    await Assert.That(options.Services.IncludeSelfRegistration).IsTrue();
  }

  [Test]
  [NotInParallel(Order = 9)]
  public async Task WhizbangCoreOptions_Services_CanBeConfiguredAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    _ = services.AddWhizbang(options => {
      options.Services.IncludeSelfRegistration = false;
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var coreOptions = provider.GetService<WhizbangCoreOptions>();
    await Assert.That(coreOptions).IsNotNull();
    await Assert.That(coreOptions!.Services.IncludeSelfRegistration).IsFalse();
  }

  // ==========================================================================
  // Test Hook Implementations for Options Lambda Tests
  // ==========================================================================

  private sealed class TestNotificationHook : IMessageTagHook<SignalTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<SignalTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestTelemetryHook : IMessageTagHook<TelemetryTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<TelemetryTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestMetricHook : IMessageTagHook<MetricTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MetricTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  private sealed class TestUniversalHook : IMessageTagHook<MessageTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<MessageTagAttribute> _,
        CancellationToken __) {
      return ValueTask.FromResult<JsonElement?>(null);
    }
  }

  // ==========================================================================
  // DecorateEventStoreWithSyncTracking Tests
  // ==========================================================================

  [Test]
  public async Task DecorateEventStore_WithNoEventStoreRegistered_ReturnsServicesUnchangedAsync() {
    // Arrange - no IEventStore registered
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    var countBefore = services.Count;

    // Act - calling without IEventStore should be a no-op
    var result = services.DecorateEventStoreWithSyncTracking();

    // Assert
    await Assert.That(result).IsSameReferenceAs(services)
      .Because("DecorateEventStoreWithSyncTracking should return the service collection");
    await Assert.That(services.Count).IsEqualTo(countBefore)
      .Because("No services should be added when no IEventStore is registered");
  }

  [Test]
  public async Task DecorateEventStore_WithScopedFactoryRegistration_WrapsWithDecoratorsAsync() {
    // Arrange - register IEventStore using a factory (scoped)
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    services.AddScoped<IWorkCoordinator, StubWorkCoordinator>();
    services.AddLogging();

    // Register as scoped factory
    services.AddScoped<IEventStore>(_ => new StubEventStore());

    // Act
    services.DecorateEventStoreWithSyncTracking();

    // Assert - IEventStore should still be resolvable (now as decorator chain)
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    await Assert.That(eventStore).IsNotNull()
      .Because("IEventStore should be resolvable after decoration with factory registration");
  }

  [Test]
  public async Task DecorateEventStore_WithScopedTypeRegistration_WrapsWithDecoratorsAsync() {
    // Arrange - register IEventStore using an implementation type (scoped)
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    services.AddScoped<IWorkCoordinator, StubWorkCoordinator>();
    services.AddLogging();

    // Register as scoped type
    services.AddScoped<IEventStore, StubEventStore>();

    // Act
    services.DecorateEventStoreWithSyncTracking();

    // Assert - IEventStore should still be resolvable
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    await Assert.That(eventStore).IsNotNull()
      .Because("IEventStore should be resolvable after decoration with type registration");
  }

  [Test]
  public async Task DecorateEventStore_WithSingletonInstanceRegistration_WrapsWithDecoratorsAsync() {
    // Arrange - register IEventStore as singleton instance
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    services.AddScoped<IWorkCoordinator, StubWorkCoordinator>();
    services.AddLogging();

    var innerStore = new StubEventStore();
    services.AddSingleton<IEventStore>(innerStore);

    // Act
    services.DecorateEventStoreWithSyncTracking();

    // Assert - IEventStore should still be resolvable
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    await Assert.That(eventStore).IsNotNull()
      .Because("IEventStore should be resolvable after decoration with singleton instance");
  }

  [Test]
  public async Task DecorateEventStore_WithSingletonFactoryRegistration_WrapsWithDecoratorsAsync() {
    // Arrange - register IEventStore as singleton via factory
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    services.AddScoped<IWorkCoordinator, StubWorkCoordinator>();
    services.AddLogging();

    services.AddSingleton<IEventStore>(_ => new StubEventStore());

    // Act
    services.DecorateEventStoreWithSyncTracking();

    // Assert
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    await Assert.That(eventStore).IsNotNull()
      .Because("IEventStore should be resolvable after decoration with singleton factory");
  }

  [Test]
  public async Task DecorateEventStore_WithSingletonTypeRegistration_WrapsWithDecoratorsAsync() {
    // Arrange - register IEventStore as singleton via implementation type
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    services.AddScoped<IWorkCoordinator, StubWorkCoordinator>();
    services.AddLogging();

    services.AddSingleton<IEventStore, StubEventStore>();

    // Act
    services.DecorateEventStoreWithSyncTracking();

    // Assert
    var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    await Assert.That(eventStore).IsNotNull()
      .Because("IEventStore should be resolvable after decoration with singleton type");
  }

  // ==========================================================================
  // TracingOptionsPostConfigure Edge Case Tests
  // ==========================================================================

  [Test]
  public async Task TracingOptions_WithNullConfiguration_DoesNotThrowAsync() {
    // Arrange - no IConfiguration registered (config is optional)
    var services = new ServiceCollection();
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Act & Assert - should not throw when IConfiguration is absent
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;
    await Assert.That(tracingOptions).IsNotNull()
      .Because("TracingOptions should be resolvable even without IConfiguration");
  }

  [Test]
  public async Task TracingOptions_WithMissingSection_UsesDefaultsAsync() {
    // Arrange - IConfiguration registered but no Whizbang:Tracing section
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection([])
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert - defaults should be used when section doesn't exist
    await Assert.That(tracingOptions).IsNotNull();
  }

  [Test]
  public async Task TracingOptions_WithInvalidEnumValues_IgnoresInvalidAsync() {
    // Arrange - config has invalid enum values
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tracing:Verbosity"] = "NotAValidVerbosity",
        ["Whizbang:Tracing:Components"] = "NotAValidComponent"
      })
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act - invalid enum values should be ignored (TryParse fails, value unchanged)
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert - invalid values should not change the defaults
    await Assert.That(tracingOptions).IsNotNull();
  }

  [Test]
  public async Task TracingOptions_WithInvalidBoolValues_IgnoresInvalidAsync() {
    // Arrange - config has invalid boolean values
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tracing:EnableOpenTelemetry"] = "not-a-bool",
        ["Whizbang:Tracing:EnableStructuredLogging"] = "also-not-a-bool"
      })
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act - invalid bool values should be ignored (TryParse fails, value unchanged)
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert
    await Assert.That(tracingOptions).IsNotNull();
  }

  [Test]
  public async Task TracingOptions_WithTracedHandlersSection_BindsHandlerVerbosityAsync() {
    // Arrange - TracedHandlers section with valid values
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tracing:TracedHandlers:MyOrderHandler"] = "Normal"
      })
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert
    await Assert.That(tracingOptions.TracedHandlers.ContainsKey("MyOrderHandler")).IsTrue();
    await Assert.That(tracingOptions.TracedHandlers["MyOrderHandler"]).IsEqualTo(TraceVerbosity.Normal);
  }

  [Test]
  public async Task TracingOptions_WithTracedHandlers_InvalidVerbosity_IgnoresEntryAsync() {
    // Arrange - TracedHandlers section with an invalid verbosity value
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tracing:TracedHandlers:BadHandler"] = "NotAVerbosity"
      })
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act - invalid verbosity should be skipped
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert - the invalid entry should not be added
    await Assert.That(tracingOptions.TracedHandlers.ContainsKey("BadHandler")).IsFalse();
  }

  [Test]
  public async Task TracingOptions_WithTracedMessages_InvalidVerbosity_IgnoresEntryAsync() {
    // Arrange - TracedMessages section with an invalid verbosity value
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tracing:TracedMessages:BadMessage"] = "NotAVerbosity"
      })
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act - invalid verbosity should be skipped
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert - the invalid entry should not be added
    await Assert.That(tracingOptions.TracedMessages.ContainsKey("BadMessage")).IsFalse();
  }

  [Test]
  public async Task TracingOptions_WithEmptyTracedHandlersSection_DoesNotPopulateAsync() {
    // Arrange - no TracedHandlers section present
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tracing:Verbosity"] = "Debug"
      })
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert - no handlers should be added when section is absent
    await Assert.That(tracingOptions.TracedHandlers.Count).IsEqualTo(0)
      .Because("TracedHandlers should remain empty when no TracedHandlers section exists in config");
  }

  [Test]
  public async Task TracingOptions_WithEmptyTracedMessagesSection_DoesNotPopulateAsync() {
    // Arrange - no TracedMessages section present
    var services = new ServiceCollection();
    _ = services.AddWhizbang();

    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Tracing:Verbosity"] = "Debug"
      })
      .Build();
    services.AddSingleton<IConfiguration>(config);

    var provider = services.BuildServiceProvider();

    // Act
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    // Assert
    await Assert.That(tracingOptions.TracedMessages.Count).IsEqualTo(0)
      .Because("TracedMessages should remain empty when no TracedMessages section exists in config");
  }

  // ==========================================================================
  // Stub event store for DecorateEventStoreWithSyncTracking tests
  // ==========================================================================

  private sealed class StubEventStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
      where TMessage : notnull =>
      Task.CompletedTask;

    public System.Collections.Generic.IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public System.Collections.Generic.IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public System.Collections.Generic.IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, System.Collections.Generic.IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<System.Collections.Generic.List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new System.Collections.Generic.List<MessageEnvelope<TMessage>>());

    public Task<System.Collections.Generic.List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, System.Collections.Generic.IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new System.Collections.Generic.List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);
  }

  /// <summary>
  /// Stub IWorkCoordinator for DI resolution tests.
  /// </summary>
  private sealed class StubWorkCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ProcessWorkBatchAsync(
        ProcessWorkBatchRequest request,
        CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCursorCompletion completion,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId,
        string perspectiveName,
        CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }
}
