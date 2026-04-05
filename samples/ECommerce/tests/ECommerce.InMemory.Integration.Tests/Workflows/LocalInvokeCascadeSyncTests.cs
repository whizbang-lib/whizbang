using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InMemory.Integration.Tests.Fixtures;
using ECommerce.InventoryWorker.Perspectives;
using Medo;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace ECommerce.InMemory.Integration.Tests.Workflows;

/// <summary>
/// Integration tests verifying the full cascade pipeline:
/// LocalInvokeAsync → receptor returns event → cascade → outbox → event store →
/// perspective events → PerspectiveWorker processes → WaitForStreamAsync completes.
///
/// These tests reproduce the JDNext OrchestratorAgent pattern where LocalInvokeAsync
/// is used to invoke a command, the receptor returns a cascaded event, and then
/// WaitForStreamAsync is called to wait for the perspective to process that event.
/// </summary>
[NotInParallel("InMemory")]
[Timeout(60_000)]
public class LocalInvokeCascadeSyncTests {
  private InMemoryIntegrationFixture? _fixture;

  private static readonly ProductId _testProductId = ProductId.From(Uuid7.NewUuid7().ToGuid());

  [Before(Test)]
  [RequiresUnreferencedCode("Test code")]
  [RequiresDynamicCode("Test code")]
  public async Task SetupAsync() {
    _fixture = await SharedInMemoryFixtureSource.GetFixtureAsync();
    await _fixture.CleanupDatabaseAsync();
  }

  /// <summary>
  /// Tests the cascade pipeline using a perspective registered in SyncEventTypeRegistrations.
  /// InventoryLevelsPerspective is registered via [AwaitPerspectiveSync] attribute.
  /// This exercises the full pipeline: cascade → tracker → perspective worker → signal.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_CascadedEvent_WaitForRegisteredPerspective_CompletesAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = _testProductId,
      Name = "Cascade Sync Test Product",
      Description = "Testing cascade → WaitForStreamAsync pipeline",
      Price = 42.00m,
      InitialStock = 0
    };

    // Step 1: LocalInvokeAsync — receptor returns ProductCreatedEvent which is auto-cascaded
    // The cascade path calls _generateEventIdAndTrack which tracks in SyncEventTracker
    // for InventoryLevelsPerspective (registered via [AwaitPerspectiveSync])
    var createdEvent = await fixture.Dispatcher.LocalInvokeAsync<ProductCreatedEvent>(command);

    await Assert.That(createdEvent).IsNotNull()
      .Because("Receptor should return ProductCreatedEvent");

    // Step 2: WaitForStreamAsync — wait for InventoryLevelsPerspective to process the event
    // This is the perspective registered in SyncEventTypeRegistrations via [AwaitPerspectiveSync]
    using var scope = fixture.CreateInventoryScope();
    var syncAwaiter = scope.ServiceProvider.GetRequiredService<IPerspectiveSyncAwaiter>();

    var syncResult = await syncAwaiter.WaitForStreamAsync(
        typeof(InventoryLevelsPerspective),
        _testProductId.Value,
        eventTypes: [typeof(ProductCreatedEvent)],
        timeout: TimeSpan.FromSeconds(10));

    // Assert — should complete, NOT time out
    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.Synced)
      .Because("WaitForStreamAsync should complete after PerspectiveWorker processes the cascaded event");
  }

  /// <summary>
  /// Tests that an unregistered perspective (not in SyncEventTypeRegistrations) returns NoPendingEvents.
  /// This demonstrates that without [AwaitPerspectiveSync], events are not tracked for that perspective.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_UnregisteredPerspective_ReturnsNoPendingEventsAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var productId = ProductId.From(TrackedGuid.NewMedo());
    var command = new CreateProductCommand {
      ProductId = productId,
      Name = "Unregistered Perspective Test",
      Description = "Testing with perspective NOT in SyncEventTypeRegistrations",
      Price = 10.00m,
      InitialStock = 0
    };

    var createdEvent = await fixture.Dispatcher.LocalInvokeAsync<ProductCreatedEvent>(command);
    await Assert.That(createdEvent).IsNotNull();

    using var scope = fixture.CreateInventoryScope();
    var syncAwaiter = scope.ServiceProvider.GetRequiredService<IPerspectiveSyncAwaiter>();

    // ProductCatalogPerspective is NOT registered in SyncEventTypeRegistrations
    // (no [AwaitPerspectiveSync] attribute references it for ProductCreatedEvent)
    var syncResult = await syncAwaiter.WaitForStreamAsync(
        typeof(ProductCatalogPerspective),
        productId.Value,
        eventTypes: [typeof(ProductCreatedEvent)],
        timeout: TimeSpan.FromSeconds(5));

    // Should return NoPendingEvents because the tracker has no entries for this perspective
    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents)
      .Because("ProductCatalogPerspective is not registered in SyncEventTypeRegistrations");
  }
}
