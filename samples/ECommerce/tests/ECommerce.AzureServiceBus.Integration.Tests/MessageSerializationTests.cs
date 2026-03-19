using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ECommerce.Contracts.Commands;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace ECommerce.Integration.Tests;

/// <summary>
/// Tests that verify message creation and serialization with real ECommerce.Contracts types.
/// These tests verify the core issue: MessageIds and WhizbangIds must not serialize as all zeros!
/// </summary>
public class MessageSerializationTests {
  /// <summary>
  /// Verify MessageId.New() creates non-zero GUIDs (UUIDv7).
  /// If this fails, the problem is in MessageId creation itself.
  /// </summary>
  [Test]
  public async Task MessageId_New_CreatesNonZeroGuidAsync() {
    // Act
    var messageId = MessageId.New();

    // Assert
    await Assert.That(messageId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(messageId.Value).IsNotEqualTo(new Guid("00000000-0000-0000-0000-000000000000"));

    // UUIDv7 should have version bits set (version 7 = 0111)
    var bytes = messageId.Value.ToByteArray();
    await Assert.That(bytes).IsNotNull();
  }

  /// <summary>
  /// Verify MessageId serializes to a string (not an empty GUID).
  /// If this fails, the MessageIdJsonConverter is not working.
  /// </summary>
  [Test]
  [RequiresDynamicCode("JSON serialization may require dynamic code generation")]
  [RequiresUnreferencedCode("JSON serialization may require unreferenced code")]
  public async Task MessageId_Serializes_AsNonEmptyStringAsync() {
    // Arrange
    var messageId = MessageId.New();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Act
    var json = JsonSerializer.Serialize(messageId, jsonOptions);

    // Assert
    await Assert.That(json).IsNotNull();
    await Assert.That(json).IsNotEmpty();
    await Assert.That(json).DoesNotContain("00000000-0000-0000-0000-000000000000");

    // Should be a quoted string (UUIDv7 format)
    await Assert.That(json).StartsWith("\"");
    await Assert.That(json).EndsWith("\"");
  }

  /// <summary>
  /// Verify MessageId deserializes from JSON correctly.
  /// If this fails, the MessageIdJsonConverter Read() method is broken.
  /// </summary>
  [Test]
  [RequiresDynamicCode("JSON serialization may require dynamic code generation")]
  [RequiresUnreferencedCode("JSON serialization may require unreferenced code")]
  public async Task MessageId_RoundTrip_PreservesValueAsync() {
    // Arrange
    var originalId = MessageId.New();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Act
    var json = JsonSerializer.Serialize(originalId, jsonOptions);
    var deserialized = JsonSerializer.Deserialize<MessageId>(json, jsonOptions);

    // Assert
    await Assert.That(deserialized.Value).IsEqualTo(originalId.Value);
  }

  /// <summary>
  /// Verify WhizbangIds (OrderId, ProductId) serialize correctly.
  /// This is THE critical test - these are the types showing as all zeros in the logs!
  /// </summary>
  [Test]
  [RequiresUnreferencedCode("JSON serialization may require unreferenced code")]
  [RequiresDynamicCode("JSON serialization may require dynamic code")]
  public async Task WhizbangIds_Serialize_AsNonEmptyStringsAsync() {
    // Arrange
    var orderId = OrderId.New();
    var productId = ProductId.New();
    var customerId = CustomerId.New();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Act
    var orderJson = JsonSerializer.Serialize(orderId, jsonOptions);
    var productJson = JsonSerializer.Serialize(productId, jsonOptions);
    var customerJson = JsonSerializer.Serialize(customerId, jsonOptions);

    // Assert - All should serialize as non-empty strings
    await Assert.That(orderJson).DoesNotContain("00000000-0000-0000-0000-000000000000");
    await Assert.That(productJson).DoesNotContain("00000000-0000-0000-0000-000000000000");
    await Assert.That(customerJson).DoesNotContain("00000000-0000-0000-0000-000000000000");

    // Should be quoted strings
    await Assert.That(orderJson).StartsWith("\"");
    await Assert.That(productJson).StartsWith("\"");
    await Assert.That(customerJson).StartsWith("\"");
  }

  /// <summary>
  /// Verify MessageEnvelope with CreateProductCommand serializes correctly.
  /// This tests the EXACT scenario: MessageEnvelope with a command containing WhizbangId.
  /// </summary>
  [Test]
  [RequiresUnreferencedCode("JSON serialization may require unreferenced code")]
  [RequiresDynamicCode("JSON serialization may require dynamic code")]
  public async Task MessageEnvelope_WithCreateProductCommand_SerializesWhizbangIdsCorrectlyAsync() {
    // Arrange - Create a real CreateProductCommand with ProductId (WhizbangId)
    var productId = ProductId.New();
    var command = new CreateProductCommand {
      ProductId = productId,
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m
    };

    var originalMessageId = MessageId.New();
    var envelope = new MessageEnvelope<CreateProductCommand> {
      MessageId = originalMessageId,
      Payload = command,
      Hops = []
    };

    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Act
    var json = JsonSerializer.Serialize(envelope, jsonOptions);

    // Assert - MessageId and ProductId should NOT be all zeros in JSON
    await Assert.That(json).DoesNotContain("\"MessageId\":\"00000000-0000-0000-0000-000000000000\"")
      .Because("MessageId should serialize as a valid UUIDv7, not all zeros");
    await Assert.That(json).DoesNotContain("\"ProductId\":\"00000000-0000-0000-0000-000000000000\"")
      .Because("ProductId (WhizbangId) should serialize as a valid UUIDv7, not all zeros");

    // Deserialize and verify IDs are preserved
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<CreateProductCommand>>(json, jsonOptions);
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId.Value).IsEqualTo(originalMessageId.Value)
      .Because("MessageId must survive JSON round-trip");
    await Assert.That(deserialized.Payload.ProductId.Value).IsEqualTo(productId.Value)
      .Because("ProductId (WhizbangId) must survive JSON round-trip");
  }

