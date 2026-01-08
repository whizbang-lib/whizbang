// NOTE: Database cleanup happens at fixture initialization (AspireIntegrationFixture.cs:147)
// No need for [After(Class)] cleanup - the container may be stopped by then

using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.GraphQL;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the product seeding workflow.
/// Tests the complete flow: SeedMutations → CreateProductCommand → ProductCreatedEvent → Perspectives.
/// Uses batch-aware ServiceBus emulator. Tests within this class run sequentially
/// to avoid topic conflicts, but different test classes run in parallel.
/// </summary>
[NotInParallel]
public class SeedProductsWorkflowTests {
  private static ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Get SHARED ServiceBus resources (emulator + single static ServiceBusClient)
    var testIndex = GetTestIndex();
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(testIndex);

    // Create fixture with shared client (per-test PostgreSQL + hosts, but shared ServiceBusClient)
    _fixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, 0);
    await _fixture.InitializeAsync();

    // Clean database before each test to ensure isolated state
    // This is critical for idempotency tests that check if seeding skips on second call
    await _fixture.CleanupDatabaseAsync();
  }

  private static int GetTestIndex() {
    // Assign fixed index for this test class (all 4 workflow test classes use batch 0)
    return 1; // SeedProductsWorkflowTests = index 1
  }

  [After(Test)]
  public async Task CleanupAsync() {
    // CRITICAL: Drain Service Bus messages BEFORE disposing fixture
    // Service Bus subscriptions (sub-00-a, sub-01-a) are PERSISTENT - messages remain after hosts stop
    // Without draining, Test 2's BFF receives Test 1's old messages, causing assertion failures
    if (_fixture != null) {
      try {
        // Drain any remaining messages from Service Bus subscriptions
        await _fixture.CleanupDatabaseAsync();
      } catch (Exception ex) {
        Console.WriteLine($"[After(Test)] Warning: Cleanup encountered error (non-critical): {ex.Message}");
      }

      // Dispose fixture to stop hosts and close connections
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
  public async Task SeedProducts_CreatesAllProducts_MaterializesInAllPerspectivesAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Create SeedMutations instance with test dependencies
    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act - Call seed mutation
    var seededCount = await seedMutations.SeedProductsAsync();

    // Wait for event processing
    await fixture.WaitForEventProcessingAsync();

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
  public async Task SeedProducts_CalledTwice_DoesNotDuplicateProductsAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act - Call seed mutation TWICE
    var firstSeedCount = await seedMutations.SeedProductsAsync();
    await fixture.WaitForEventProcessingAsync();

    var secondSeedCount = await seedMutations.SeedProductsAsync();
    await fixture.WaitForEventProcessingAsync();

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
  public async Task SeedProducts_CreatesInventoryLevels_WithCorrectStockAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act
    await seedMutations.SeedProductsAsync();
    await fixture.WaitForEventProcessingAsync();

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
  public async Task SeedProducts_SynchronizesPerspectives_AcrossBFFAndInventoryWorkerAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var seedMutations = new SeedMutations(
      fixture.Dispatcher,
      fixture.BffProductLens,
      fixture.GetLogger<SeedMutations>());

    // Act
    await seedMutations.SeedProductsAsync();
    await fixture.WaitForEventProcessingAsync();

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
