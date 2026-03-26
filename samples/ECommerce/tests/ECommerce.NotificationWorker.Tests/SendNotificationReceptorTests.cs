using System.Runtime.CompilerServices;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.NotificationWorker.Receptors;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace ECommerce.NotificationWorker.Tests;

/// <summary>
/// Tests for <see cref="SendNotificationReceptor"/>.
/// </summary>
public class SendNotificationReceptorTests {
  [Test]
  public async Task HandleAsync_SendsNotification_ReturnsNotificationSentEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<SendNotificationReceptor>.Instance;
    var receptor = new SendNotificationReceptor(dispatcher, logger);

    var command = new SendNotificationCommand {
      CustomerId = "customer-123",
      Subject = "Order Confirmation",
      Message = "Your order has been confirmed.",
      Type = NotificationType.Email
    };

    // Act
    var result = await receptor.HandleAsync(command, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.CustomerId).IsEqualTo("customer-123");
    await Assert.That(result.Subject).IsEqualTo("Order Confirmation");
    await Assert.That(result.Type).IsEqualTo(NotificationType.Email);
    await Assert.That(dispatcher.PublishedEvents).Count().IsEqualTo(1);
  }

  [Test]
  public async Task HandleAsync_PublishesNotificationSentEvent_ViaDispatcherAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = NullLogger<SendNotificationReceptor>.Instance;
    var receptor = new SendNotificationReceptor(dispatcher, logger);

    var command = new SendNotificationCommand {
      CustomerId = "customer-456",
      Subject = "Shipping Update",
      Message = "Your order has shipped.",
      Type = NotificationType.Sms
    };

    // Act
    await receptor.HandleAsync(command, CancellationToken.None);

    // Assert
    var publishedEvent = dispatcher.PublishedEvents.First() as NotificationSentEvent;
    await Assert.That(publishedEvent).IsNotNull();
    await Assert.That(publishedEvent!.CustomerId).IsEqualTo("customer-456");
  }

  /// <summary>
  /// Simple test double for IDispatcher that captures published events.
  /// </summary>
  private sealed class TestDispatcher : IDispatcher {
    public List<object> PublishedEvents { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      PublishedEvents.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Delivered(MessageId.New(), "test"));
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
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotImplementedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotImplementedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotImplementedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotImplementedException();

    // DispatchOptions overloads
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull =>
        Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Accepted(MessageId.New(), "test"));
    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) =>
        Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Accepted(MessageId.New(), "test"));
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) =>
        Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Accepted(MessageId.New(), "test"));
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) =>
        ValueTask.FromResult(default(TResult)!);
    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) =>
        ValueTask.CompletedTask;
    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) {
      PublishedEvents.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(DeliveryReceipt.Delivered(MessageId.New(), "test"));
    }
    public Task CascadeMessageAsync(IMessage message, DispatchModes mode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull {
      throw new NotImplementedException();
    }

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) {
      throw new NotImplementedException();
    }

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) where TMessage : notnull {
      throw new NotImplementedException();
    }

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, [CallerMemberName] string callerMemberName = "", [CallerFilePath] string callerFilePath = "", [CallerLineNumber] int callerLineNumber = 0) {
      throw new NotImplementedException();
    }

    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) {
      throw new NotImplementedException();
    }
  }
}
