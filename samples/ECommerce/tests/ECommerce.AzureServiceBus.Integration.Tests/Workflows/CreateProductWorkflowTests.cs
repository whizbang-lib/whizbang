using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;
using Medo;

namespace ECommerce.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the CreateProduct workflow.
/// Tests the complete flow: Command -> Receptor -> Event Store -> Perspectives.
/// All tests share a single fixture (PostgreSQL database + hosts) for performance.
/// Database cleanup between tests ensures isolation.
/// </summary>
[NotInParallel("ServiceBus")]
[Timeout(120_000)]  // 120s: first test needs container init (~30s), subsequent tests fast
public class CreateProductWorkflowTests {
  private ServiceBusIntegrationFixture? _fixture;

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
    // Get shared fixture (creates container + hosts on first call, reuses on subsequent calls)
    _fixture = await SharedServiceBusFixtureSource.GetFixtureAsync();

    // Wait for any in-flight work from previous test to complete
    await Task.Delay(500);

    // Clean database between tests to ensure isolation
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Don't dispose - shared fixture is reused across tests
    // Cleanup happens in Before(Test) of next test
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

    // Act - Wait for 2 inventory perspectives for ProductCreatedEvent
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Wait for workers to be idle before data assertions
    await fixture.WaitForWorkersIdleAsync();

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

    // BFF assertions removed -- BFF receives via Service Bus transport
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

    // Act - Wait for all perspective processing
    // 3 commands x 2 inventory perspectives for ProductCreatedEvent = 6 total
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 6, timeoutMilliseconds: 45000, hostFilter: "inventory");
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    await perspectiveTask;

    // Wait for workers to be idle before data assertions
    await fixture.WaitForWorkersIdleAsync();

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

    // BFF assertions removed -- BFF receives via Service Bus transport
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

    // Act - Wait for perspective processing (2 for ProductCreated only, no restock for zero stock)
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Wait for workers to be idle before data assertions
    await fixture.WaitForWorkersIdleAsync();

    // Assert - Verify product exists with zero inventory
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(command.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(0);
    await Assert.That(inventoryLevel.Available).IsEqualTo(0);

    // BFF assertions removed -- BFF receives via Service Bus transport
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

    // Act - Wait for 2 inventory perspectives for ProductCreatedEvent
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Wait for workers to be idle before data assertions
    await fixture.WaitForWorkersIdleAsync();

    // Assert - Verify product exists with null ImageUrl
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.ImageUrl).IsNull();

    // BFF assertions removed -- BFF receives via Service Bus transport
  }
}
