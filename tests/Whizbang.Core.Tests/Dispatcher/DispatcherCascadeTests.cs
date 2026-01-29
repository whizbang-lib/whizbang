using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for automatic event cascade from receptor return values.
/// When a receptor returns a tuple or array containing IEvent instances,
/// the Dispatcher should automatically publish those events.
/// </summary>
/// <tests>Whizbang.Core/Dispatcher.cs:_cascadeEventsFromResultAsync</tests>
public class DispatcherCascadeTests {
  // ========================================
  // TEST MESSAGES AND EVENTS
  // ========================================

  public record CreateOrderCommand(Guid OrderId, Guid CustomerId);
  public record OrderCreatedResult(Guid OrderId);
  public record OrderCreatedEvent([property: StreamKey] Guid OrderId, Guid CustomerId) : IEvent;
  public record OrderShippedEvent([property: StreamKey] Guid OrderId) : IEvent;
  public record NotificationSentEvent([property: StreamKey] Guid OrderId, string Type) : IEvent;

  // ========================================
  // EVENT TRACKING INFRASTRUCTURE
  // ========================================

  /// <summary>
  /// Tracks events that have been published through the cascade mechanism.
  /// This receptor subscribes to all test events and records them.
  /// </summary>
  public static class PublishedEventTracker {
    private static readonly List<IEvent> _publishedEvents = [];
    private static readonly object _lock = new();

    public static void Reset() {
      lock (_lock) {
        _publishedEvents.Clear();
      }
    }

    public static void Track(IEvent evt) {
      lock (_lock) {
        _publishedEvents.Add(evt);
      }
    }

    public static IReadOnlyList<IEvent> GetPublishedEvents() {
      lock (_lock) {
        return _publishedEvents.ToList();
      }
    }

    public static int Count {
      get {
        lock (_lock) {
          return _publishedEvents.Count;
        }
      }
    }
  }

  // ========================================
  // TEST RECEPTORS - RETURN TUPLES/ARRAYS WITH EVENTS
  // ========================================

  /// <summary>
  /// Receptor that returns a tuple containing a result DTO and an event.
  /// The event should be automatically published by the Dispatcher.
  /// </summary>
  public class TupleReturningReceptor : IReceptor<CreateOrderCommand, (OrderCreatedResult, OrderCreatedEvent)> {
    public ValueTask<(OrderCreatedResult, OrderCreatedEvent)> HandleAsync(
        CreateOrderCommand message,
        CancellationToken cancellationToken = default) {
      var result = new OrderCreatedResult(message.OrderId);
      var evt = new OrderCreatedEvent(message.OrderId, message.CustomerId);
      return ValueTask.FromResult((result, evt));
    }
  }

  /// <summary>
  /// Receptor that returns an array of events.
  /// All events should be automatically published.
  /// </summary>
  public record CreateNotificationsCommand(Guid OrderId);

  public class ArrayReturningReceptor : IReceptor<CreateNotificationsCommand, IEvent[]> {
    public ValueTask<IEvent[]> HandleAsync(
        CreateNotificationsCommand message,
        CancellationToken cancellationToken = default) {
      var events = new IEvent[] {
        new NotificationSentEvent(message.OrderId, "Email"),
        new NotificationSentEvent(message.OrderId, "SMS")
      };
      return ValueTask.FromResult(events);
    }
  }

  /// <summary>
  /// Receptor that returns a tuple with multiple events.
  /// </summary>
  public record ShipOrderCommand(Guid OrderId, Guid CustomerId);

  public class MultiEventTupleReceptor : IReceptor<ShipOrderCommand, (OrderCreatedEvent, OrderShippedEvent, OrderCreatedResult)> {
    public ValueTask<(OrderCreatedEvent, OrderShippedEvent, OrderCreatedResult)> HandleAsync(
        ShipOrderCommand message,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult((
        new OrderCreatedEvent(message.OrderId, message.CustomerId),
        new OrderShippedEvent(message.OrderId),
        new OrderCreatedResult(message.OrderId)
      ));
    }
  }

  /// <summary>
  /// Receptor that returns a nested tuple with events.
  /// </summary>
  public record NestedCommand(Guid OrderId, Guid CustomerId);

