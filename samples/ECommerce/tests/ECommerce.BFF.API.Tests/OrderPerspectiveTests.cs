using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using ECommerce.BFF.API.Perspectives;
using ECommerce.BFF.API.Tests.TestHelpers;
using ECommerce.Contracts.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace ECommerce.BFF.API.Tests;

/// <summary>
/// Integration tests for OrderPerspective using unified Whizbang API with EF Core.
/// Tests verify that the perspective correctly updates the BFF read model when OrderCreatedEvent is received.
/// </summary>
[RequiresUnreferencedCode("Test code - reflection allowed")]
[RequiresDynamicCode("Test code - reflection allowed")]
public class OrderPerspectiveTests : IAsyncDisposable {
  private readonly EFCoreTestHelper _helper = new();

  [Test]
  public async Task OrderPerspective_Update_WithOrderCreatedEvent_InsertsOrderRecordAsync() {
    // Arrange
    var hubContext = new MockHubContext<OrderStatusHub>();
    var perspective = new OrderPerspective(
      _helper.GetPerspectiveStore<OrderReadModel>(),
      _helper.GetLensQuery<OrderReadModel>(),
      hubContext,
      _helper.GetLogger<OrderPerspective>());

    var lens = _helper.GetLensQuery<OrderReadModel>();

    var orderId = OrderId.New();
    var customerId = CustomerId.New();
    var @event = EventBuilder.CreateOrderCreatedEvent(
      orderId: orderId.Value,
      customerId: customerId.Value,
      totalAmount: 99.99m
    );

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify order was inserted using lens query
    var order = await lens.GetByIdAsync(orderId.Value.ToString(), CancellationToken.None);

    await Assert.That(order).IsNotNull();
    await Assert.That(order!.OrderId).IsEqualTo(orderId.Value.ToString());
    await Assert.That(order.CustomerId).IsEqualTo(customerId.Value.ToString());
    await Assert.That(order.Status).IsEqualTo("Created");
    await Assert.That(order.TotalAmount).IsEqualTo(99.99m);
    await Assert.That(order.ItemCount).IsEqualTo(1);
  }

  [Test]
  public async Task OrderPerspective_Update_WithOrderCreatedEvent_StoresOrderCorrectlyAsync() {
    // Arrange
    var hubContext = new MockHubContext<OrderStatusHub>();
    var perspective = new OrderPerspective(
      _helper.GetPerspectiveStore<OrderReadModel>(),
      _helper.GetLensQuery<OrderReadModel>(),
      hubContext,
      _helper.GetLogger<OrderPerspective>());

    var lens = _helper.GetLensQuery<OrderReadModel>();

    var orderId = OrderId.New();
    var @event = EventBuilder.CreateOrderCreatedEvent(orderId: orderId.Value);

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify order was stored with correct status
    var order = await lens.GetByIdAsync(orderId.Value.ToString(), CancellationToken.None);

    await Assert.That(order).IsNotNull();
    await Assert.That(order!.OrderId).IsEqualTo(orderId.Value.ToString());
    await Assert.That(order.Status).IsEqualTo("Created");
  }

  [Test]
  public async Task OrderPerspective_Update_WithOrderCreatedEvent_SendsSignalRNotificationAsync() {
    // Arrange
    var hubContext = new MockHubContext<OrderStatusHub>();
    var perspective = new OrderPerspective(
      _helper.GetPerspectiveStore<OrderReadModel>(),
      _helper.GetLensQuery<OrderReadModel>(),
      hubContext,
      _helper.GetLogger<OrderPerspective>());

    var orderId = OrderId.New();
    var customerId = CustomerId.New();
    var @event = EventBuilder.CreateOrderCreatedEvent(
      orderId: orderId.Value,
      customerId: customerId.Value
    );

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify SignalR message was sent
    await Assert.That(hubContext.SentMessages).HasCount().EqualTo(1);

    var (method, args) = hubContext.SentMessages[0];
    await Assert.That(method).IsEqualTo("OrderStatusChanged");
    await Assert.That(args).HasCount().EqualTo(1);
  }

