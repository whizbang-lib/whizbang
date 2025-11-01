using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for v0.1.0 Dispatcher functionality.
/// These tests define the required behavior for message routing and orchestration.
/// </summary>
public class DispatcherTests {
  // Test Messages
  public record CreateOrder(Guid CustomerId, string[] Items);
  public record OrderCreated(Guid OrderId, Guid CustomerId);
  public record OrderPlaced(Guid OrderId);

  [Test]
  public async Task Send_WithValidMessage_ShouldReturnResultAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1", "item2" });

    // Act
    var result = await dispatcher.Send<OrderCreated>(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<OrderCreated>();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task Send_WithUnknownMessageType_ShouldThrowHandlerNotFoundExceptionAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var unknownCommand = new UnknownCommand();

    // Act & Assert
    await Assert.That(async () => await dispatcher.Send<object>(unknownCommand))
        .ThrowsExactly<HandlerNotFoundException>()
        .WithMessage("*UnknownCommand*");
  }

  [Test]
  public async Task Send_WithContext_ShouldPreserveCorrelationIdAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });
    var context = MessageContext.Create(
        CorrelationId.New(),
        CausationId.New()
    );

    // Act
    var result = await dispatcher.Send<OrderCreated>(command, context);

    // Assert
    await Assert.That(result).IsNotNull();
    // Context should be tracked (verified through traceability)
  }

  [Test]
  public async Task Publish_WithEvent_ShouldNotifyAllHandlersAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var orderCreated = new OrderCreated(Guid.NewGuid(), Guid.NewGuid());
    var notificationCount = 0;

    // Subscribe multiple handlers (this will be via perspectives in implementation)
    // For now, test that Publish doesn't throw

    // Act
    await dispatcher.Publish(orderCreated);

    // Assert
    // Should complete without error
    // In full implementation, verify all perspectives were notified
  }

  [Test]
  public async Task SendMany_WithMultipleCommands_ShouldReturnAllResultsAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var commands = new object[] {
            new CreateOrder(Guid.NewGuid(), new[] { "item1" }),
            new CreateOrder(Guid.NewGuid(), new[] { "item2" }),
            new CreateOrder(Guid.NewGuid(), new[] { "item3" })
        };

    // Act
    var results = await dispatcher.SendMany<OrderCreated>(commands);

    // Assert
    await Assert.That(results).IsNotNull();
    await Assert.That(results.Count()).IsEqualTo(3);
    foreach (var result in results) {
      await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
    }
  }

  [Test]
  public async Task Dispatcher_MessageContext_ShouldGenerateUniqueMessageIdsAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var command1 = new CreateOrder(Guid.NewGuid(), new[] { "item1" });
    var command2 = new CreateOrder(Guid.NewGuid(), new[] { "item2" });

    // Act
    var result1 = await dispatcher.Send<OrderCreated>(command1);
    var result2 = await dispatcher.Send<OrderCreated>(command2);

    // Assert
    // Each message should have unique MessageId (tracked in context)
    await Assert.That(result1.OrderId).IsNotEqualTo(result2.OrderId);
  }

  [Test]
  public async Task Dispatcher_ShouldRouteToCorrectHandlerAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var createCommand = new CreateOrder(Guid.NewGuid(), new[] { "item1" });

    // Act
    var result = await dispatcher.Send<OrderCreated>(createCommand);

    // Assert - Should route to OrderReceptor specifically
    await Assert.That(result).IsTypeOf<OrderCreated>();
    await Assert.That(result.CustomerId).IsEqualTo(createCommand.CustomerId);
  }

  [Test]
  public async Task Dispatcher_MultipleReceptorsSameMessage_ShouldRouteToAllAsync() {
    // This tests multi-destination routing
    // When multiple receptors handle the same message type,
    // dispatcher should route to all of them

    // Arrange
    var dispatcher = CreateDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });

    // Act - Send should complete
    // In full implementation with multi-destination support,
    // this would return results from all receptors
    var result = await dispatcher.Send<OrderCreated>(command);

    // Assert
    await Assert.That(result).IsNotNull();
  }

  [Test]
  public async Task Dispatcher_ShouldTrackCausationChainAsync() {
    // Arrange
    var dispatcher = CreateDispatcher();
    var initialContext = MessageContext.New();
    var command = new CreateOrder(Guid.NewGuid(), new[] { "item1" });

    // Act
    var result = await dispatcher.Send<OrderCreated>(command, initialContext);

    // Assert
    // The result's causation should reference the command's message ID
    await Assert.That(result).IsNotNull();
    // Full causation tracking verified in integration tests
  }

  // Helper method to create dispatcher
  // Will be implemented to return InMemoryDispatcher
  private IDispatcher CreateDispatcher() {
    throw new NotImplementedException("Dispatcher creation not yet implemented");
  }

  // Test supporting types
  public record UnknownCommand();
}
