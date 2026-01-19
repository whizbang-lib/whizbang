using Bogus;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;

namespace ECommerce.BFF.API.Tests.TestHelpers;

/// <summary>
/// Factory methods for creating test events with realistic data.
/// </summary>
public static class EventBuilder {
  private static readonly Faker _faker = new Faker();

  public static OrderCreatedEvent CreateOrderCreatedEvent(
    string? orderId = null,
    string? customerId = null,
    decimal? totalAmount = null,
    List<OrderLineItem>? lineItems = null,
    DateTime? createdAt = null) {
    var items = lineItems ?? [
      new OrderLineItem {
        ProductId = _faker.Random.Guid().ToString(),
        ProductName = _faker.Commerce.ProductName(),
        Quantity = _faker.Random.Number(1, 5),
        UnitPrice = _faker.Finance.Amount(5, 100, 2)
      }
    ];

    return new OrderCreatedEvent {
      OrderId = orderId ?? _faker.Random.Guid().ToString(),
      CustomerId = customerId ?? _faker.Random.Guid().ToString(),
      TotalAmount = totalAmount ?? items.Sum(i => i.Quantity * i.UnitPrice),
      LineItems = items,
      CreatedAt = createdAt ?? DateTime.UtcNow
    };
  }

  public static InventoryReservedEvent CreateInventoryReservedEvent(
    string? orderId = null,
    string? productId = null,
    int? quantity = null,
    DateTime? reservedAt = null) {
    return new InventoryReservedEvent {
      OrderId = orderId ?? _faker.Random.Guid().ToString(),
      ProductId = productId ?? _faker.Random.Guid().ToString(),
      Quantity = quantity ?? _faker.Random.Number(1, 10),
      ReservedAt = reservedAt ?? DateTime.UtcNow
    };
  }

  public static PaymentProcessedEvent CreatePaymentProcessedEvent(
    string? orderId = null,
    string? customerId = null,
    string? transactionId = null,
    decimal? amount = null) {
    return new PaymentProcessedEvent {
      OrderId = orderId ?? _faker.Random.Guid().ToString(),
      CustomerId = customerId ?? _faker.Random.Guid().ToString(),
      TransactionId = transactionId ?? _faker.Random.Guid().ToString(),
      Amount = amount ?? _faker.Finance.Amount(10, 1000, 2)
    };
  }

  public static PaymentFailedEvent CreatePaymentFailedEvent(
    string? orderId = null,
    string? customerId = null,
    string? reason = null) {
    return new PaymentFailedEvent {
      OrderId = orderId ?? _faker.Random.Guid().ToString(),
      CustomerId = customerId ?? _faker.Random.Guid().ToString(),
      Reason = reason ?? "Insufficient funds"
    };
  }
}
