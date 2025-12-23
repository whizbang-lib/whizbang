using System.Text.Json.Serialization;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace ECommerce.Contracts;

/// <summary>
/// JSON source generation context for ECommerce message types.
/// Provides AOT-compatible serialization for all commands, events, and value objects.
/// Call <see cref="Register"/> during application startup to register this context with the global registry.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
// Commands
[JsonSerializable(typeof(AdjustInventoryCommand))]
[JsonSerializable(typeof(CreateOrderCommand))]
[JsonSerializable(typeof(CreateProductCommand))]
[JsonSerializable(typeof(CreateShipmentCommand))]
[JsonSerializable(typeof(DeleteProductCommand))]
[JsonSerializable(typeof(ProcessPaymentCommand))]
[JsonSerializable(typeof(ReserveInventoryCommand))]
[JsonSerializable(typeof(RestockInventoryCommand))]
[JsonSerializable(typeof(SendNotificationCommand))]
[JsonSerializable(typeof(UpdateProductCommand))]
// Events
[JsonSerializable(typeof(InventoryAdjustedEvent))]
[JsonSerializable(typeof(InventoryReleasedEvent))]
[JsonSerializable(typeof(InventoryReservedEvent))]
[JsonSerializable(typeof(InventoryRestockedEvent))]
[JsonSerializable(typeof(NotificationSentEvent))]
[JsonSerializable(typeof(OrderCreatedEvent))]
[JsonSerializable(typeof(PaymentFailedEvent))]
[JsonSerializable(typeof(PaymentProcessedEvent))]
[JsonSerializable(typeof(ProductCreatedEvent))]
[JsonSerializable(typeof(ProductDeletedEvent))]
[JsonSerializable(typeof(ProductUpdatedEvent))]
[JsonSerializable(typeof(ShipmentCreatedEvent))]
// Value Objects
[JsonSerializable(typeof(OrderLineItem))]
[JsonSerializable(typeof(CustomerId))]
[JsonSerializable(typeof(OrderId))]
[JsonSerializable(typeof(ProductId))]
// Message Envelopes
[JsonSerializable(typeof(MessageEnvelope<AdjustInventoryCommand>))]
[JsonSerializable(typeof(MessageEnvelope<CreateOrderCommand>))]
[JsonSerializable(typeof(MessageEnvelope<CreateProductCommand>))]
[JsonSerializable(typeof(MessageEnvelope<CreateShipmentCommand>))]
[JsonSerializable(typeof(MessageEnvelope<DeleteProductCommand>))]
[JsonSerializable(typeof(MessageEnvelope<ProcessPaymentCommand>))]
[JsonSerializable(typeof(MessageEnvelope<ReserveInventoryCommand>))]
[JsonSerializable(typeof(MessageEnvelope<RestockInventoryCommand>))]
[JsonSerializable(typeof(MessageEnvelope<SendNotificationCommand>))]
[JsonSerializable(typeof(MessageEnvelope<UpdateProductCommand>))]
[JsonSerializable(typeof(MessageEnvelope<InventoryAdjustedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<InventoryReleasedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<InventoryReservedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<InventoryRestockedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<NotificationSentEvent>))]
[JsonSerializable(typeof(MessageEnvelope<OrderCreatedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<PaymentFailedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<PaymentProcessedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<ProductCreatedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<ProductDeletedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<ProductUpdatedEvent>))]
[JsonSerializable(typeof(MessageEnvelope<ShipmentCreatedEvent>))]
// Collection types (for List<OrderLineItem>)
[JsonSerializable(typeof(List<OrderLineItem>))]
public partial class ECommerceJsonContext : JsonSerializerContext {
  /// <summary>
  /// Registers this JSON context with the global JsonContextRegistry.
  /// Call this method during application startup (in Program.cs) to enable serialization of ECommerce message types.
  /// </summary>
  /// <remarks>
  /// This must be called explicitly to avoid the CA2255 warning about ModuleInitializers in library code.
  /// Previously used ModuleInitializer, but that's discouraged for libraries as it gives consumers no control.
  /// </remarks>
  public static void Register() {
    JsonContextRegistry.RegisterContext(Default);
  }
}
