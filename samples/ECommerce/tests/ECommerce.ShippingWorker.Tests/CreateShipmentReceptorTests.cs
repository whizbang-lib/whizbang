using System.Runtime.CompilerServices;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.ShippingWorker.Receptors;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace ECommerce.ShippingWorker.Tests;

/// <summary>
/// Tests for <see cref="CreateShipmentReceptor"/>.
/// </summary>
public class CreateShipmentReceptorTests {
  [Test]
  public async Task HandleAsync_CreatesShipment_ReturnsShipmentCreatedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<CreateShipmentReceptor>.Instance;
    var receptor = new CreateShipmentReceptor(dispatcher, logger);

    var command = new CreateShipmentCommand {
      OrderId = "order-123",
      ShippingAddress = "123 Main St, City, State 12345"
    };

    // Act
    var result = await receptor.HandleAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsEqualTo("order-123");
    await Assert.That(result.ShipmentId).StartsWith("SHIP-");
    await Assert.That(result.TrackingNumber).StartsWith("TRK");
  }

  [Test]
  public async Task HandleAsync_PublishesShipmentCreatedEvent_ViaDispatcherAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<CreateShipmentReceptor>.Instance;
    var receptor = new CreateShipmentReceptor(dispatcher, logger);

    var command = new CreateShipmentCommand {
      OrderId = "order-456",
      ShippingAddress = "456 Oak Ave, Town, State 67890"
    };

    // Act
    await receptor.HandleAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(dispatcher.PublishedEvents).Count().IsEqualTo(1);
    var publishedEvent = dispatcher.PublishedEvents.First() as ShipmentCreatedEvent;
    await Assert.That(publishedEvent).IsNotNull();
    await Assert.That(publishedEvent!.OrderId).IsEqualTo("order-456");
  }

  /// <summary>
  /// Simple test double for IDispatcher that captures published events.
  /// </summary>
  private sealed class TestDispatcher : IDispatcher {
    public List<object> PublishedEvents { get; } = [];

    public Task PublishAsync<TEvent>(TEvent eventData) {
      PublishedEvents.Add(eventData!);
      return Task.CompletedTask;
    }

    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull =>
        Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Accepted(MessageId.New(), "test"));
    public Task<IDeliveryReceipt> SendAsync(object message) =>
        Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Accepted(MessageId.New(), "test"));
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) =>
        Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Accepted(MessageId.New(), "test"));

    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull =>
        ValueTask.FromResult(default(TResult)!);
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) =>
        ValueTask.FromResult(default(TResult)!);
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull =>
        ValueTask.FromResult(default(TResult)!);
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) =>
        ValueTask.FromResult(default(TResult)!);

    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull =>
        ValueTask.CompletedTask;
    public ValueTask LocalInvokeAsync(object message) =>
        ValueTask.CompletedTask;
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull =>
        ValueTask.CompletedTask;
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) =>
        ValueTask.CompletedTask;

    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
        Task.FromResult<IEnumerable<IDeliveryReceipt>>([]);
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
        Task.FromResult<IEnumerable<IDeliveryReceipt>>([]);
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) =>
        ValueTask.FromResult<IEnumerable<TResult>>([]);
  }
}