  public class NestedTupleReceptor : IReceptor<NestedCommand, (OrderCreatedResult, (OrderCreatedEvent, OrderShippedEvent))> {
    public ValueTask<(OrderCreatedResult, (OrderCreatedEvent, OrderShippedEvent))> HandleAsync(
        NestedCommand message,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult((
        new OrderCreatedResult(message.OrderId),
        (new OrderCreatedEvent(message.OrderId, message.CustomerId), new OrderShippedEvent(message.OrderId))
      ));
    }
  }

  /// <summary>
  /// Receptor that returns a simple result without any events.
  /// No events should be published.
  /// </summary>
  public record SimpleCommand(string Data);
  public record SimpleResult(string Data);

  public class NonEventReturningReceptor : IReceptor<SimpleCommand, SimpleResult> {
    public ValueTask<SimpleResult> HandleAsync(
        SimpleCommand message,
        CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new SimpleResult(message.Data));
    }
  }

  /// <summary>
  /// Receptor that returns an array that may be empty.
  /// Should handle gracefully without crashing.
  /// </summary>
  public record EmptyEventsCommand(Guid OrderId);

  public class EmptyArrayReceptor : IReceptor<EmptyEventsCommand, (OrderCreatedResult, IEvent[])> {
    public ValueTask<(OrderCreatedResult, IEvent[])> HandleAsync(
        EmptyEventsCommand message,
        CancellationToken cancellationToken = default) {
      // Return empty array - should not crash and should not publish anything
      return ValueTask.FromResult((new OrderCreatedResult(message.OrderId), Array.Empty<IEvent>()));
    }
  }

  /// <summary>
  /// Receptor that subscribes to OrderCreatedEvent and tracks it.
  /// Used to verify events were published through cascade.
  /// </summary>
  public class EventTrackingReceptor : IReceptor<OrderCreatedEvent> {
    public ValueTask HandleAsync(OrderCreatedEvent message, CancellationToken cancellationToken = default) {
      PublishedEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Receptor that subscribes to OrderShippedEvent and tracks it.
  /// </summary>
  public class ShippedEventTrackingReceptor : IReceptor<OrderShippedEvent> {
    public ValueTask HandleAsync(OrderShippedEvent message, CancellationToken cancellationToken = default) {
      PublishedEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Receptor that subscribes to NotificationSentEvent and tracks it.
  /// </summary>
  public class NotificationEventTrackingReceptor : IReceptor<NotificationSentEvent> {
    public ValueTask HandleAsync(NotificationSentEvent message, CancellationToken cancellationToken = default) {
      PublishedEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // TESTS - ALL SHOULD FAIL INITIALLY (TDD RED PHASE)
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_TupleWithEvent_AutoPublishesEventAsync() {
    // Arrange
    PublishedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new CreateOrderCommand(Guid.NewGuid(), Guid.NewGuid());

    // Act - Invoke receptor that returns (Result, Event)
    var result = await dispatcher.LocalInvokeAsync<(OrderCreatedResult, OrderCreatedEvent)>(command);

    // Assert - Result should be returned normally
    await Assert.That(result.Item1).IsNotNull();
    await Assert.That(result.Item1.OrderId).IsEqualTo(command.OrderId);

    // Assert - Event should be automatically published (CASCADE!)
    // This test will FAIL until we implement auto-cascade
    await Assert.That(PublishedEventTracker.Count).IsEqualTo(1)
      .Because("The event from the tuple should be auto-published");
    var publishedEvent = PublishedEventTracker.GetPublishedEvents()[0] as OrderCreatedEvent;
    await Assert.That(publishedEvent).IsNotNull();
    await Assert.That(publishedEvent!.OrderId).IsEqualTo(command.OrderId);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_ArrayOfEvents_AutoPublishesAllEventsAsync() {
    // Arrange
    PublishedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new CreateNotificationsCommand(Guid.NewGuid());

    // Act - Invoke receptor that returns IEvent[]
    var result = await dispatcher.LocalInvokeAsync<IEvent[]>(command);

    // Assert - Array should be returned normally
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Length).IsEqualTo(2);

    // Assert - All events should be automatically published (CASCADE!)
    await Assert.That(PublishedEventTracker.Count).IsEqualTo(2)
      .Because("All events in the array should be auto-published");
    var notifications = PublishedEventTracker.GetPublishedEvents()
      .OfType<NotificationSentEvent>()
      .ToList();
    await Assert.That(notifications.Count).IsEqualTo(2);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_TupleWithMultipleEvents_AutoPublishesAllEventsAsync() {
    // Arrange
    PublishedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new ShipOrderCommand(Guid.NewGuid(), Guid.NewGuid());

    // Act - Invoke receptor that returns (Event1, Event2, Result)
    var result = await dispatcher.LocalInvokeAsync<(OrderCreatedEvent, OrderShippedEvent, OrderCreatedResult)>(command);

    // Assert - All items returned normally
    await Assert.That(result.Item1).IsNotNull();
    await Assert.That(result.Item2).IsNotNull();
    await Assert.That(result.Item3).IsNotNull();

    // Assert - Both events should be auto-published (CASCADE!)
    await Assert.That(PublishedEventTracker.Count).IsEqualTo(2)
      .Because("Both events in the tuple should be auto-published");

    var publishedEvents = PublishedEventTracker.GetPublishedEvents();
    await Assert.That(publishedEvents.Any(e => e is OrderCreatedEvent)).IsTrue();
    await Assert.That(publishedEvents.Any(e => e is OrderShippedEvent)).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_NestedTupleWithEvents_AutoPublishesAllEventsAsync() {
    // Arrange
    PublishedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new NestedCommand(Guid.NewGuid(), Guid.NewGuid());

    // Act - Invoke receptor that returns (Result, (Event1, Event2))
    var result = await dispatcher.LocalInvokeAsync<(OrderCreatedResult, (OrderCreatedEvent, OrderShippedEvent))>(command);

    // Assert - Nested structure returned normally
    await Assert.That(result.Item1).IsNotNull();
    await Assert.That(result.Item2.Item1).IsNotNull();
    await Assert.That(result.Item2.Item2).IsNotNull();

    // Assert - Events from nested tuple should be auto-published (CASCADE!)
    await Assert.That(PublishedEventTracker.Count).IsEqualTo(2)
      .Because("Events in nested tuples should be recursively extracted and auto-published");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_NonEventReturn_DoesNotPublishAnythingAsync() {
    // Arrange
    PublishedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new SimpleCommand("test data");

    // Act - Invoke receptor that returns plain result (no IEvent)
    var result = await dispatcher.LocalInvokeAsync<SimpleResult>(command);

    // Assert - Result returned normally
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Data).IsEqualTo("test data");

    // Assert - No events should be published (no IEvent in return value)
    await Assert.That(PublishedEventTracker.Count).IsEqualTo(0)
      .Because("Non-event return values should not trigger any publishing");
  }

  [Test]
  [NotInParallel]
  public async Task SendAsync_TupleWithEvent_AutoPublishesEventAsync() {
    // Arrange
    PublishedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new CreateOrderCommand(Guid.NewGuid(), Guid.NewGuid());

    // Act - Use SendAsync (outbox path) instead of LocalInvokeAsync
    var receipt = await dispatcher.SendAsync(command);

    // Assert - Message was sent successfully
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);

    // Assert - Event should still be auto-published from the receptor's tuple return (CASCADE!)
    // Note: SendAsync executes the receptor locally, so cascade should still happen
    await Assert.That(PublishedEventTracker.Count).IsEqualTo(1)
      .Because("Events from receptor tuple returns should cascade even via SendAsync");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_EmptyArrayInTuple_HandlesGracefullyAsync() {
    // Arrange
    PublishedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var command = new EmptyEventsCommand(Guid.NewGuid());

    // Act - Invoke receptor that returns (Result, empty array)
    var result = await dispatcher.LocalInvokeAsync<(OrderCreatedResult, IEvent[])>(command);

    // Assert - Result returned normally
    await Assert.That(result.Item1).IsNotNull();
    await Assert.That(result.Item2).IsNotNull();
    await Assert.That(result.Item2.Length).IsEqualTo(0);

    // Assert - No crash, no events published (empty array)
    await Assert.That(PublishedEventTracker.Count).IsEqualTo(0)
      .Because("Empty arrays in tuples should be handled gracefully without publishing");
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    // Register all receptors including our test receptors
    services.AddReceptors();

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
