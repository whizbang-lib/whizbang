using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Medo;
using TUnit.Core;

namespace ECommerce.RabbitMQ.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the UpdateProduct workflow.
/// Tests the complete flow: Command → Receptor → Event Store → Perspectives.
/// Each test gets its own PostgreSQL databases + hosts. RabbitMQ container is shared via SharedRabbitMqFixtureSource.
/// Tests run sequentially for reliable timing.
/// </summary>
[Category("Integration")]
[Category("Workflow")]
[NotInParallel]
public class UpdateProductWorkflowTests {
  private static RabbitMqIntegrationFixture? _fixture;

  // Test product IDs (UUIDv7 for proper time-ordering and uniqueness across test runs)
  private static readonly ProductId _testProdUpdateName = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdUpdateAll = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdUpdatePrice = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdUpdateDescImg = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdMultiUpdate = ProductId.From(Uuid7.NewUuid7().ToGuid());
  private static readonly ProductId _testProdUpdateNoInventory = ProductId.From(Uuid7.NewUuid7().ToGuid());

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Initialize shared containers (first test only)
    await SharedRabbitMqFixtureSource.InitializeAsync();

    // Get separate database connections for each host (eliminates lock contention)
    var inventoryDbConnection = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();
    var bffDbConnection = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();

    // Create and initialize test fixture with separate databases
    _fixture = new RabbitMqIntegrationFixture(
      SharedRabbitMqFixtureSource.RabbitMqConnectionString,
      inventoryDbConnection,
      bffDbConnection,
      SharedRabbitMqFixtureSource.ManagementApiUri,
      testId: Guid.NewGuid().ToString("N")[..12]
    );
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }

  /// <summary>
  /// Tests that updating a product's name via IDispatcher results in:
  /// 1. ProductUpdatedEvent published to Event Store
  /// 2. InventoryWorker perspective updates product
  /// 3. BFF perspective updates product
  /// 4. Updated product is queryable via lenses
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task UpdateProduct_Name_UpdatesPerspectivesAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Create initial product
    var createCommand = new CreateProductCommand {
      ProductId = _testProdUpdateName,
      Name = "Original Name",
      Description = "Original description",
      Price = 50.00m,
      ImageUrl = "/images/original.png",
      InitialStock = 10
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Update product name
    var updateCommand = new UpdateProductCommand {
      ProductId = _testProdUpdateName,
      Name = "Updated Name",
      Description = null,
      Price = null,
      ImageUrl = null
    };
    using var updateWaiter = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(updateCommand);
    await updateWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Assert - Verify InventoryWorker perspective updated
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo("Updated Name");
    await Assert.That(inventoryProduct.Description).IsEqualTo("Original description"); // Unchanged
    await Assert.That(inventoryProduct.Price).IsEqualTo(50.00m); // Unchanged

    // Assert - Verify BFF perspective updated
    var bffProduct = await fixture.BffProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Name).IsEqualTo("Updated Name");
    await Assert.That(bffProduct.Description).IsEqualTo("Original description"); // Unchanged
  }

  /// <summary>
  /// Tests that updating all product fields works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task UpdateProduct_AllFields_UpdatesPerspectivesAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Create initial product
    var createCommand = new CreateProductCommand {
      ProductId = _testProdUpdateAll,
      Name = "Original Product",
      Description = "Original description",
      Price = 100.00m,
      ImageUrl = "/images/original.png",
      InitialStock = 20
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Update all fields
    var updateCommand = new UpdateProductCommand {
      ProductId = _testProdUpdateAll,
      Name = "Completely Updated Product",
      Description = "Brand new description",
      Price = 149.99m,
      ImageUrl = "/images/updated.png"
    };
    using var updateWaiter = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(updateCommand);
    await updateWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Assert - Verify InventoryWorker perspective fully updated
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo("Completely Updated Product");
    await Assert.That(inventoryProduct.Description).IsEqualTo("Brand new description");
    await Assert.That(inventoryProduct.Price).IsEqualTo(149.99m);
    await Assert.That(inventoryProduct.ImageUrl).IsEqualTo("/images/updated.png");

    // Assert - Verify BFF perspective fully updated
    var bffProduct = await fixture.BffProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Name).IsEqualTo("Completely Updated Product");
    await Assert.That(bffProduct.Description).IsEqualTo("Brand new description");
    await Assert.That(bffProduct.Price).IsEqualTo(149.99m);
    await Assert.That(bffProduct.ImageUrl).IsEqualTo("/images/updated.png");
  }

  /// <summary>
  /// Tests that updating only the price works correctly (partial update).
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task UpdateProduct_PriceOnly_UpdatesOnlyPriceAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Create initial product
    var createCommand = new CreateProductCommand {
      ProductId = _testProdUpdatePrice,
      Name = "Price Test Product",
      Description = "Testing price updates",
      Price = 25.00m,
      ImageUrl = "/images/price-test.png",
      InitialStock = 15
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Update only price
    var updateCommand = new UpdateProductCommand {
      ProductId = _testProdUpdatePrice,
      Name = null,
      Description = null,
      Price = 35.00m,
      ImageUrl = null
    };
    using var updateWaiter = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(updateCommand);
    await updateWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Assert - Verify only price changed
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Price).IsEqualTo(35.00m); // Updated
    await Assert.That(inventoryProduct.Name).IsEqualTo("Price Test Product"); // Unchanged
    await Assert.That(inventoryProduct.Description).IsEqualTo("Testing price updates"); // Unchanged
    await Assert.That(inventoryProduct.ImageUrl).IsEqualTo("/images/price-test.png"); // Unchanged

    var bffProduct = await fixture.BffProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Price).IsEqualTo(35.00m); // Updated
  }

  /// <summary>
  /// Tests that updating product description and image URL works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task UpdateProduct_DescriptionAndImage_UpdatesBothFieldsAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Create initial product
    var createCommand = new CreateProductCommand {
      ProductId = _testProdUpdateDescImg,
      Name = "Descriptive Product",
      Description = "Old description",
      Price = 60.00m,
      ImageUrl = "/images/old.png",
      InitialStock = 30
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Update description and image
    var updateCommand = new UpdateProductCommand {
      ProductId = _testProdUpdateDescImg,
      Name = null,
      Description = "Completely new and improved description",
      Price = null,
      ImageUrl = "/images/new-and-improved.png"
    };
    using var updateWaiter = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(updateCommand);
    await updateWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Assert - Verify description and image updated
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Description).IsEqualTo("Completely new and improved description");
    await Assert.That(inventoryProduct.ImageUrl).IsEqualTo("/images/new-and-improved.png");
    await Assert.That(inventoryProduct.Name).IsEqualTo("Descriptive Product"); // Unchanged
    await Assert.That(inventoryProduct.Price).IsEqualTo(60.00m); // Unchanged

    var bffProduct = await fixture.BffProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Description).IsEqualTo("Completely new and improved description");
    await Assert.That(bffProduct.ImageUrl).IsEqualTo("/images/new-and-improved.png");
  }

  /// <summary>
  /// Tests that multiple sequential updates accumulate correctly.
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task UpdateProduct_MultipleSequentialUpdates_AccumulatesChangesAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Create initial product
    var createCommand = new CreateProductCommand {
      ProductId = _testProdMultiUpdate,
      Name = "Multi-Update Product",
      Description = "Original",
      Price = 10.00m,
      ImageUrl = "/images/v1.png",
      InitialStock = 5
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Update name
    var update1 = new UpdateProductCommand {
      ProductId = _testProdMultiUpdate,
      Name = "Updated Name",
      Description = null,
      Price = null,
      ImageUrl = null
    };
    using var updateWaiter1 = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(update1);
    await updateWaiter1.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Update price
    var update2 = new UpdateProductCommand {
      ProductId = _testProdMultiUpdate,
      Name = null,
      Description = null,
      Price = 20.00m,
      ImageUrl = null
    };
    using var updateWaiter2 = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(update2);
    await updateWaiter2.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Update description and image
    var update3 = new UpdateProductCommand {
      ProductId = _testProdMultiUpdate,
      Name = null,
      Description = "Final description",
      Price = null,
      ImageUrl = "/images/v3.png"
    };
    using var updateWaiter3 = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(update3);
    await updateWaiter3.WaitAsync(timeoutMilliseconds: 45000);

    // Assert - Verify all changes accumulated
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo("Updated Name"); // From update1
    await Assert.That(inventoryProduct.Price).IsEqualTo(20.00m); // From update2
    await Assert.That(inventoryProduct.Description).IsEqualTo("Final description"); // From update3
    await Assert.That(inventoryProduct.ImageUrl).IsEqualTo("/images/v3.png"); // From update3

    var bffProduct = await fixture.BffProductLens.GetByIdAsync(createCommand.ProductId);
    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Name).IsEqualTo("Updated Name");
    await Assert.That(bffProduct.Price).IsEqualTo(20.00m);
    await Assert.That(bffProduct.Description).IsEqualTo("Final description");
    await Assert.That(bffProduct.ImageUrl).IsEqualTo("/images/v3.png");
  }

  /// <summary>
  /// Tests that updating a product does NOT affect its inventory levels.
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task UpdateProduct_DoesNotAffectInventoryAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Create product with initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdUpdateNoInventory,
      Name = "Inventory Isolation Test",
      Description = "Testing inventory isolation",
      Price = 40.00m,
      ImageUrl = "/images/isolation.png",
      InitialStock = 75
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Verify initial inventory
    var initialInventory = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(initialInventory).IsNotNull();
    await Assert.That(initialInventory!.Quantity).IsEqualTo(75);

    // Act - Update product (all fields)
    var updateCommand = new UpdateProductCommand {
      ProductId = _testProdUpdateNoInventory,
      Name = "Updated Product Name",
      Description = "Updated description",
      Price = 50.00m,
      ImageUrl = "/images/updated-isolation.png"
    };
    using var updateWaiter = fixture.CreatePerspectiveWaiter<ProductUpdatedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);
    await fixture.Dispatcher.SendAsync(updateCommand);
    await updateWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Assert - Verify inventory unchanged
    var updatedInventory = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(updatedInventory).IsNotNull();
    await Assert.That(updatedInventory!.Quantity).IsEqualTo(75); // No change
    await Assert.That(updatedInventory.Available).IsEqualTo(75); // No change

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(75); // No change
  }
}
