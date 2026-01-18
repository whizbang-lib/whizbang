using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InMemory.Integration.Tests.Fixtures;

namespace ECommerce.InMemory.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the CreateProduct workflow using InProcessTransport.
/// Tests the complete flow: Command → Receptor → Event Store → Perspectives.
/// Uses in-memory transport for fast, deterministic testing without Service Bus infrastructure.
/// This isolates business logic testing from Azure Service Bus concerns.
/// Each test gets its own isolated fixture and database for parallel execution.
/// </summary>
[Timeout(20_000)]  // 20s timeout per test
public class CreateProductWorkflowTests {
  private InMemoryIntegrationFixture? _fixture;

  // Test product IDs (deterministic GUIDs for reproducibility)
  private static readonly ProductId _testProd1 = ProductId.From(Guid.Parse("10000000-0000-0000-0000-000000000001"));
  private static readonly ProductId _testProdMulti1 = ProductId.From(Guid.Parse("10000000-0000-0000-0000-000000000011"));
  private static readonly ProductId _testProdMulti2 = ProductId.From(Guid.Parse("10000000-0000-0000-0000-000000000012"));
  private static readonly ProductId _testProdMulti3 = ProductId.From(Guid.Parse("10000000-0000-0000-0000-000000000013"));
  private static readonly ProductId _testProdZeroStock = ProductId.From(Guid.Parse("10000000-0000-0000-0000-000000000020"));
  private static readonly ProductId _testProdNoImage = ProductId.From(Guid.Parse("10000000-0000-0000-0000-000000000030"));

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Create isolated fixture for this test (not shared)
    _fixture = new InMemoryIntegrationFixture();
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
    }
  }

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

    // Act - Create waiter BEFORE sending command to avoid race condition
    using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(command);
    await productWaiter.WaitAsync(timeoutMilliseconds: 15000);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 15000);

    // Assert - Verify in InventoryWorker perspective
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo(command.Name);
    await Assert.That(inventoryProduct.Description).IsEqualTo(command.Description);
    await Assert.That(inventoryProduct.Price).IsEqualTo(command.Price);
    await Assert.That(inventoryProduct.ImageUrl).IsEqualTo(command.ImageUrl);

    // Assert - Verify inventory was initialized with initial stock
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(command.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(command.InitialStock);
    await Assert.That(inventoryLevel.Available).IsEqualTo(command.InitialStock);

    // Assert - Verify in BFF perspective
    var bffProduct = await fixture.BffProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Name).IsEqualTo(command.Name);
    await Assert.That(bffProduct.Description).IsEqualTo(command.Description);
    await Assert.That(bffProduct.Price).IsEqualTo(command.Price);

    // Assert - Verify BFF inventory perspective
    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(command.ProductId.Value);
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
    // NOTE: Both hosts subscribe to same topics, so BFF also receives InventoryRestockedEvent
    foreach (var command in commands) {
      using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
        inventoryPerspectives: 2,
        bffPerspectives: 2);
      using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
        inventoryPerspectives: 1,
        bffPerspectives: 1);  // BFF also has InventoryLevelsPerspective
      await fixture.Dispatcher.SendAsync(command);
      await productWaiter.WaitAsync(timeoutMilliseconds: 15000);
      await restockWaiter.WaitAsync(timeoutMilliseconds: 15000);
    }

    // Assert - Verify all products materialized in InventoryWorker perspective
    foreach (var command in commands) {
      var product = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId.Value);
      await Assert.That(product).IsNotNull();
      await Assert.That(product!.Name).IsEqualTo(command.Name);
      await Assert.That(product.Price).IsEqualTo(command.Price);

      var inventory = await fixture.InventoryLens.GetByProductIdAsync(command.ProductId.Value);
      await Assert.That(inventory).IsNotNull();
      await Assert.That(inventory!.Quantity).IsEqualTo(command.InitialStock);
    }

    // Assert - Verify all products materialized in BFF perspective
    foreach (var command in commands) {
      var product = await fixture.BffProductLens.GetByIdAsync(command.ProductId.Value);
      await Assert.That(product).IsNotNull();
      await Assert.That(product!.Name).IsEqualTo(command.Name);

      var inventory = await fixture.BffInventoryLens.GetByProductIdAsync(command.ProductId.Value);
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

    // Act - Create waiter BEFORE sending command to avoid race condition
    // NOTE: Only wait for ProductCreatedEvent since InitialStock = 0 won't trigger InventoryRestockedEvent
    using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(command);
    await productWaiter.WaitAsync(timeoutMilliseconds: 15000);

    // Assert - Verify product exists with zero inventory
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(command.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(0);
    await Assert.That(inventoryLevel.Available).IsEqualTo(0);

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(command.ProductId.Value);
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


    var command = new CreateProductCommand {
      ProductId = _testProdNoImage,
      Name = "No Image Product",
      Description = "Product without an image",
      Price = 19.99m,
      ImageUrl = null,
      InitialStock = 25
    };

    // Act - Create waiter BEFORE sending command to avoid race condition
    using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(command);
    await productWaiter.WaitAsync(timeoutMilliseconds: 15000);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 15000);

    // Assert - Verify product exists with null ImageUrl
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.ImageUrl).IsNull();

    var bffProduct = await fixture.BffProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.ImageUrl).IsNull();
  }
}
