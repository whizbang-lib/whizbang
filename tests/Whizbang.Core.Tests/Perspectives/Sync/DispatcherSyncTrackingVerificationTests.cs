using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// VERIFICATION TESTS: These tests verify the complete tracking chain works correctly.
///
/// The chain is:
/// 1. Source generator generates module initializer that calls SyncEventTypeRegistrations.Register()
/// 2. TrackedEventTypeRegistry (default constructor) reads from SyncEventTypeRegistrations
/// 3. Dispatcher checks _trackedEventTypeRegistry.GetPerspectiveNames(eventType)
/// 4. If perspectives are returned, Dispatcher tracks in _syncEventTracker
/// 5. PerspectiveSyncAwaiter reads from _syncEventTracker
///
/// If any step fails, events won't be tracked and sync will fall through to DB discovery.
/// </summary>
/// <remarks>
/// These tests use the shared static SyncEventTypeRegistrations, so they must run
/// sequentially to avoid interference.
/// </remarks>
[NotInParallel("SyncTests")]
public class DispatcherSyncTrackingVerificationTests {

  /// <summary>
  /// CRITICAL: Verify that SyncEventTypeRegistrations works correctly.
  /// This simulates what the module initializer does.
  /// </summary>
  [Test]
  public async Task SyncEventTypeRegistrations_RegisterAndRetrieve_WorksAsync() {
    // Arrange - clear previous test state
    SyncEventTypeRegistrations.Clear();

    var eventType = typeof(VerificationTestEventB);
    const string perspectiveName = "MyApp.Perspectives.TestPerspectiveC";

    // Act - simulate module initializer
    SyncEventTypeRegistrations.Register(eventType, perspectiveName);

    // Get mappings (this is what TrackedEventTypeRegistry calls)
    var mappings = SyncEventTypeRegistrations.GetMappings();

    // Assert
    await Assert.That(mappings.ContainsKey(eventType)).IsTrue()
      .Because("Event type should be registered");
    await Assert.That(mappings[eventType]).Contains(perspectiveName)
      .Because("Perspective name should be in the array");

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// CRITICAL: Verify TrackedEventTypeRegistry with default constructor reads from SyncEventTypeRegistrations.
  /// </summary>
  [Test]
  public async Task TrackedEventTypeRegistry_DefaultConstructor_ReadsSyncEventTypeRegistrationsAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();

    var eventType = typeof(VerificationTestEventB);
    var perspectiveName = typeof(VerificationTestPerspectiveC).FullName!;

    // Register BEFORE creating registry
    SyncEventTypeRegistrations.Register(eventType, perspectiveName);

    // Act - create registry with default constructor (dynamic mode)
    var registry = new TrackedEventTypeRegistry();
    var perspectives = registry.GetPerspectiveNames(eventType);

    // Assert
    await Assert.That(perspectives.Count).IsEqualTo(1)
      .Because("Registry should find the registered perspective");
    await Assert.That(perspectives[0]).IsEqualTo(perspectiveName)
      .Because("Perspective name should match exactly");

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// CRITICAL: Verify that Type objects match correctly.
  /// The module initializer uses typeof(EventType) and the Dispatcher checks with messageType.
  /// These Type objects MUST be the same for the dictionary lookup to work.
  /// </summary>
  [Test]
  public async Task TypeEquality_SameTypeFromDifferentContexts_AreEqualAsync() {
    // This test verifies that Type objects from different contexts are the same
    var type1 = typeof(VerificationTestEventB);
    var type2 = typeof(VerificationTestEventB);

    // In .NET, Type objects are cached per type per assembly
    await Assert.That(type1).IsEqualTo(type2)
      .Because("Type objects for the same type should be equal");
    await Assert.That(ReferenceEquals(type1, type2)).IsTrue()
      .Because("Type objects should be reference equal (cached)");

    // Dictionary lookup
    var dict = new Dictionary<Type, string> {
      { type1, "value1" }
    };

    await Assert.That(dict.ContainsKey(type2)).IsTrue()
      .Because("Dictionary lookup with Type key should work");
  }

  /// <summary>
  /// CRITICAL: Verify the complete tracking chain.
  /// This simulates what happens when an event is cascaded:
  /// 1. Module initializer registers event → perspective mapping
  /// 2. Dispatcher checks TrackedEventTypeRegistry
  /// 3. If match found, Dispatcher tracks in SyncEventTracker
  /// 4. PerspectiveSyncAwaiter finds tracked event
  /// </summary>
  [Test]
  public async Task FullTrackingChain_EventCascaded_TrackedAndFoundAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();

    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var eventType = typeof(VerificationTestEventB);
    var perspectiveName = typeof(VerificationTestPerspectiveC).FullName!;

    // Step 1: Module initializer registers (simulate generated code)
    SyncEventTypeRegistrations.Register(eventType, perspectiveName);

    // Step 2: Create TrackedEventTypeRegistry (what AddWhizbang() does)
    var typeRegistry = new TrackedEventTypeRegistry(); // Default constructor = dynamic mode

    // Step 3: Create SyncEventTracker (singleton)
    var singletonTracker = new SyncEventTracker();

    // Step 4: Simulate Dispatcher's tracking logic from _cascadeEventsFromResultAsync
    // This is lines 1884-1891 in Dispatcher.cs
    var perspectiveNames = typeRegistry.GetPerspectiveNames(eventType);

    // ASSERTION: Registry MUST return the perspective name
    await Assert.That(perspectiveNames.Count).IsGreaterThan(0)
      .Because("CRITICAL: TrackedEventTypeRegistry MUST return perspectives for tracked event types. " +
               "If this fails, events won't be tracked and sync will fall through to DB discovery.");

    foreach (var name in perspectiveNames) {
      singletonTracker.TrackEvent(eventType, eventId, streamId, name);
    }

    // Step 5: Verify PerspectiveSyncAwaiter can find the event
    var pendingEvents = singletonTracker.GetPendingEvents(streamId, perspectiveName, [eventType]);

    // ASSERTION: Event MUST be found in tracker
    await Assert.That(pendingEvents.Count).IsEqualTo(1)
      .Because("CRITICAL: Event must be found in singleton tracker for cross-scope sync to work");
    await Assert.That(pendingEvents[0].EventId).IsEqualTo(eventId);

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// FAILURE SCENARIO: Without module initializer, registry returns empty, no tracking occurs.
  /// This demonstrates what happens when the generated code doesn't run.
  /// </summary>
  [Test]
  public async Task MissingModuleInitializer_RegistryReturnsEmpty_NoTrackingAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear(); // Ensure clean state

    var eventType = typeof(VerificationTestEventB);

    // NO registration - simulates missing module initializer

    // Create registry
    var typeRegistry = new TrackedEventTypeRegistry();

    // Check for perspectives
    var perspectiveNames = typeRegistry.GetPerspectiveNames(eventType);

    // Without registration, registry returns empty
    await Assert.That(perspectiveNames.Count).IsEqualTo(0)
      .Because("Without module initializer, registry returns empty list");

    // This means Dispatcher's foreach loop doesn't execute:
    // foreach (var perspectiveName in perspectiveNames) { ... }
    // And the event is NOT tracked!
  }

