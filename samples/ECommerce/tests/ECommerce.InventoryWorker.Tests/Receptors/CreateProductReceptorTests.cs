using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.InventoryWorker.Receptors;
using Microsoft.Extensions.Logging;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace ECommerce.InventoryWorker.Tests.Receptors;

/// <summary>
/// Tests for CreateProductReceptor
/// </summary>
public class CreateProductReceptorTests {
  [Test]
  public async Task HandleAsync_WithValidCommand_ReturnsProductCreatedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var command = new CreateProductCommand {
      ProductId = "prod-123",
      Name = "Test Widget",
      Description = "A test widget",
      Price = 29.99m,
      ImageUrl = "https://example.com/widget.jpg",
      InitialStock = 0
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.ProductId).IsEqualTo("prod-123");
    await Assert.That(result.Name).IsEqualTo("Test Widget");
    await Assert.That(result.Description).IsEqualTo("A test widget");
    await Assert.That(result.Price).IsEqualTo(29.99m);
    await Assert.That(result.ImageUrl).IsEqualTo("https://example.com/widget.jpg");
  }

  [Test]
  public async Task HandleAsync_WithValidCommand_PublishesProductCreatedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var command = new CreateProductCommand {
      ProductId = "prod-456",
      Name = "Widget",
      Description = "Description",
      Price = 19.99m,
      ImageUrl = null,
      InitialStock = 0
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert
    await Assert.That(dispatcher.PublishedEvents).HasCount().EqualTo(1);
    await Assert.That(dispatcher.PublishedEvents[0]).IsTypeOf<ProductCreatedEvent>();

    var publishedEvent = (ProductCreatedEvent)dispatcher.PublishedEvents[0];
    await Assert.That(publishedEvent.ProductId).IsEqualTo("prod-456");
    await Assert.That(publishedEvent.Name).IsEqualTo("Widget");
  }

  [Test]
  public async Task HandleAsync_WithZeroInitialStock_PublishesOnlyProductCreatedEventAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var command = new CreateProductCommand {
      ProductId = "prod-789",
      Name = "No Stock Widget",
      Description = "Widget without stock",
      Price = 9.99m,
      ImageUrl = null,
      InitialStock = 0
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert - Only one event (ProductCreated)
    await Assert.That(dispatcher.PublishedEvents).HasCount().EqualTo(1);
    await Assert.That(dispatcher.PublishedEvents[0]).IsTypeOf<ProductCreatedEvent>();
  }

  [Test]
  public async Task HandleAsync_WithPositiveInitialStock_PublishesBothEventsAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var command = new CreateProductCommand {
      ProductId = "prod-stock",
      Name = "Stocked Widget",
      Description = "Widget with stock",
      Price = 49.99m,
      ImageUrl = null,
      InitialStock = 100
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert - Two events (ProductCreated + InventoryRestocked)
    await Assert.That(dispatcher.PublishedEvents).HasCount().EqualTo(2);
    await Assert.That(dispatcher.PublishedEvents[0]).IsTypeOf<ProductCreatedEvent>();
    await Assert.That(dispatcher.PublishedEvents[1]).IsTypeOf<InventoryRestockedEvent>();

    var inventoryEvent = (InventoryRestockedEvent)dispatcher.PublishedEvents[1];
    await Assert.That(inventoryEvent.ProductId).IsEqualTo("prod-stock");
    await Assert.That(inventoryEvent.QuantityAdded).IsEqualTo(100);
    await Assert.That(inventoryEvent.NewTotalQuantity).IsEqualTo(100);
  }

  [Test]
  public async Task HandleAsync_WithNullImageUrl_CreatesEventWithNullImageUrlAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var command = new CreateProductCommand {
      ProductId = "prod-no-img",
      Name = "No Image Widget",
      Description = "Widget without image",
      Price = 14.99m,
      ImageUrl = null,
      InitialStock = 0
    };

    // Act
    var result = await receptor.HandleAsync(command);

    // Assert
    await Assert.That(result.ImageUrl).IsNull();
  }

  [Test]
  public async Task HandleAsync_SetsCreatedAtTimestampAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var beforeCall = DateTime.UtcNow;

    var command = new CreateProductCommand {
      ProductId = "prod-time",
      Name = "Timestamp Widget",
      Description = "Test timestamp",
      Price = 99.99m,
      ImageUrl = null,
      InitialStock = 0
    };

    // Act
    var result = await receptor.HandleAsync(command);

    var afterCall = DateTime.UtcNow;

    // Assert - CreatedAt should be between before and after
    await Assert.That(result.CreatedAt).IsGreaterThanOrEqualTo(beforeCall);
    await Assert.That(result.CreatedAt).IsLessThanOrEqualTo(afterCall);
  }

  [Test]
  public async Task HandleAsync_LogsInformation_AboutProductCreationAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var command = new CreateProductCommand {
      ProductId = "prod-log",
      Name = "Log Widget",
      Description = "Test logging",
      Price = 5.99m,
      ImageUrl = null,
      InitialStock = 0
    };

    // Act
    await receptor.HandleAsync(command);

    // Assert - Should have logged something
    await Assert.That(logger.LoggedMessages).HasCount().GreaterThanOrEqualTo(1);
  }

  [Test]
  public async Task HandleAsync_WithCancellationToken_CompletesSuccessfullyAsync() {
    // Arrange
    var dispatcher = new TestDispatcher();
    var logger = new TestLogger<CreateProductReceptor>();
    var receptor = new CreateProductReceptor(dispatcher, logger);

    var command = new CreateProductCommand {
      ProductId = "prod-cancel",
      Name = "Cancel Widget",
      Description = "Test cancellation",
      Price = 1.99m,
      ImageUrl = null,
      InitialStock = 0
    };

    var cts = new CancellationTokenSource();

    // Act
    var result = await receptor.HandleAsync(command, cts.Token);

    // Assert
    await Assert.That(result).IsNotNull();
  }
}

/// <summary>
/// Test implementation of IDispatcher for testing
/// </summary>
internal class TestDispatcher : IDispatcher {
  public List<object> PublishedEvents { get; } = new();

  public Task PublishAsync<TEvent>(TEvent @event) {
    PublishedEvents.Add(@event!);
    return Task.CompletedTask;
  }

  // Minimal stub implementations for other IDispatcher methods
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull =>
    throw new NotImplementedException();

  public Task<IDeliveryReceipt> SendAsync(object message) =>
    throw new NotImplementedException();

  public Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0) =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(
    TMessage message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask<TResult> LocalInvokeAsync<TResult>(
    object message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0) =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync(object message) =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0) where TMessage : notnull =>
    throw new NotImplementedException();

  public ValueTask LocalInvokeAsync(
    object message,
    IMessageContext context,
    string callerMemberName = "",
    string callerFilePath = "",
    int callerLineNumber = 0) =>
    throw new NotImplementedException();

  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
    throw new NotImplementedException();

  public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) =>
    throw new NotImplementedException();
}

/// <summary>
/// Test implementation of ILogger for testing
/// </summary>
internal class TestLogger<T> : ILogger<T> {
  public List<string> LoggedMessages { get; } = new();

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
    var message = formatter(state, exception);
    LoggedMessages.Add(message);
  }
}
