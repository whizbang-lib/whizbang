using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

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
}
