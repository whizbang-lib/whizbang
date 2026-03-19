using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.GraphQL;
using ECommerce.Contracts.Events;
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
    await SeedProductsOnceAsync(fixture);
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
  private static async Task SeedProductsOnceAsync(ServiceBusIntegrationFixture fixture) {
    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 24,  // 12 products × 2 perspectives
      bffPerspectives: 24);
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 12,  // 12 products × 1 perspective
      bffPerspectives: 12);

    var seededCount = await seedMutations.SeedProductsAsync();
    if (seededCount == 0) {
      return; // Already seeded
    }

    // Wait concurrently — both event types process in parallel through Service Bus
    await Task.WhenAll(
      productWaiter.WaitAsync(timeoutMilliseconds: 60000),
      restockWaiter.WaitAsync(timeoutMilliseconds: 60000));
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

    // Assert - Verify all 12 products materialized in BFF perspective
    var bffProducts = await fixture.BffProductLens.GetAllAsync();
    await Assert.That(bffProducts.Count).IsGreaterThanOrEqualTo(12);

    // Assert - Verify specific product data
    var teamSweatshirt = bffProducts.FirstOrDefault(p => p.Name == "Team Sweatshirt");
    await Assert.That(teamSweatshirt).IsNotNull();
    await Assert.That(teamSweatshirt!.Description).Contains("hoodie");
    await Assert.That(teamSweatshirt.Price).IsEqualTo(45.99m);
    await Assert.That(teamSweatshirt.ImageUrl).IsEqualTo("/images/sweatshirt.png");

    // Assert - Verify inventory level for Team Sweatshirt
    var sweatshirtInventory = await fixture.BffInventoryLens.GetByProductIdAsync(teamSweatshirt.ProductId);
    await Assert.That(sweatshirtInventory).IsNotNull();
    await Assert.That(sweatshirtInventory!.Quantity).IsEqualTo(75);
    await Assert.That(sweatshirtInventory.Available).IsEqualTo(75);
  }

  [Test]
  [Timeout(30_000)]
  public async Task SeedProducts_CalledTwice_DoesNotDuplicateProductsAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Second call should be idempotent — returns 0 (no new events)
    var secondSeedCount = await seedMutations.SeedProductsAsync();
    await Assert.That(secondSeedCount).IsEqualTo(0);

    // Verify only 12 products exist (no duplicates)
    var bffProducts = await fixture.BffProductLens.GetAllAsync();
    var seedProductNames = new[] {
      "Team Sweatshirt", "Team T-Shirt", "Official Match Soccer Ball",
      "Team Baseball Cap", "Foam #1 Finger", "Team Golf Umbrella",
      "Portable Stadium Seat", "Team Beanie", "Team Scarf",
      "Water Bottle", "Team Pennant", "Team Drawstring Bag"
    };

    var seededProducts = bffProducts.Where(p => seedProductNames.Contains(p.Name)).ToList();
    await Assert.That(seededProducts.Count).IsEqualTo(12);

    var distinctNames = seededProducts.Select(p => p.Name).Distinct().Count();
    await Assert.That(distinctNames).IsEqualTo(12);
  }

  [Test]
  [Timeout(30_000)]
  public async Task SeedProducts_CreatesInventoryLevels_WithCorrectStockAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var products = await fixture.BffProductLens.GetAllAsync();

    // Team Sweatshirt - 75 units
    var sweatshirt = products.FirstOrDefault(p => p.Name == "Team Sweatshirt");
    await Assert.That(sweatshirt).IsNotNull();
    var sweatshirtInventory = await fixture.BffInventoryLens.GetByProductIdAsync(sweatshirt!.ProductId);
    await Assert.That(sweatshirtInventory!.Quantity).IsEqualTo(75);

    // Team T-Shirt - 120 units
    var tshirt = products.FirstOrDefault(p => p.Name == "Team T-Shirt");
    await Assert.That(tshirt).IsNotNull();
    var tshirtInventory = await fixture.BffInventoryLens.GetByProductIdAsync(tshirt!.ProductId);
    await Assert.That(tshirtInventory!.Quantity).IsEqualTo(120);

    // Foam #1 Finger - 150 units (highest stock)
    var foamFinger = products.FirstOrDefault(p => p.Name == "Foam #1 Finger");
    await Assert.That(foamFinger).IsNotNull();
    var foamFingerInventory = await fixture.BffInventoryLens.GetByProductIdAsync(foamFinger!.ProductId);
    await Assert.That(foamFingerInventory!.Quantity).IsEqualTo(150);

    // Team Golf Umbrella - 35 units (lowest stock)
    var umbrella = products.FirstOrDefault(p => p.Name == "Team Golf Umbrella");
    await Assert.That(umbrella).IsNotNull();
    var umbrellaInventory = await fixture.BffInventoryLens.GetByProductIdAsync(umbrella!.ProductId);
    await Assert.That(umbrellaInventory!.Quantity).IsEqualTo(35);
  }

  [Test]
  [Timeout(30_000)]
  public async Task SeedProducts_SynchronizesPerspectives_AcrossBFFAndInventoryWorkerAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var bffProducts = await fixture.BffProductLens.GetAllAsync();
    var inventoryProducts = await fixture.InventoryProductLens.GetAllAsync();

    var seedProductNames = new[] {
      "Team Sweatshirt", "Team T-Shirt", "Official Match Soccer Ball",
      "Team Baseball Cap", "Foam #1 Finger", "Team Golf Umbrella",
      "Portable Stadium Seat", "Team Beanie", "Team Scarf",
      "Water Bottle", "Team Pennant", "Team Drawstring Bag"
    };

    var bffSeededProducts = bffProducts.Where(p => seedProductNames.Contains(p.Name)).ToList();
    var inventorySeededProducts = inventoryProducts.Where(p => seedProductNames.Contains(p.Name)).ToList();

    await Assert.That(bffSeededProducts.Count).IsEqualTo(12);
    await Assert.That(inventorySeededProducts.Count).IsEqualTo(12);

    foreach (var productName in seedProductNames) {
      var bffProduct = bffSeededProducts.FirstOrDefault(p => p.Name == productName);
      var inventoryProduct = inventorySeededProducts.FirstOrDefault(p => p.Name == productName);

      await Assert.That(bffProduct).IsNotNull();
      await Assert.That(inventoryProduct).IsNotNull();

      await Assert.That(bffProduct!.ProductId).IsEqualTo(inventoryProduct!.ProductId);
      await Assert.That(bffProduct.Name).IsEqualTo(inventoryProduct.Name);
      await Assert.That(bffProduct.Description).IsEqualTo(inventoryProduct.Description);
      await Assert.That(bffProduct.Price).IsEqualTo(inventoryProduct.Price);
      await Assert.That(bffProduct.ImageUrl).IsEqualTo(inventoryProduct.ImageUrl);
    }
  }
}
