using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for <see cref="IDispatcher.LocalInvokeAndSyncAsync{TMessage,TResult}"/> and
/// <see cref="IDispatcher.LocalInvokeAndSyncAsync{TMessage}"/>.
/// These tests verify that the dispatcher correctly invokes handlers and waits
/// for all perspectives to process emitted events.
/// </summary>
/// <docs>core-concepts/dispatcher#local-invoke-and-sync</docs>
[Category("Dispatcher")]
[Category("Sync")]
[NotInParallel] // Uses static ScopedEventTrackerAccessor.CurrentTracker
public sealed class DispatcherLocalInvokeAndSyncTests {
  // Test messages
  public record CreateOrderCommand(Guid CustomerId, decimal Amount);
  public record OrderCreatedResult(Guid OrderId);
  public record VoidCommand(string Data);

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithResult_InvokesHandlerAndReturnsSyncedAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new CreateOrderCommand(Guid.NewGuid(), 100.00m);

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Simulate event tracking (normally done by event store decorator)
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync<CreateOrderCommand, OrderCreatedResult>(command);

      // Assert
      await Assert.That(result).IsNotNull();
      await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
      await Assert.That(eventCompletionAwaiter.WaitForEventsWasCalled).IsTrue();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_Void_InvokesHandlerAndReturnsSyncResultAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new VoidCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Simulate event tracking
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      // Assert
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
      await Assert.That(result.EventsAwaited).IsEqualTo(1);
      await Assert.That(eventCompletionAwaiter.WaitForEventsWasCalled).IsTrue();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithNoEvents_ReturnsNoPendingEventsAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new FakeScopedEventTracker(); // Empty - no events tracked
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new VoidCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      // Assert - should return NoPendingEvents since no events were tracked
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
      await Assert.That(result.EventsAwaited).IsEqualTo(0);
      await Assert.That(eventCompletionAwaiter.WaitForEventsWasCalled).IsFalse();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithResult_WhenTimeout_ThrowsTimeoutExceptionAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: false); // Will timeout
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new CreateOrderCommand(Guid.NewGuid(), 100.00m);

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Simulate event tracking
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act & Assert
      await Assert.That(async () => await dispatcher.LocalInvokeAndSyncAsync<CreateOrderCommand, OrderCreatedResult>(
          command,
          timeout: TimeSpan.FromMilliseconds(10)))
          .ThrowsExactly<TimeoutException>();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_Void_WhenTimeout_ReturnsTimedOutOutcomeAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: false); // Will timeout
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new VoidCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Simulate event tracking
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(
          command,
          timeout: TimeSpan.FromMilliseconds(10));

      // Assert
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
      await Assert.That(result.EventsAwaited).IsEqualTo(1);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithMultipleEvents_WaitsForAllEventsAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new VoidCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Simulate multiple events being tracked
      var eventId1 = Guid.NewGuid();
      var eventId2 = Guid.NewGuid();
      var eventId3 = Guid.NewGuid();
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), eventId1);
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), eventId2);
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), eventId3);

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      // Assert
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
      await Assert.That(result.EventsAwaited).IsEqualTo(3);
      await Assert.That(eventCompletionAwaiter.LastEventIds).IsNotNull();
      await Assert.That(eventCompletionAwaiter.LastEventIds!.Count).IsEqualTo(3);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithoutEventCompletionAwaiter_ReturnsSyncedAsync() {
    // Arrange - no IEventCompletionAwaiter registered
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter: null);
    var command = new VoidCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Simulate event tracking
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      // Assert - should return Synced since we can't verify either way
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
      await Assert.That(result.EventsAwaited).IsEqualTo(1);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  // Helper to create dispatcher with test dependencies
  private static IDispatcher _createDispatcher(IEventCompletionAwaiter? eventCompletionAwaiter) {
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    // Register test receptors
    services.AddReceptors();

    // Register event completion awaiter if provided
    if (eventCompletionAwaiter != null) {
      services.AddSingleton(eventCompletionAwaiter);
    }

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // Test receptors
  public class CreateOrderReceptor : IReceptor<CreateOrderCommand, OrderCreatedResult> {
    public ValueTask<OrderCreatedResult> HandleAsync(CreateOrderCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new OrderCreatedResult(Guid.NewGuid()));
    }
  }

  public class VoidCommandReceptor : IReceptor<VoidCommand> {
    public ValueTask HandleAsync(VoidCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  // Fake implementations for testing
  private sealed class FakeEventCompletionAwaiter(bool completesImmediately) : IEventCompletionAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();
    private readonly bool _completesImmediately = completesImmediately;

    public bool WaitForEventsWasCalled { get; private set; }
    public IReadOnlyList<Guid>? LastEventIds { get; private set; }

    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, CancellationToken cancellationToken = default) {
      WaitForEventsWasCalled = true;
      LastEventIds = eventIds;
      return Task.FromResult(_completesImmediately);
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => _completesImmediately;
  }

  private sealed class FakeScopedEventTracker : IScopedEventTracker {
    private readonly List<TrackedEvent> _events = [];

    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) {
      _events.Add(new TrackedEvent(streamId, eventType, eventId));
    }

    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => _events;

    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => _events;

    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) {
      return _events.All(e => processedEventIds.Contains(e.EventId));
    }
  }
}
