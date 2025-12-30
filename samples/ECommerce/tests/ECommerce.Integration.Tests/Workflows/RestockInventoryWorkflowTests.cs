// NOTE: Database cleanup happens at fixture initialization (AspireIntegrationFixture.cs:147)
// No need for [After(Class)] cleanup - the container may be stopped by then

using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;
using Medo;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the RestockInventory workflow.
/// Tests the complete flow: Command → Receptor → Event Store → Perspectives.
/// Uses batch-aware ServiceBus emulator. Tests within this class run sequentially
/// to avoid topic conflicts, but different test classes run in parallel.
/// </summary>
[NotInParallel]
public class RestockInventoryWorkflowTests {
  private static AspireIntegrationFixture? _fixture;

  // Test product IDs (UUIDv7 for proper time-ordering and uniqueness across test runs)
  private static readonly ProductId _testProdRestock1 = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdMultiRestock = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdRestockZero = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdRestockZeroQty = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdLargeRestock = ProductId.From(Uuid7.NewUuid7().ToGuid());

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Get batch-specific fixture (shared with other tests in same batch)
    var testIndex = GetTestIndex();
    var batchFixture = await SharedFixtureSource.GetBatchFixtureAsync(testIndex);
    var connectionString = batchFixture.ConnectionString;

    // Derive topic suffix from test index within batch
    var topicSuffix = (testIndex % 25).ToString("D2");
    var batchIndex = testIndex / 25;

    // Create fixture with batch-scoped connection string
    _fixture = new AspireIntegrationFixture(connectionString, topicSuffix, batchIndex);
    await _fixture.InitializeAsync();

    // Clean database before each test to ensure isolated state
    // This is critical for integration tests that check specific quantities
    await _fixture.CleanupDatabaseAsync();
  }

  private static int GetTestIndex() {
    // Assign fixed index for this test class (all 4 workflow test classes use batch 0)
    return 3; // RestockInventoryWorkflowTests = index 3
  }


  /// <summary>
  /// Tests that restocking inventory via IDispatcher results in:
  /// 1. InventoryRestockedEvent published to Event Store
  /// 2. InventoryWorker perspective updates inventory levels
  /// 3. BFF perspective updates inventory levels
  /// 4. Updated inventory is queryable via lenses
  /// </summary>
  [Test]
  public async Task RestockInventory_PublishesEvent_UpdatesPerspectivesAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");


    // First, create a product with initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdRestock1,
      Name = "Restockable Product",
      Description = "Product that will be restocked",
      Price = 50.00m,
      ImageUrl = "/images/restock.png",
      InitialStock = 10
    };
    await fixture.Dispatcher.SendAsync(createCommand);
    await fixture.WaitForEventProcessingAsync();

    // Act - Restock inventory
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdRestock1,
      QuantityToAdd = 50
    };
    await fixture.Dispatcher.SendAsync(restockCommand);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Verify InventoryWorker perspective updated
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(60); // 10 + 50
    await Assert.That(inventoryLevel.Available).IsEqualTo(60);

    // Assert - Verify BFF perspective updated
    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(60); // 10 + 50
  }

  /// <summary>
  /// Tests that multiple restock operations accumulate correctly.
  /// </summary>
  [Test]
  public async Task RestockInventory_MultipleRestocks_AccumulatesCorrectlyAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");


    // Create product with initial stock of 5
    var createCommand = new CreateProductCommand {
      ProductId = _testProdMultiRestock,
      Name = "Multi-Restock Product",
      Description = "Product restocked multiple times",
      Price = 25.00m,
      ImageUrl = "/images/multi-restock.png",
      InitialStock = 5
    };
    await fixture.Dispatcher.SendAsync(createCommand);
    await fixture.WaitForEventProcessingAsync();

    // Act - Perform multiple restock operations
    // Wait between each restock to ensure events are processed and perspectives are updated
    var restockCommands = new[] {
      new RestockInventoryCommand { ProductId = _testProdMultiRestock, QuantityToAdd = 10 },
      new RestockInventoryCommand { ProductId = _testProdMultiRestock, QuantityToAdd = 20 },
      new RestockInventoryCommand { ProductId = _testProdMultiRestock, QuantityToAdd = 15 }
    };

    foreach (var restockCommand in restockCommands) {
      await fixture.Dispatcher.SendAsync(restockCommand);
      await fixture.WaitForEventProcessingAsync();
    }

    // Assert - Verify total quantity = 5 + 10 + 20 + 15 = 50
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(50);
    await Assert.That(inventoryLevel.Available).IsEqualTo(50);

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(50);
  }

  /// <summary>
  /// Tests that restocking from zero inventory works correctly.
  /// </summary>
  [Test]
  public async Task RestockInventory_FromZeroStock_IncreasesCorrectlyAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");


    // Create product with zero initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdRestockZero,
      Name = "Restock from Zero",
      Description = "Product starting with no inventory",
      Price = 75.00m,
      ImageUrl = "/images/restock-zero.png",
      InitialStock = 0
    };
    await fixture.Dispatcher.SendAsync(createCommand);
    await fixture.WaitForEventProcessingAsync();

    // Act - Restock from zero
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdRestockZero,
      QuantityToAdd = 100
    };
    await fixture.Dispatcher.SendAsync(restockCommand);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Verify quantity increased from 0 to 100
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(100);

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(100);
  }

  /// <summary>
  /// Tests that restocking with zero quantity is handled correctly (edge case).
  /// </summary>
  [Test]
  public async Task RestockInventory_ZeroQuantity_NoChangeAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");


    // Create product with initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdRestockZeroQty,
      Name = "Zero Quantity Restock",
      Description = "Testing zero quantity restock",
      Price = 30.00m,
      ImageUrl = "/images/zero-qty.png",
      InitialStock = 25
    };
    await fixture.Dispatcher.SendAsync(createCommand);
    await fixture.WaitForEventProcessingAsync();

    // Act - Restock with zero quantity
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdRestockZeroQty,
      QuantityToAdd = 0
    };
    await fixture.Dispatcher.SendAsync(restockCommand);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Verify quantity unchanged
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(25); // No change

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(25); // No change
  }

  /// <summary>
  /// Tests that restocking large quantities works correctly.
  /// </summary>
  [Test]
  public async Task RestockInventory_LargeQuantity_HandlesCorrectlyAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");


    // Create product with small initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdLargeRestock,
      Name = "Large Restock Product",
      Description = "Product with large inventory increase",
      Price = 100.00m,
      ImageUrl = "/images/large-restock.png",
      InitialStock = 50
    };
    await fixture.Dispatcher.SendAsync(createCommand);
    await fixture.WaitForEventProcessingAsync();

    // Act - Restock with large quantity
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdLargeRestock,
      QuantityToAdd = 10000
    };
    await fixture.Dispatcher.SendAsync(restockCommand);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Verify large quantity handled
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(10050); // 50 + 10000

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(10050);
  }
}
