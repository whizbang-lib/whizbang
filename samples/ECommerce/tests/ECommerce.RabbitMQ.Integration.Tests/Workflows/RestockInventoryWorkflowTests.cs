using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Medo;
using TUnit.Core;

namespace ECommerce.RabbitMQ.Integration.Tests.Workflows;

/// <summary>
/// End-to-end integration tests for the RestockInventory workflow.
/// Tests the complete flow: Command → Receptor → Event Store → Perspectives.
/// Each test gets its own PostgreSQL databases + hosts. RabbitMQ container is shared via SharedRabbitMqFixtureSource.
/// Tests run sequentially for reliable timing.
/// </summary>
[Category("Integration")]
[Category("Workflow")]
[NotInParallel("RabbitMQ")]
public class RestockInventoryWorkflowTests {
  private static RabbitMqIntegrationFixture? _fixture;

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
  /// Tests that restocking inventory via IDispatcher results in:
  /// 1. InventoryRestockedEvent published to Event Store
  /// 2. InventoryWorker perspective updates inventory levels
  /// 3. BFF perspective updates inventory levels
  /// 4. Updated inventory is queryable via lenses
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task RestockInventory_PublishesEvent_UpdatesPerspectivesAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange

    // First, create a product with initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdRestock1,
      Name = "Restockable Product",
      Description = "Product that will be restocked",
      Price = 50.00m,
      ImageUrl = "/images/restock.png",
      InitialStock = 10
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Restock inventory
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdRestock1,
      QuantityToAdd = 50
    };
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);  // BFF also has InventoryLevelsPerspective that handles this event
    await fixture.Dispatcher.SendAsync(restockCommand);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 45000);


    // Refresh lens scopes to get fresh DbContexts that can see committed perspective data
    fixture.RefreshLensScopes();

    // Assert - Verify InventoryWorker perspective updated
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(60); // 10 + 50
    await Assert.That(inventoryLevel.Available).IsEqualTo(60);

    // Assert - Verify BFF perspective updated
    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(60); // 10 + 50
  }

  /// <summary>
  /// Tests that multiple restock operations accumulate correctly.
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task RestockInventory_MultipleRestocks_AccumulatesCorrectlyAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange

    // Create product with initial stock of 5
    var createCommand = new CreateProductCommand {
      ProductId = _testProdMultiRestock,
      Name = "Multi-Restock Product",
      Description = "Product restocked multiple times",
      Price = 25.00m,
      ImageUrl = "/images/multi-restock.png",
      InitialStock = 5
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Perform multiple restock operations
    // Wait between each restock to ensure events are processed and perspectives are updated
    var restockCommands = new[] {
      new RestockInventoryCommand { ProductId = _testProdMultiRestock, QuantityToAdd = 10 },
      new RestockInventoryCommand { ProductId = _testProdMultiRestock, QuantityToAdd = 20 },
      new RestockInventoryCommand { ProductId = _testProdMultiRestock, QuantityToAdd = 15 }
    };

    foreach (var restockCommand in restockCommands) {
      using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
        inventoryPerspectives: 1,
        bffPerspectives: 1);  // BFF also has InventoryLevelsPerspective that handles this event
      await fixture.Dispatcher.SendAsync(restockCommand);
      await restockWaiter.WaitAsync(timeoutMilliseconds: 45000);
    }


    // Refresh lens scopes to get fresh DbContexts that can see committed perspective data
    fixture.RefreshLensScopes();

    // Assert - Verify total quantity = 5 + 10 + 20 + 15 = 50
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(50);
    await Assert.That(inventoryLevel.Available).IsEqualTo(50);

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(50);
  }

  /// <summary>
  /// Tests that restocking from zero inventory works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task RestockInventory_FromZeroStock_IncreasesCorrectlyAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange

    // Create product with zero initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdRestockZero,
      Name = "Restock from Zero",
      Description = "Product starting with no inventory",
      Price = 75.00m,
      ImageUrl = "/images/restock-zero.png",
      InitialStock = 0
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Restock from zero
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdRestockZero,
      QuantityToAdd = 100
    };
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);  // BFF also has InventoryLevelsPerspective that handles this event
    await fixture.Dispatcher.SendAsync(restockCommand);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 45000);


    // Refresh lens scopes to get fresh DbContexts that can see committed perspective data
    fixture.RefreshLensScopes();

    // Assert - Verify quantity increased from 0 to 100
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(100);

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(100);
  }

  /// <summary>
  /// Tests that restocking with zero quantity is handled correctly (edge case).
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task RestockInventory_ZeroQuantity_NoChangeAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange

    // Create product with initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdRestockZeroQty,
      Name = "Zero Quantity Restock",
      Description = "Testing zero quantity restock",
      Price = 30.00m,
      ImageUrl = "/images/zero-qty.png",
      InitialStock = 25
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Restock with zero quantity
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdRestockZeroQty,
      QuantityToAdd = 0
    };
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);  // BFF also has InventoryLevelsPerspective that handles this event
    await fixture.Dispatcher.SendAsync(restockCommand);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 45000);


    // Refresh lens scopes to get fresh DbContexts that can see committed perspective data
    fixture.RefreshLensScopes();

    // Assert - Verify quantity unchanged
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(25); // No change

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(25); // No change
  }

  /// <summary>
  /// Tests that restocking large quantities works correctly.
  /// </summary>
  [Test]
  [Timeout(60000)] // 60 seconds: container init (~15s) + perspective processing (45s)
  public async Task RestockInventory_LargeQuantity_HandlesCorrectlyAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    // Arrange

    // Create product with small initial stock
    var createCommand = new CreateProductCommand {
      ProductId = _testProdLargeRestock,
      Name = "Large Restock Product",
      Description = "Product with large inventory increase",
      Price = 100.00m,
      ImageUrl = "/images/large-restock.png",
      InitialStock = 50
    };
    using var createWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(createCommand);
    await createWaiter.WaitAsync(timeoutMilliseconds: 45000);

    // Act - Restock with large quantity
    var restockCommand = new RestockInventoryCommand {
      ProductId = _testProdLargeRestock,
      QuantityToAdd = 10000
    };
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 1,
      bffPerspectives: 1);  // BFF also has InventoryLevelsPerspective that handles this event
    await fixture.Dispatcher.SendAsync(restockCommand);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 45000);


    // Refresh lens scopes to get fresh DbContexts that can see committed perspective data
    fixture.RefreshLensScopes();

    // Assert - Verify large quantity handled
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(10050); // 50 + 10000

    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(createCommand.ProductId.Value);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(10050);
  }
}