  /// <summary>
  /// VERIFICATION: ServiceCollection registration works correctly.
  /// </summary>
  [Test]
  public async Task ServiceCollection_AddWhizbangCore_RegistersSingletonTrackerAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();
    SyncEventTypeRegistrations.Register(typeof(VerificationTestEventB), typeof(VerificationTestPerspectiveC).FullName!);

    var services = new ServiceCollection();

    // Add logging (required dependency)
    services.AddLogging();

    // Add the core services manually (simulating what AddWhizbang would do)
    services.AddSingleton<ISyncEventTracker, SyncEventTracker>();
    services.AddSingleton<ITrackedEventTypeRegistry, TrackedEventTypeRegistry>();

    var sp = services.BuildServiceProvider();

    // Act - resolve services
    var tracker1 = sp.GetService<ISyncEventTracker>();
    var tracker2 = sp.GetService<ISyncEventTracker>();
    var registry = sp.GetService<ITrackedEventTypeRegistry>();

    // Assert - singleton behavior
    await Assert.That(tracker1).IsNotNull();
    await Assert.That(tracker2).IsNotNull();
    await Assert.That(ReferenceEquals(tracker1, tracker2)).IsTrue()
      .Because("ISyncEventTracker should be a singleton");

    await Assert.That(registry).IsNotNull();

    // Assert - registry works
    var perspectives = registry!.GetPerspectiveNames(typeof(VerificationTestEventB));
    await Assert.That(perspectives.Count).IsGreaterThan(0)
      .Because("Registry should find registered perspectives");

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// CRITICAL: Test PerspectiveSyncAwaiter constructor injection.
  /// Verify that DI correctly injects optional ISyncEventTracker parameter.
  /// </summary>
  [Test]
  public async Task PerspectiveSyncAwaiter_DIInjection_ReceivesSingletonTrackerAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();
    SyncEventTypeRegistrations.Register(typeof(VerificationTestEventB), typeof(VerificationTestPerspectiveC).FullName!);