  [Test]
  public async Task OrderPerspective_Update_WithMultipleLineItems_CalculatesCorrectItemCountAsync() {
    // Arrange
    var hubContext = new MockHubContext<OrderStatusHub>();
    var perspective = new OrderPerspective(
      _helper.GetPerspectiveStore<OrderReadModel>(),
      _helper.GetLensQuery<OrderReadModel>(),
      hubContext,
      _helper.GetLogger<OrderPerspective>());

    var lens = _helper.GetLensQuery<OrderReadModel>();

    var lineItems = new List<ECommerce.Contracts.Commands.OrderLineItem> {
      new() { ProductId = ProductId.New(), ProductName = "Item 1", Quantity = 2, UnitPrice = 10.00m },
      new() { ProductId = ProductId.New(), ProductName = "Item 2", Quantity = 1, UnitPrice = 20.00m },
      new() { ProductId = ProductId.New(), ProductName = "Item 3", Quantity = 3, UnitPrice = 5.00m }
    };

    var orderId = OrderId.New();
    var @event = EventBuilder.CreateOrderCreatedEvent(
      orderId: orderId.Value,
      lineItems: lineItems
    );

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify item count is correct using lens query
    var order = await lens.GetByIdAsync(orderId.Value.ToString(), CancellationToken.None);

    await Assert.That(order).IsNotNull();
    await Assert.That(order!.ItemCount).IsEqualTo(3); // 3 line items
    await Assert.That(order.LineItems).HasCount().EqualTo(3);
  }

  [Test]
  public async Task OrderPerspective_Update_WithDuplicateOrderId_UpdatesRecordAsync() {
    // Arrange
    var hubContext = new MockHubContext<OrderStatusHub>();
    var perspective = new OrderPerspective(
      _helper.GetPerspectiveStore<OrderReadModel>(),
      _helper.GetLensQuery<OrderReadModel>(),
      hubContext,
      _helper.GetLogger<OrderPerspective>());

    var lens = _helper.GetLensQuery<OrderReadModel>();

    var orderId = OrderId.New();
    var firstEvent = EventBuilder.CreateOrderCreatedEvent(
      orderId: orderId.Value,
      createdAt: DateTime.UtcNow.AddMinutes(-5)
    );

    // Act - First upsert
    await perspective.Update(firstEvent, CancellationToken.None);

    var secondEvent = EventBuilder.CreateOrderCreatedEvent(
      orderId: orderId.Value,
      createdAt: DateTime.UtcNow
    );

    await perspective.Update(secondEvent, CancellationToken.None);

    // Assert - Verify only one record exists (upsert behavior)
    var order = await lens.GetByIdAsync(orderId.Value.ToString(), CancellationToken.None);

    await Assert.That(order).IsNotNull();
    await Assert.That(order!.OrderId).IsEqualTo(orderId.Value.ToString());
    // UpdatedAt should reflect the second event
    await Assert.That(order.UpdatedAt).IsGreaterThan(order.CreatedAt);
  }

  [Test]
  public async Task OrderPerspective_Update_HandlesMultipleCallsAsync() {
    // Arrange
    var hubContext = new MockHubContext<OrderStatusHub>();
    var perspective = new OrderPerspective(
      _helper.GetPerspectiveStore<OrderReadModel>(),
      _helper.GetLensQuery<OrderReadModel>(),
      hubContext,
      _helper.GetLogger<OrderPerspective>());

    var lens = _helper.GetLensQuery<OrderReadModel>();

    // Act - Multiple calls should succeed
    var orderId1 = OrderId.New();
    var orderId2 = OrderId.New();
    var orderId3 = OrderId.New();

    var event1 = EventBuilder.CreateOrderCreatedEvent(orderId: orderId1.Value);
    var event2 = EventBuilder.CreateOrderCreatedEvent(orderId: orderId2.Value);
    var event3 = EventBuilder.CreateOrderCreatedEvent(orderId: orderId3.Value);

    await perspective.Update(event1, CancellationToken.None);
    await perspective.Update(event2, CancellationToken.None);
    await perspective.Update(event3, CancellationToken.None);

    // Assert - All orders were stored correctly
    var order1 = await lens.GetByIdAsync(orderId1.Value.ToString(), CancellationToken.None);
    var order2 = await lens.GetByIdAsync(orderId2.Value.ToString(), CancellationToken.None);
    var order3 = await lens.GetByIdAsync(orderId3.Value.ToString(), CancellationToken.None);

    await Assert.That(order1).IsNotNull();
    await Assert.That(order2).IsNotNull();
    await Assert.That(order3).IsNotNull();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _helper.CleanupDatabaseAsync();
  }

  public async ValueTask DisposeAsync() {
    await _helper.DisposeAsync();
  }
}