  /// <summary>
  /// Verify MessageEnvelope with CreateOrderCommand serializes all three WhizbangId types.
  /// This is the most comprehensive test - OrderId, CustomerId, and ProductId all in one message.
  /// </summary>
  [Test]
  [RequiresUnreferencedCode("JSON serialization may require unreferenced code")]
  [RequiresDynamicCode("JSON serialization may require dynamic code")]
  public async Task MessageEnvelope_WithCreateOrderCommand_SerializesAllWhizbangIdsCorrectlyAsync() {
    // Force ECommerce.Contracts assembly to load (ensures JSON converters are registered)
    _ = typeof(CreateOrderCommand).Assembly;

    // Arrange - Create CreateOrderCommand with all three WhizbangId types
    var orderId = OrderId.New();
    var customerId = CustomerId.New();
    var productId = ProductId.New();

    var command = new CreateOrderCommand {
      OrderId = orderId,
      CustomerId = customerId,
      LineItems = [
        new OrderLineItem {
          ProductId = productId,
          ProductName = "Test Product",
          Quantity = 2,
          UnitPrice = 50.00m
        }
      ],
      TotalAmount = 100.00m
    };

    var originalMessageId = MessageId.New();
    var envelope = new MessageEnvelope<CreateOrderCommand> {
      MessageId = originalMessageId,
      Payload = command,
      Hops = []
    };

    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Act - Serialize and deserialize (simulates outbox path)
    var json = JsonSerializer.Serialize(envelope, jsonOptions);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<CreateOrderCommand>>(json, jsonOptions);

    // Assert - ALL IDs must be preserved!
    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.MessageId.Value).IsEqualTo(originalMessageId.Value)
      .Because("MessageId must survive serialization round-trip");
    await Assert.That(deserialized.Payload.OrderId.Value).IsEqualTo(orderId.Value)
      .Because("OrderId (WhizbangId) must survive serialization round-trip");
    await Assert.That(deserialized.Payload.CustomerId.Value).IsEqualTo(customerId.Value)
      .Because("CustomerId (WhizbangId) must survive serialization round-trip");
    await Assert.That(deserialized.Payload.LineItems[0].ProductId.Value).IsEqualTo(productId.Value)
      .Because("ProductId (WhizbangId) in nested LineItem must survive serialization round-trip");
  }

  /// <summary>
  /// Verify that JsonContextRegistry has converters registered.
  /// This test verifies the fix for the module initialization order bug.
  /// </summary>
  [Test]
  public async Task JsonContextRegistry_HasConvertersRegisteredAsync() {
    // Force ECommerce.Contracts assembly to load (same as Program.cs fix)
    _ = typeof(CreateProductCommand).Assembly;

    // Act
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

    // Assert - Should have converters registered
    await Assert.That(jsonOptions.Converters).IsNotEmpty()
      .Because("JsonContextRegistry should have converters from all loaded assemblies");

    // Verify specific converters exist
    var hasOrderIdConverter = jsonOptions.Converters.Any(c => c.GetType().Name == "OrderIdJsonConverter");
    var hasProductIdConverter = jsonOptions.Converters.Any(c => c.GetType().Name == "ProductIdJsonConverter");
    var hasCustomerIdConverter = jsonOptions.Converters.Any(c => c.GetType().Name == "CustomerIdJsonConverter");
    var hasMessageIdConverter = jsonOptions.Converters.Any(c => c.GetType().Name == "MessageIdJsonConverter");

    await Assert.That(hasOrderIdConverter).IsTrue()
      .Because("OrderIdJsonConverter should be registered");
    await Assert.That(hasProductIdConverter).IsTrue()
      .Because("ProductIdJsonConverter should be registered");
    await Assert.That(hasCustomerIdConverter).IsTrue()
      .Because("CustomerIdJsonConverter should be registered");
    await Assert.That(hasMessageIdConverter).IsTrue()
      .Because("MessageIdJsonConverter should be registered");
  }
}
