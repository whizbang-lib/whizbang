using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the RestockInventory workflow.
/// Tests the complete flow: Command → Receptor → Event Store → Perspectives.
/// </summary>
[NotInParallel]
public class RestockInventoryWorkflowTests {
  private static SharedIntegrationFixture? _fixture;

  // Test product IDs (deterministic GUIDs for reproducibility)
  private static readonly ProductId TestProdRestock1 = ProductId.From(Guid.Parse("00000000-0000-0000-0000-000000000101"));
  private static readonly ProductId TestProdMultiRestock = ProductId.From(Guid.Parse("00000000-0000-0000-0000-000000000102"));
  private static readonly ProductId TestProdRestockZero = ProductId.From(Guid.Parse("00000000-0000-0000-0000-000000000103"));
  private static readonly ProductId TestProdRestockZeroQty = ProductId.From(Guid.Parse("00000000-0000-0000-0000-000000000104"));
  private static readonly ProductId TestProdLargeRestock = ProductId.From(Guid.Parse("00000000-0000-0000-0000-000000000105"));

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    _fixture = await SharedFixtureSource.GetFixtureAsync();
  }

  [After(Class)]
  public static async Task CleanupAsync() {
    if (_fixture != null) {
      await _fixture.CleanupDatabaseAsync();
    }
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
      ProductId = TestProdRestock1,
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
      ProductId = TestProdRestock1,
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
      ProductId = TestProdMultiRestock,
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
      new RestockInventoryCommand { ProductId = TestProdMultiRestock, QuantityToAdd = 10 },
      new RestockInventoryCommand { ProductId = TestProdMultiRestock, QuantityToAdd = 20 },
      new RestockInventoryCommand { ProductId = TestProdMultiRestock, QuantityToAdd = 15 }
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
      ProductId = TestProdRestockZero,
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
      ProductId = TestProdRestockZero,
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
      ProductId = TestProdRestockZeroQty,
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
      ProductId = TestProdRestockZeroQty,
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
      ProductId = TestProdLargeRestock,
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
      ProductId = TestProdLargeRestock,
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
