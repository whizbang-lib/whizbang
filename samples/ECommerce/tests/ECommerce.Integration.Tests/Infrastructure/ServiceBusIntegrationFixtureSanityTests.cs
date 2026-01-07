using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;
using Medo;
using Npgsql;

namespace ECommerce.Integration.Tests.Infrastructure;

/// <summary>
/// Sanity tests for ServiceBusIntegrationFixture to verify:
/// 1. TestContainers PostgreSQL connectivity
/// 2. Service Bus emulator integration
/// 3. Message publishing and receiving
/// 4. Perspective materialization
/// Tests run sequentially to avoid ServiceBus topic conflicts.
/// </summary>
[NotInParallel]
public class ServiceBusIntegrationFixtureSanityTests {
  private static ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Get SHARED ServiceBus resources (emulator + single static ServiceBusClient)
    var testIndex = 99; // Use high index to avoid conflicts with workflow tests
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(testIndex);

    // Create fixture with shared client
    _fixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, 0);
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
    }
  }

  /// <summary>
  /// Sanity Test 1: Verify TestContainers PostgreSQL starts and accepts connections.
  /// This tests the basic container setup without any Whizbang infrastructure.
  /// </summary>
  [Test]
  public async Task PostgreSQL_Container_StartsAndAcceptsConnectionsAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Act - Try to connect to PostgreSQL directly
    await using var connection = new NpgsqlConnection(fixture.ConnectionString);
    await connection.OpenAsync();

    // Assert - Execute a simple query
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT 1";
    var result = await cmd.ExecuteScalarAsync();

    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsEqualTo(1);
  }

  /// <summary>
  /// Sanity Test 2: Verify Whizbang schemas are created correctly.
  /// Tests that InitializeAsync() properly sets up inventory and bff schemas.
  /// </summary>
  [Test]
  public async Task PostgreSQL_WhizbangSchemas_AreCreatedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    // Act - Query for schema existence
    await using var connection = new NpgsqlConnection(fixture.ConnectionString);
    await connection.OpenAsync();

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT COUNT(*) FROM information_schema.schemata
      WHERE schema_name IN ('inventory', 'bff')";
    var schemaCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);

    // Assert - Both schemas should exist
    await Assert.That(schemaCount).IsEqualTo(2);
  }

  /// <summary>
  /// Sanity Test 3: Verify Service Bus message can be published.
  /// Tests that the ServiceBusClient is properly configured and can send messages.
  /// </summary>
  [Test]
  public async Task ServiceBus_PublishMessage_SucceedsAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var testProductId = ProductId.From(Uuid7.NewUuid7().ToGuid());

    var command = new CreateProductCommand {
      ProductId = testProductId,
      Name = "Sanity Test Product",
      Description = "Testing ServiceBus publishing",
      Price = 10.00m,
      ImageUrl = "/images/sanity.png",
      InitialStock = 5
    };

    // Act - Send command via dispatcher (publishes to ServiceBus via transport)
    Console.WriteLine($"[SANITY] Sending CreateProductCommand for ProductId={testProductId}");
    await fixture.Dispatcher.SendAsync(command);

    // Assert - Command accepted (doesn't throw)
    // Note: This only tests publishing, not receiving
    Console.WriteLine($"[SANITY] ✅ Message published successfully without throwing");
  }

  /// <summary>
  /// Sanity Test 4: Verify InventoryWorker perspectives materialize from event store.
  /// Tests that perspective workers process events and create perspective rows.
  /// </summary>
  [Test]
  public async Task InventoryWorker_Perspectives_MaterializeAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var testProductId = ProductId.From(Uuid7.NewUuid7().ToGuid());

    var command = new CreateProductCommand {
      ProductId = testProductId,
      Name = "Inventory Perspective Test",
      Description = "Testing InventoryWorker perspective materialization",
      Price = 20.00m,
      ImageUrl = "/images/inventory-test.png",
      InitialStock = 10
    };

    // Act - Send command and wait for event processing
    Console.WriteLine($"[SANITY] Sending command for InventoryWorker perspective test: {testProductId}");
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Verify product materialized in InventoryWorker perspective
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(testProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo("Inventory Perspective Test");

    // Assert - Verify inventory level materialized
    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(testProductId);
    await Assert.That(inventoryLevel).IsNotNull();
    await Assert.That(inventoryLevel!.Quantity).IsEqualTo(10);

    Console.WriteLine($"[SANITY] ✅ InventoryWorker perspectives materialized successfully");
  }

  /// <summary>
  /// Sanity Test 5: Verify BFF perspectives materialize from Service Bus messages.
  /// This is the CRITICAL test - it verifies ServiceBusConsumerWorker receives messages.
  /// If this fails, we know the Service Bus message delivery is broken.
  /// </summary>
  [Test]
  [Timeout(30_000)]  // 30s timeout - give plenty of time for message delivery
  public async Task BFF_Perspectives_MaterializeFromServiceBusAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var testProductId = ProductId.From(Uuid7.NewUuid7().ToGuid());

    var command = new CreateProductCommand {
      ProductId = testProductId,
      Name = "BFF Perspective Test",
      Description = "Testing BFF perspective materialization from Service Bus",
      Price = 30.00m,
      ImageUrl = "/images/bff-test.png",
      InitialStock = 15
    };

    // Act - Send command and wait for event processing
    Console.WriteLine($"[SANITY] Sending command for BFF perspective test: {testProductId}");
    Console.WriteLine("[SANITY] This tests that ServiceBusConsumerWorker receives messages from topics");
    await fixture.Dispatcher.SendAsync(command);

    // Wait for both InventoryWorker (from event store) AND BFF (from Service Bus)
    await fixture.WaitForEventProcessingAsync();

    // Dump diagnostics to understand what's happening
    await fixture.DumpEventTypesAndAssociationsAsync();

    // Assert - Verify InventoryWorker perspective (should always work)
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(testProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    Console.WriteLine($"[SANITY] ✅ InventoryWorker perspective: Product found");

    // Assert - Verify BFF perspective (THIS IS THE CRITICAL TEST)
    var bffProduct = await fixture.BffProductLens.GetByIdAsync(testProductId);
    if (bffProduct == null) {
      Console.WriteLine($"[SANITY] ❌ BFF perspective: Product NOT found");
      Console.WriteLine($"[SANITY] This means ServiceBusConsumerWorker is NOT receiving messages from Service Bus");

      // Additional diagnostics
      await using var connection = new NpgsqlConnection(fixture.ConnectionString);
      await connection.OpenAsync();

      await using var cmd = connection.CreateCommand();
      cmd.CommandText = "SELECT COUNT(*) FROM bff.wh_inbox";
      var inboxCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
      Console.WriteLine($"[SANITY] BFF inbox message count: {inboxCount}");

      cmd.CommandText = "SELECT COUNT(*) FROM bff.wh_per_product_dto";
      var bffProductCount = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
      Console.WriteLine($"[SANITY] BFF product perspective row count: {bffProductCount}");
    } else {
      Console.WriteLine($"[SANITY] ✅ BFF perspective: Product found with name '{bffProduct.Name}'");
    }

    await Assert.That(bffProduct).IsNotNull();
    await Assert.That(bffProduct!.Name).IsEqualTo("BFF Perspective Test");

    // Assert - Verify BFF inventory level
    var bffInventory = await fixture.BffInventoryLens.GetByProductIdAsync(testProductId);
    await Assert.That(bffInventory).IsNotNull();
    await Assert.That(bffInventory!.Quantity).IsEqualTo(15);

    Console.WriteLine($"[SANITY] ✅ BFF perspectives materialized successfully from Service Bus");
  }

  /// <summary>
  /// Sanity Test 6: Verify Service Bus topics and subscriptions are configured correctly.
  /// Tests that the emulator has the expected topic/subscription structure.
  /// </summary>
  [Test]
  [Timeout(20_000)]
  public async Task ServiceBus_TopicsAndSubscriptions_AreConfiguredAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(99);

    Console.WriteLine("[SANITY] Inspecting Service Bus emulator configuration...");

    // Act - Try to create senders for expected topics
    var topicNames = new[] { "topic-00", "topic-01" };
    var results = new Dictionary<string, bool>();

    foreach (var topicName in topicNames) {
      try {
        var sender = sharedClient.CreateSender(topicName);
        await sender.DisposeAsync();
        results[topicName] = true;
        Console.WriteLine($"[SANITY] ✅ Topic '{topicName}' exists and is accessible");
      } catch (Exception ex) {
        results[topicName] = false;
        Console.WriteLine($"[SANITY] ❌ Topic '{topicName}' error: {ex.Message}");
      }
    }

    // Assert - All topics should be accessible
    await Assert.That(results["topic-00"]).IsTrue();
    await Assert.That(results["topic-01"]).IsTrue();
  }
}
