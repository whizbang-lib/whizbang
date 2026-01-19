using ECommerce.Contracts.Events;
using Whizbang.Core.Messaging;

namespace ECommerce.Contracts;

/// <summary>
/// Provides all known event types in the ECommerce application for AOT-compatible polymorphic deserialization.
/// Required by PerspectiveWorker to load events when invoking lifecycle receptors.
/// </summary>
public class ECommerceEventTypeProvider : IEventTypeProvider {
  private static readonly IReadOnlyList<Type> _eventTypes = new[] {
    typeof(InventoryAdjustedEvent),
    typeof(InventoryReleasedEvent),
    typeof(InventoryReservedEvent),
    typeof(InventoryRestockedEvent),
    typeof(NotificationSentEvent),
    typeof(OrderCreatedEvent),
    typeof(PaymentFailedEvent),
    typeof(PaymentProcessedEvent),
    typeof(ProductCreatedEvent),
    typeof(ProductDeletedEvent),
    typeof(ProductUpdatedEvent),
    typeof(ShipmentCreatedEvent)
  };

  public IReadOnlyList<Type> GetEventTypes() => _eventTypes;
}
