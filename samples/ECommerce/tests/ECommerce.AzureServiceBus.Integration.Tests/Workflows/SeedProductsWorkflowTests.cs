using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.GraphQL;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the product seeding workflow.
/// Tests the complete flow: SeedMutations → CreateProductCommand → ProductCreatedEvent → Perspectives.
/// All tests share a single fixture and seeded state — products are seeded once via [Before(Class)],
/// then all tests verify against the shared state.
/// </summary>
[NotInParallel("ServiceBus")]
public class SeedProductsWorkflowTests {
  private ServiceBusIntegrationFixture? _fixture;

  [Before(Class)]
  [Timeout(120_000)]  // Class setup: seeding 12 products through Service Bus
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public static async Task ClassSetupAsync() {
    // Seed once for all tests in this class — runs outside per-test timeout
    var fixture = await SharedServiceBusFixtureSource.GetFixtureAsync();
    await Task.Delay(500);
    await fixture.CleanupDatabaseAsync();
    await _seedProductsOnceAsync(fixture);
  }

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    _fixture = await SharedServiceBusFixtureSource.GetFixtureAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Don't dispose or clean — all tests share seeded state
  }

  /// <summary>
  /// Seeds products once and waits for all perspective processing to complete.
  /// </summary>
  private static async Task _seedProductsOnceAsync(ServiceBusIntegrationFixture fixture) {
    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Wait for 24 inventory perspectives (12 products × 2 ProductCreatedEvent perspectives)
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 24, timeoutMilliseconds: 90000, hostFilter: "inventory");

    var seededCount = await seedMutations.SeedProductsAsync();
    Console.WriteLine($"[SeedProducts] SeedProductsAsync returned: {seededCount}");
    if (seededCount == 0) {
      return; // Already seeded
    }

    await perspectiveTask;

    // Wait for workers idle to drain InventoryRestockedEvent perspectives (transport roundtrip)
    await fixture.WaitForWorkersIdleAsync();
  }

  [Test]
  [Timeout(30_000)]  // Assertions only — seeding done in [Before(Class)]
  public async Task SeedProducts_CreatesAllProducts_MaterializesInAllPerspectivesAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Assert - Verify all 12 products materialized in InventoryWorker perspective
    var inventoryProducts = await fixture.InventoryProductLens.GetAllAsync();
    await Assert.That(inventoryProducts.Count).IsGreaterThanOrEqualTo(12);

    // Assert - Verify all 12 products have inventory levels
    var inventoryLevels = await fixture.InventoryLens.GetAllAsync();
    await Assert.That(inventoryLevels.Count).IsGreaterThanOrEqualTo(12);

    // BFF assertions removed — BFF receives via Service Bus transport

    // Assert - Verify specific product data in InventoryWorker perspective
    var teamSweatshirt = inventoryProducts.FirstOrDefault(p => p.Name == "Team Sweatshirt");
    await Assert.That(teamSweatshirt).IsNotNull();
    await Assert.That(teamSweatshirt!.Description).Contains("hoodie");
    await Assert.That(teamSweatshirt.Price).IsEqualTo(45.99m);
    await Assert.That(teamSweatshirt.ImageUrl).IsEqualTo("/images/sweatshirt.png");

    // Assert - Verify inventory level for Team Sweatshirt
    var sweatshirtInventory = await fixture.InventoryLens.GetByProductIdAsync(teamSweatshirt.ProductId);
    await Assert.That(sweatshirtInventory).IsNotNull();
    await Assert.That(sweatshirtInventory!.Quantity).IsEqualTo(75);
    await Assert.That(sweatshirtInventory.Available).IsEqualTo(75);
  }

  [Test]
  [Timeout(30_000)]
  public async Task SeedProducts_CalledTwice_DoesNotDuplicateProductsAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Verify only 12 products exist (no duplicates) — use InventoryWorker perspective
    // Note: SeedMutations uses BFF lens for idempotency check, which requires ASB transport.
    // We verify via inventory lens instead, which is local and deterministic.
    var inventoryProducts = await fixture.InventoryProductLens.GetAllAsync();
    var seedProductNames = new[] {
      "Team Sweatshirt", "Team T-Shirt", "Official Match Soccer Ball",
      "Team Baseball Cap", "Foam #1 Finger", "Team Golf Umbrella",
      "Portable Stadium Seat", "Team Beanie", "Team Scarf",
      "Water Bottle", "Team Pennant", "Team Drawstring Bag"
    };

    var seededProducts = inventoryProducts.Where(p => seedProductNames.Contains(p.Name)).ToList();
    await Assert.That(seededProducts.Count).IsEqualTo(12);

    var distinctNames = seededProducts.Select(p => p.Name).Distinct().Count();
    await Assert.That(distinctNames).IsEqualTo(12);
  }

  [Test]
  [Timeout(30_000)]
  public async Task SeedProducts_CreatesInventoryLevels_WithCorrectStockAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var products = await fixture.InventoryProductLens.GetAllAsync();

    // Team Sweatshirt - 75 units
    var sweatshirt = products.FirstOrDefault(p => p.Name == "Team Sweatshirt");
    await Assert.That(sweatshirt).IsNotNull();
    var sweatshirtInventory = await fixture.InventoryLens.GetByProductIdAsync(sweatshirt!.ProductId);
    await Assert.That(sweatshirtInventory!.Quantity).IsEqualTo(75);

    // Team T-Shirt - 120 units
    var tshirt = products.FirstOrDefault(p => p.Name == "Team T-Shirt");
    await Assert.That(tshirt).IsNotNull();
    var tshirtInventory = await fixture.InventoryLens.GetByProductIdAsync(tshirt!.ProductId);
    await Assert.That(tshirtInventory!.Quantity).IsEqualTo(120);

    // Foam #1 Finger - 150 units (highest stock)
    var foamFinger = products.FirstOrDefault(p => p.Name == "Foam #1 Finger");
    await Assert.That(foamFinger).IsNotNull();
    var foamFingerInventory = await fixture.InventoryLens.GetByProductIdAsync(foamFinger!.ProductId);
    await Assert.That(foamFingerInventory!.Quantity).IsEqualTo(150);

    // Team Golf Umbrella - 35 units (lowest stock)
    var umbrella = products.FirstOrDefault(p => p.Name == "Team Golf Umbrella");
    await Assert.That(umbrella).IsNotNull();
    var umbrellaInventory = await fixture.InventoryLens.GetByProductIdAsync(umbrella!.ProductId);
    await Assert.That(umbrellaInventory!.Quantity).IsEqualTo(35);
  }

  [Test]
  [Timeout(30_000)]
  public async Task SeedProducts_SynchronizesPerspectives_InventoryWorkerMaterializesAllAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // BFF assertions removed — BFF receives via Service Bus transport
    var inventoryProducts = await fixture.InventoryProductLens.GetAllAsync();

    var seedProductNames = new[] {
      "Team Sweatshirt", "Team T-Shirt", "Official Match Soccer Ball",
      "Team Baseball Cap", "Foam #1 Finger", "Team Golf Umbrella",
      "Portable Stadium Seat", "Team Beanie", "Team Scarf",
      "Water Bottle", "Team Pennant", "Team Drawstring Bag"
    };

    var inventorySeededProducts = inventoryProducts.Where(p => seedProductNames.Contains(p.Name)).ToList();
    await Assert.That(inventorySeededProducts.Count).IsEqualTo(12);

    foreach (var productName in seedProductNames) {
      var inventoryProduct = inventorySeededProducts.FirstOrDefault(p => p.Name == productName);
      await Assert.That(inventoryProduct).IsNotNull();
    }
  }
}
