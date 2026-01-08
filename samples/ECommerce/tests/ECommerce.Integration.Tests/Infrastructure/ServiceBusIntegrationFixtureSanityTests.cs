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
  /// Sanity Test 6: Verify events are stored with CORRECT DATA in event store.
  /// This tests that when we send InitialStock=15, the event in wh_event_store contains 15.
  /// </summary>
  [Test]
  [Timeout(30_000)]
  public async Task EventStore_ContainsCorrectEventDataAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var testProductId = ProductId.From(Uuid7.NewUuid7().ToGuid());
    var expectedStock = 42;  // Use distinctive value to find in logs

    var command = new CreateProductCommand {
      ProductId = testProductId,
      Name = "Event Data Test",
      Description = "Testing event data correctness",
      Price = 99.99m,
      ImageUrl = "/images/data-test.png",
      InitialStock = expectedStock
    };

    // Act - Send command and wait for processing
    Console.WriteLine($"[SANITY-DATA] Sending command with InitialStock={expectedStock}");
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Check InventoryRestockedEvent in event store has correct data
    await using var connection = new NpgsqlConnection(fixture.ConnectionString);
    await connection.OpenAsync();

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = @"
      SELECT event_data::text
      FROM inventory.wh_event_store
      WHERE stream_id = @streamId
        AND event_type = 'ECommerce.Contracts.Events.InventoryRestockedEvent, ECommerce.Contracts'
      ORDER BY version DESC
      LIMIT 1";
    cmd.Parameters.AddWithValue("streamId", testProductId.Value);

    var eventDataJson = await cmd.ExecuteScalarAsync() as string;
    Console.WriteLine($"[SANITY-DATA] Event JSON from wh_event_store: {eventDataJson}");

    await Assert.That(eventDataJson).IsNotNull();

    // Verify event contains correct QuantityAdded value
    if (!eventDataJson!.Contains($"\"QuantityAdded\":{expectedStock}")) {
      Console.WriteLine($"[SANITY-DATA] ❌ Event missing QuantityAdded={expectedStock}");
    }
    await Assert.That(eventDataJson.Contains($"\"QuantityAdded\":{expectedStock}")).IsTrue();

    // Verify event contains correct NewTotalQuantity value
    if (!eventDataJson.Contains($"\"NewTotalQuantity\":{expectedStock}")) {
      Console.WriteLine($"[SANITY-DATA] ❌ Event missing NewTotalQuantity={expectedStock}");
    }
    await Assert.That(eventDataJson.Contains($"\"NewTotalQuantity\":{expectedStock}")).IsTrue();

    Console.WriteLine($"[SANITY-DATA] ✅ Event stored with correct data (QuantityAdded={expectedStock})");
  }

  /// <summary>
  /// Sanity Test 7: Verify perspective DTO has CORRECT DATA after materialization.
  /// This tests the END-TO-END data flow from command to materialized perspective.
  /// </summary>
  [Test]
  [Timeout(30_000)]
  public async Task Perspective_ContainsCorrectDataAfterMaterializationAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");
    var testProductId = ProductId.From(Uuid7.NewUuid7().ToGuid());
    var expectedStock = 88;  // Distinctive value
    var expectedPrice = 123.45m;

    var command = new CreateProductCommand {
      ProductId = testProductId,
      Name = "Data Propagation Test",
      Description = "Testing data propagation to perspectives",
      Price = expectedPrice,
      ImageUrl = "/images/propagation-test.png",
      InitialStock = expectedStock
    };

    // Act
    Console.WriteLine($"[SANITY-PROPAGATION] Sending command: Stock={expectedStock}, Price={expectedPrice}");
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForEventProcessingAsync();

    // Assert - Check InventoryWorker perspective has correct data
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(testProductId);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo("Data Propagation Test");
    await Assert.That(inventoryProduct.Price).IsEqualTo(expectedPrice);

    var inventoryLevel = await fixture.InventoryLens.GetByProductIdAsync(testProductId);
    await Assert.That(inventoryLevel).IsNotNull();

    Console.WriteLine($"[SANITY-PROPAGATION] InventoryWorker perspective: Quantity={inventoryLevel!.Quantity} (expected {expectedStock})");

    if (inventoryLevel.Quantity != expectedStock) {
      Console.WriteLine($"[SANITY-PROPAGATION] ❌ FOUND THE BUG: Expected quantity {expectedStock}, got {inventoryLevel.Quantity}");

      // Dump event store data for diagnostics
      await using var connection = new NpgsqlConnection(fixture.ConnectionString);
      await connection.OpenAsync();

      // Diagnostic 0: Check event count
      await using var countCmd = connection.CreateCommand();
      countCmd.CommandText = @"
        SELECT COUNT(*)
        FROM inventory.wh_event_store
        WHERE stream_id = @streamId";
      countCmd.Parameters.AddWithValue("streamId", testProductId.Value);
      var eventCount = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
      Console.WriteLine($"[SANITY-PROPAGATION-DEBUG] Event store has {eventCount} events for stream {testProductId.Value}");

      // Diagnostic 1: Events in wh_event_store
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = @"
        SELECT version, event_type, event_data::text
        FROM inventory.wh_event_store
        WHERE stream_id = @streamId
        ORDER BY version";
      cmd.Parameters.AddWithValue("streamId", testProductId.Value);

      await using var reader = await cmd.ExecuteReaderAsync();
      Console.WriteLine($"[SANITY-PROPAGATION] Events in store for stream {testProductId}:");
      while (await reader.ReadAsync()) {
        var version = reader.GetInt32(0);
        var eventType = reader.GetString(1);
        var eventData = reader.GetString(2);
        Console.WriteLine($"  Event #{version}: {eventType}");
        Console.WriteLine($"    Data: {eventData}");
      }
      await reader.CloseAsync();

      // Diagnostic 2: Perspective work items created
      await using var cmd2 = connection.CreateCommand();
      cmd2.CommandText = @"
        SELECT
          pe.event_work_id,
          pe.perspective_name,
          pe.event_id,
          pe.sequence_number,
          pe.status,
          pe.processed_at,
          es.event_type
        FROM inventory.wh_perspective_events pe
        INNER JOIN inventory.wh_event_store es ON pe.event_id = es.event_id
        WHERE pe.stream_id = @streamId
        ORDER BY pe.sequence_number";
      cmd2.Parameters.AddWithValue("streamId", testProductId.Value);

      await using var reader2 = await cmd2.ExecuteReaderAsync();
      Console.WriteLine($"[SANITY-PROPAGATION] Perspective work items for stream {testProductId}:");
      while (await reader2.ReadAsync()) {
        var workId = reader2.GetGuid(0);
        var perspectiveName = reader2.GetString(1);
        var eventId = reader2.GetGuid(2);
        var sequenceNumber = reader2.GetInt64(3);
        var status = reader2.GetInt32(4);
        var processedAt = reader2.IsDBNull(5) ? "NULL" : reader2.GetDateTime(5).ToString("O");
        var eventType = reader2.GetString(6);
        Console.WriteLine($"  Work Item {workId}:");
        Console.WriteLine($"    Perspective: {perspectiveName}");
        Console.WriteLine($"    Event Type: {eventType}");
        Console.WriteLine($"    Sequence: {sequenceNumber}, Status: {status}, Processed: {processedAt}");
      }
      await reader2.CloseAsync();

      // Diagnostic 3: Message associations
      await using var cmd3 = connection.CreateCommand();
      cmd3.CommandText = @"
        SELECT message_type, association_type, target_name, service_name
        FROM inventory.wh_message_associations
        WHERE association_type = 'perspective'
        ORDER BY message_type, target_name";

      await using var reader3 = await cmd3.ExecuteReaderAsync();
      Console.WriteLine($"[SANITY-PROPAGATION] Perspective associations in inventory schema:");
      while (await reader3.ReadAsync()) {
        var messageType = reader3.GetString(0);
        var associationType = reader3.GetString(1);
        var targetName = reader3.GetString(2);
        var serviceName = reader3.GetString(3);
        Console.WriteLine($"  {messageType} -> {targetName} ({serviceName})");
      }
    }

    await Assert.That(inventoryLevel.Quantity).IsEqualTo(expectedStock);

    Console.WriteLine($"[SANITY-PROPAGATION] ✅ Perspective has correct data (Quantity={expectedStock}, Price={expectedPrice})");
  }

  /// <summary>
  /// Sanity Test 8: Verify Service Bus topics and subscriptions are configured correctly.
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
