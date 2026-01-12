// NOTE: Database cleanup happens at fixture initialization (AspireIntegrationFixture.cs:147)
// No need for [After(Class)] cleanup - the container may be stopped by then

using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.GraphQL;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the product seeding workflow.
/// Tests the complete flow: SeedMutations → CreateProductCommand → ProductCreatedEvent → Perspectives.
/// Each test gets its own PostgreSQL + hosts. ServiceBus emulator is shared via SharedFixtureSource.
/// </summary>
[NotInParallel]
public class SeedProductsWorkflowTests {
  private static ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    var testIndex = 1;
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(testIndex);
    _fixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, 0);
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      try {
        await _fixture.CleanupDatabaseAsync();
      } catch (Exception ex) {
        Console.WriteLine($"[After(Test)] Warning: Cleanup encountered error (non-critical): {ex.Message}");
      }
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }


  /// <summary>
  /// Tests that calling SeedProducts mutation results in:
  /// 1. 12 CreateProductCommands dispatched
  /// 2. 12 ProductCreatedEvents published to Event Store
  /// 3. Products materialized in InventoryWorker perspectives (Product + Inventory)
  /// 4. Products materialized in BFF perspectives (ProductCatalog + InventoryLevels)
  /// 5. Products queryable via lenses
  /// </summary>
  [Test]
  [Timeout(180000)] // 180 seconds: container init (~15s) + bulk event processing (12 products)
  public async Task SeedProducts_CreatesAllProducts_MaterializesInAllPerspectivesAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange


    // Create SeedMutations instance with test dependencies
    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act - Call seed mutation
    using var waiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 24,
      bffPerspectives: 24);
    var seededCount = await seedMutations.SeedProductsAsync();

    // Wait for all perspectives to complete (12 products × 4 perspectives each = 48)
    await waiter.WaitAsync(timeoutMilliseconds: 45000);

    // Assert - Verify seeding result
    await Assert.That(seededCount).IsEqualTo(12);

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

  /// <summary>
  /// Tests that SeedProducts is idempotent - calling it twice doesn't duplicate products.
  /// </summary>
  [Test]
  [Timeout(180000)] // 180 seconds: container init (~15s) + bulk event processing (12 products)
  public async Task SeedProducts_CalledTwice_DoesNotDuplicateProductsAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange


    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act - Call seed mutation TWICE
    using var waiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 24,
      bffPerspectives: 24);
    var firstSeedCount = await seedMutations.SeedProductsAsync();
    await waiter.WaitAsync(timeoutMilliseconds: 120000);

    var secondSeedCount = await seedMutations.SeedProductsAsync();
    // No wait needed - second call is idempotent and returns 0 (no events published)

    // Assert - First call should seed 12 products
    await Assert.That(firstSeedCount).IsEqualTo(12);

    // Assert - Second call should skip seeding (idempotent)
    await Assert.That(secondSeedCount).IsEqualTo(0);

    // Assert - Verify only 12 products exist (no duplicates)
    var bffProducts = await fixture.BffProductLens.GetAllAsync();

    // Count products that match seed product names
    var seedProductNames = new[] {
      "Team Sweatshirt", "Team T-Shirt", "Official Match Soccer Ball",
      "Team Baseball Cap", "Foam #1 Finger", "Team Golf Umbrella",
      "Portable Stadium Seat", "Team Beanie", "Team Scarf",
      "Water Bottle", "Team Pennant", "Team Drawstring Bag"
    };

    var seededProducts = bffProducts.Where(p => seedProductNames.Contains(p.Name)).ToList();
    await Assert.That(seededProducts.Count).IsEqualTo(12);

    // Verify no duplicate names
    var distinctNames = seededProducts.Select(p => p.Name).Distinct().Count();
    await Assert.That(distinctNames).IsEqualTo(12);
  }

  /// <summary>
  /// Tests that seeded products have correct stock levels in both perspectives.
  /// </summary>
  [Test]
  [Timeout(180000)] // 180 seconds: container init (~15s) + bulk event processing (12 products)
  public async Task SeedProducts_CreatesInventoryLevels_WithCorrectStockAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange


    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act
    using var waiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 24,
      bffPerspectives: 24);
    await seedMutations.SeedProductsAsync();
    await waiter.WaitAsync(timeoutMilliseconds: 120000);

    // Assert - Verify specific product stock levels
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

  /// <summary>
  /// Tests that seeded products are properly synchronized across both worker and BFF perspectives.
  /// </summary>
  [Test]
  [Timeout(180000)] // 180 seconds: container init (~15s) + bulk event processing (12 products)
  public async Task SeedProducts_SynchronizesPerspectives_AcrossBFFAndInventoryWorkerAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange


    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act
    using var waiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 24,
      bffPerspectives: 24);
    await seedMutations.SeedProductsAsync();
    await waiter.WaitAsync(timeoutMilliseconds: 120000);

    // Assert - Get all products from both perspectives
    var bffProducts = await fixture.BffProductLens.GetAllAsync();
    var inventoryProducts = await fixture.InventoryProductLens.GetAllAsync();

    // Verify both perspectives have the same product count
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

    // Verify each product exists in both perspectives with matching data
    foreach (var productName in seedProductNames) {
      var bffProduct = bffSeededProducts.FirstOrDefault(p => p.Name == productName);
      var inventoryProduct = inventorySeededProducts.FirstOrDefault(p => p.Name == productName);

      await Assert.That(bffProduct).IsNotNull();
      await Assert.That(inventoryProduct).IsNotNull();

      // Verify matching product data
      await Assert.That(bffProduct!.ProductId).IsEqualTo(inventoryProduct!.ProductId);
      await Assert.That(bffProduct.Name).IsEqualTo(inventoryProduct.Name);
      await Assert.That(bffProduct.Description).IsEqualTo(inventoryProduct.Description);
      await Assert.That(bffProduct.Price).IsEqualTo(inventoryProduct.Price);
      await Assert.That(bffProduct.ImageUrl).IsEqualTo(inventoryProduct.ImageUrl);
    }
  }
}
