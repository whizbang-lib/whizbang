using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;
using Medo;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the CreateProduct workflow.
/// Tests the complete flow: Command → Receptor → Event Store → Perspectives.
/// Uses batch-aware ServiceBus emulator. Tests within this class run sequentially
/// to avoid topic conflicts, but different test classes run in parallel.
/// </summary>
[NotInParallel]
public class CreateProductWorkflowTests {
  private static AspireIntegrationFixture? _fixture;

  // Test product IDs (UUIDv7 for proper time-ordering and uniqueness across test runs)
  private static readonly ProductId _testProd1 = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdMulti1 = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdMulti2 = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdMulti3 = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdZeroStock = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdNoImage = ProductId.From(Uuid7.NewUuid7().ToGuid());

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Get batch-specific fixture (shared with other tests in same batch)
    var batchFixture = await SharedFixtureSource.GetBatchFixtureAsync(typeof(CreateProductWorkflowTests));
    var connectionString = batchFixture.ConnectionString;

    // Derive topic suffix from test index within batch
    var testIndex = GetTestIndex();
    var topicSuffix = (testIndex % 25).ToString("D2");
    var batchIndex = testIndex / 25;

    // Create fixture with batch-scoped connection string
    _fixture = new AspireIntegrationFixture(connectionString, topicSuffix, batchIndex);
    await _fixture.InitializeAsync();
  }

  private static int GetTestIndex() {
    var testClassName = typeof(CreateProductWorkflowTests).FullName!;
    return Math.Abs(testClassName.GetHashCode()) % 1000;
  }

  // NOTE: Database cleanup happens at fixture initialization (AspireIntegrationFixture.cs:147)
  // No need for [After(Class)] cleanup - the container may be stopped by then

  /// <summary>
  /// Tests that creating a product via IDispatcher results in:
  /// 1. Event published to Event Store
  /// 2. InventoryWorker perspective materializes the product
  /// 3. BFF perspective materializes the product
  /// 4. Product is queryable via lenses
  /// </summary>
  [Test]
  public async Task CreateProduct_PublishesEvent_MaterializesInBothPerspectivesAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = _testProd1,
      Name = "Integration Test Product",
      Description = "A test product for integration testing",
      Price = 99.99m,
      ImageUrl = "/images/test-product.png",
      InitialStock = 50
    };

    // Act
    await fixture.Dispatcher.SendAsync(command);

    // Process events through perspectives (simulates Service Bus event processing)
    await fixture.WaitForEventProcessingAsync();

    // Assert - Verify in InventoryWorker perspective
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo(command.Name);
    await Assert.That(inventoryProduct.Description).IsEqualTo(command.Description);
    await Assert.That(inventoryProduct.Price).IsEqualTo(command.Price);
    await Assert.That(inventoryProduct.ImageUrl).IsEqualTo(command.ImageUrl);

    // Assert - Verify inventory was initialized with initial stock
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(command.ProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(command.InitialStock);
    await Assert.That(inventoryLevel.Available).IsEqualTo(command.InitialStock);

    // Assert - Verify in BFF perspective
    var bffProduct = await fixture.BffProductLens.GetByIdAsync(command.ProductId);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Name).IsEqualTo(command.Name);
    await Assert.That(bffProduct.Description).IsEqualTo(command.Description);
    await Assert.That(bffProduct.Price).IsEqualTo(command.Price);

    // Assert - Verify BFF inventory perspective
    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(command.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(command.InitialStock);
  }

  /// <summary>
  /// Tests that creating multiple products in sequence works correctly.
  /// </summary>
  [Test]
  public async Task CreateProduct_MultipleProducts_AllMaterializeCorrectlyAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");


    var commands = new[] {
      new CreateProductCommand {
        ProductId = _testProdMulti1,
        Name = "Product 1",
        Description = "First test product",
        Price = 10.00m,
        ImageUrl = "/images/product1.png",
        InitialStock = 100
      },
      new CreateProductCommand {
        ProductId = _testProdMulti2,
        Name = "Product 2",
        Description = "Second test product",
        Price = 20.00m,
        ImageUrl = "/images/product2.png",
        InitialStock = 200
      },
      new CreateProductCommand {
        ProductId = _testProdMulti3,
        Name = "Product 3",
        Description = "Third test product",
        Price = 30.00m,
        ImageUrl = "/images/product3.png",
        InitialStock = 300
      }
    };

    // Act - Create each product and wait for event processing
    // This ensures events are processed in order and perspectives are updated before the next product
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
      await fixture.WaitForEventProcessingAsync();
    }

    // Assert - Verify all products materialized in InventoryWorker perspective
    foreach (var command in commands) {
      var product = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId);
      await Assert.That(product).IsNotNull();
      await Assert.That(product!.Name).IsEqualTo(command.Name);
      await Assert.That(product.Price).IsEqualTo(command.Price);

      var inventory = await fixture.InventoryLens.GetByProductIdAsync(command.ProductId);
      await Assert.That(inventory).IsNotNull();
      await Assert.That(inventory!.Quantity).IsEqualTo(command.InitialStock);
    }

    // Assert - Verify all products materialized in BFF perspective
    foreach (var command in commands) {
      var product = await fixture.BffProductLens.GetByIdAsync(command.ProductId);
      await Assert.That(product).IsNotNull();
      await Assert.That(product!.Name).IsEqualTo(command.Name);

      var inventory = await fixture.BffInventoryLens.GetByProductIdAsync(command.ProductId);
      await Assert.That(inventory).IsNotNull();
      await Assert.That(inventory!.Quantity).IsEqualTo(command.InitialStock);
    }
  }

  /// <summary>
  /// Tests that creating a product with zero initial stock works correctly.
  /// </summary>
  [Test]
  public async Task CreateProduct_ZeroInitialStock_MaterializesWithZeroQuantityAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");


    var command = new CreateProductCommand {
      ProductId = _testProdZeroStock,
      Name = "Zero Stock Product",
      Description = "Product with no initial inventory",
      Price = 49.99m,
      ImageUrl = "/images/zero-stock.png",
      InitialStock = 0
    };

    // Act
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Verify product exists with zero inventory
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(command.ProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(0);
    await Assert.That(inventoryLevel.Available).IsEqualTo(0);

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(command.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(0);
  }

  /// <summary>
  /// Tests that creating a product without an image URL works correctly (nullable field).
  /// </summary>
  [Test]
  public async Task CreateProduct_NoImageUrl_MaterializesWithNullImageAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    Console.WriteLine($"[TEST] Starting CreateProduct_NoImageUrl test with ProductId: {_testProdNoImage}");

    var command = new CreateProductCommand {
      ProductId = _testProdNoImage,
      Name = "No Image Product",
      Description = "Product without an image",
      Price = 19.99m,
      ImageUrl = null,
      InitialStock = 25
    };

    // Act
    Console.WriteLine("[TEST] Sending CreateProductCommand...");
    await fixture.Dispatcher.SendAsync(command);
    Console.WriteLine("[TEST] Command sent, waiting for event processing...");

    // DIAGNOSTIC: Dump event types and associations
    await fixture.DumpEventTypesAndAssociationsAsync();

    await fixture.WaitForEventProcessingAsync();
    Console.WriteLine("[TEST] Event processing complete");

    // Assert - Verify product exists with null ImageUrl
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.ImageUrl).IsNull();

    var bffProduct = await fixture.BffProductLens.GetByIdAsync(command.ProductId);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.ImageUrl).IsNull();
  }
}
