using System.Text.Json;
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
using Whizbang.Core.Messaging;
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
      options.Tags.UseHook<NotificationTagAttribute, TestNotificationHook>();
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
      options.Tags.UseHook<NotificationTagAttribute, TestNotificationHook>();
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
      options.Tags.UseHook<NotificationTagAttribute, TestNotificationHook>();
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
      options.Tags.UseHook<NotificationTagAttribute, TestNotificationHook>();
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
      options.Tags.UseHook<NotificationTagAttribute, TestNotificationHook>();
    });
    var provider = services.BuildServiceProvider();

    // Assert - TryAddScoped should not override existing registration
    using var scope = provider.CreateScope();
    var resolvedHook = scope.ServiceProvider.GetService<TestNotificationHook>();
    await Assert.That(resolvedHook).IsSameReferenceAs(existingHook);
  }

  // ==========================================================================
  // Tracing Service Registration Tests
  // ==========================================================================

  [Test]
  public async Task AddWhizbang_RegistersITracer_AsSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var tracer1 = provider.GetService<ITracer>();
    var tracer2 = provider.GetService<ITracer>();

    await Assert.That(tracer1).IsNotNull();
    await Assert.That(tracer1).IsTypeOf<Tracer>();
    await Assert.That(tracer1).IsSameReferenceAs(tracer2); // Singleton
  }

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
    await Assert.That(options.Value.Verbosity).IsEqualTo(TraceVerbosity.Off); // Default
  }

  [Test]
  public async Task AddWhizbang_RegistersTraceOutputs_AsEnumerableAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert
    var outputs = provider.GetServices<ITraceOutput>().ToList();

    await Assert.That(outputs.Count).IsGreaterThanOrEqualTo(2);
    await Assert.That(outputs.Any(o => o is LoggerTraceOutput)).IsTrue();
    await Assert.That(outputs.Any(o => o is OpenTelemetryTraceOutput)).IsTrue();
  }

  [Test]
  public async Task AddWhizbang_WithTracingConfig_ConfiguresTracingOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act
    _ = services.AddWhizbang(options => {
      options.Tracing.Verbosity = TraceVerbosity.Verbose;
      options.Tracing.Components = TraceComponents.Handlers | TraceComponents.EventStore;
      options.Tracing.TracedHandlers["OrderReceptor"] = TraceVerbosity.Debug;
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    await Assert.That(tracingOptions.Verbosity).IsEqualTo(TraceVerbosity.Verbose);
    await Assert.That(tracingOptions.Components).IsEqualTo(TraceComponents.Handlers | TraceComponents.EventStore);
    await Assert.That(tracingOptions.TracedHandlers["OrderReceptor"]).IsEqualTo(TraceVerbosity.Debug);
  }

  [Test]
  public async Task AddWhizbang_ITracer_AllowsOverrideAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Pre-register custom tracer before AddWhizbang()
    var customTracer = new TestTracer();
    services.AddSingleton<ITracer>(customTracer);

    // Act
    _ = services.AddWhizbang();
    var provider = services.BuildServiceProvider();

    // Assert - TryAdd should not override pre-registered singleton
    var resolvedTracer = provider.GetService<ITracer>();
    await Assert.That(resolvedTracer).IsSameReferenceAs(customTracer);
  }

  [Test]
  public async Task AddWhizbang_TracingOptions_ConfiguredFromWhizbangCoreOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act
    _ = services.AddWhizbang(options => {
      options.Tracing.TracedMessages["ReseedSystemEvent"] = TraceVerbosity.Debug;
    });
    var provider = services.BuildServiceProvider();

    // Assert - TracingOptions from WhizbangCoreOptions should be accessible
    var coreOptions = provider.GetRequiredService<WhizbangCoreOptions>();
    var tracingOptions = provider.GetRequiredService<IOptions<TracingOptions>>().Value;

    await Assert.That(coreOptions.Tracing.TracedMessages["ReseedSystemEvent"]).IsEqualTo(TraceVerbosity.Debug);
    await Assert.That(tracingOptions.TracedMessages["ReseedSystemEvent"]).IsEqualTo(TraceVerbosity.Debug);
  }

  // ==========================================================================
  // Test Hook Implementations for Options Lambda Tests
  // ==========================================================================

  private sealed class TestNotificationHook : IMessageTagHook<NotificationTagAttribute> {
    public ValueTask<JsonElement?> OnTaggedMessageAsync(
        TagContext<NotificationTagAttribute> _,
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
        PerspectiveCheckpointCompletion completion,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCheckpointFailure failure,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
        Guid streamId,
        string perspectiveName,
        CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  /// <summary>
  /// Test ITracer for DI resolution tests.
  /// </summary>
  private sealed class TestTracer : ITracer {
    public bool ShouldTrace(TraceComponents component, string? handlerName = null, string? messageType = null) => false;

    public TraceVerbosity GetEffectiveVerbosity(string? handlerName, string? messageType, TraceVerbosity? attributeVerbosity) =>
        attributeVerbosity ?? TraceVerbosity.Off;

    public ITraceScope BeginTrace(TraceContext context) => new TestTraceScope();

    public void BeginHandlerTrace(string handlerName, string messageTypeName, int? attributeVerbosity, bool hasTraceAttribute) { }

    public void EndHandlerTrace(
        string handlerName,
        string messageTypeName,
        HandlerStatus status,
        double durationMs,
        long startTimestamp,
        long endTimestamp,
        Exception? exception) { }

    private sealed class TestTraceScope : ITraceScope {
      public void Complete() { }
      public void Fail(Exception exception) { }
      public void EarlyReturn() { }
      public void Dispose() { }
    }
  }
}