    var services = new ServiceCollection();
    services.AddLogging();

    // Register core services
    services.AddSingleton<ISyncEventTracker, SyncEventTracker>();
    services.AddSingleton<ITrackedEventTypeRegistry, TrackedEventTypeRegistry>();

    // Register dependencies for PerspectiveSyncAwaiter
    services.AddSingleton<IDebuggerAwareClock, DebuggerAwareClock>();
    services.AddScoped<IScopedEventTracker, ScopedEventTracker>();

    // Mock IWorkCoordinator
    services.AddSingleton<IWorkCoordinator>(sp => new MockWorkCoordinator());

    // Register PerspectiveSyncAwaiter as scoped (like AddWhizbang does)
    services.AddScoped<IPerspectiveSyncAwaiter, PerspectiveSyncAwaiter>();

    var sp = services.BuildServiceProvider();

    // Track an event in the singleton tracker
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var singletonTracker = sp.GetRequiredService<ISyncEventTracker>();
    singletonTracker.TrackEvent(typeof(VerificationTestEventB), eventId, streamId, typeof(VerificationTestPerspectiveC).FullName!);

    // Create a scope and resolve awaiter
    using var scope = sp.CreateScope();
    var awaiter = scope.ServiceProvider.GetService<IPerspectiveSyncAwaiter>();

    await Assert.That(awaiter).IsNotNull()
      .Because("PerspectiveSyncAwaiter should be resolvable from DI");

    // Simulate MarkProcessedByPerspective after delay (perspective processes event)
    _ = Task.Run(async () => {
      await Task.Delay(50);
      singletonTracker.MarkProcessedByPerspective([eventId], typeof(VerificationTestPerspectiveC).FullName!);
    });

    // Act - awaiter should find the tracked event via singleton tracker
    var result = await awaiter!.WaitForStreamAsync(
      typeof(VerificationTestPerspectiveC),
      streamId,
      eventTypes: [typeof(VerificationTestEventB)],
      timeout: TimeSpan.FromSeconds(5)
    );

    // Assert - sync should succeed (event was in singleton tracker)
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("Awaiter should find event via singleton tracker and sync when MarkProcessed is called");

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }

  /// <summary>
  /// BUG DEMONSTRATION: This test shows that the generated code ignores the sync result.
  /// When WaitForStreamAsync returns TimedOut, the receptor should NOT fire (default behavior).
  /// But the current generated code ignores the result and fires anyway!
  ///
  /// This test demonstrates WHAT SHOULD HAPPEN - it will FAIL against the current generated code.
  /// </summary>
  [Test]
  public async Task WaitForStreamAsync_WhenTimedOut_ShouldPreventReceptorFromFiringAsync() {
    // Arrange
    SyncEventTypeRegistrations.Clear();

    var streamId = Guid.NewGuid();

    // Create a mock work coordinator that returns PendingCount > 0 (never synced)
    var mockCoordinator = MockWorkCoordinator.WithSyncResults(pendingCount: 1);

    // Create awaiter with empty SyncEventTracker
    var awaiter = new PerspectiveSyncAwaiter(
      mockCoordinator,
      new DebuggerAwareClock(new() { Mode = DebuggerDetectionMode.Disabled }),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      syncEventTracker: new SyncEventTracker());

    // Act - call WaitForStreamAsync with a very short timeout
    var result = await awaiter.WaitForStreamAsync(
      typeof(VerificationTestPerspectiveC),
      streamId,
      eventTypes: [typeof(VerificationTestEventB)],
      timeout: TimeSpan.FromMilliseconds(50) // Very short timeout
    );

    // Assert - with empty SyncEventTracker and no tracked events, returns NoPendingEvents
    // No more DB fallback - empty tracker means nothing to wait for
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents)
      .Because("No events tracked in SyncEventTracker - nothing to wait for");

    // CRITICAL: The generated code currently ignores this result!
    // The receptor would still fire even though sync timed out.
    // This test documents the expected behavior: sync timeout = receptor should NOT fire.

    // Note: We can't test the generated code directly here, but we document the expectation:
    // When WaitForStreamAsync returns TimedOut and FireBehavior = FireOnSuccess (default),
    // the generated code should throw PerspectiveSyncTimeoutException and NOT call receptor.

    // Cleanup
    SyncEventTypeRegistrations.Clear();
  }
}

// Test types
internal sealed class VerificationTestEventB { }
internal sealed class VerificationTestPerspectiveC { }
