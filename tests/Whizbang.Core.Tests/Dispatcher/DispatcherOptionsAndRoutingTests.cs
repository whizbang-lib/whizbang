using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for Dispatcher functionality related to:
/// - DispatchOptions handling
/// - IRouted message handling (Route.Local, Route.None)
/// - WaitForPerspectives flow
/// - SendAsync overloads with options
/// </summary>
[Category("Dispatcher")]
[Category("Coverage")]
public sealed class DispatcherOptionsAndRoutingTests {
  // Test messages
  public record TestCommand(string Data);
  public record TestResult(Guid Id);
  public record TestEvent(Guid EventId);

  // ========================================
  // SENDATASYNC WITH DISPATCHOPTIONS TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithDispatchOptions_ReturnsDeliveryReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync(command, options);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithDispatchOptions_GenericOverload_ReturnsDeliveryReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    var options = new DispatchOptions();

    // Act
    var receipt = await dispatcher.SendAsync<TestCommand>(command, options);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_WithDispatchOptionsAndContext_ReturnsDeliveryReceiptAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    var options = new DispatchOptions();
    var context = MessageContext.New();

    // Act
    var receipt = await dispatcher.SendAsync(command, context, options);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.CorrelationId).IsEqualTo(context.CorrelationId);
  }

  [Test]
  public async Task SendAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  [Test]
  public async Task SendAsync_WithDispatchOptionsAndCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    var context = MessageContext.New();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(command, context, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // LOCALINVOKEASYNC WITH DISPATCHOPTIONS TESTS
  // ========================================

  [Test]
  public async Task LocalInvokeAsync_WithDispatchOptions_ReturnsResultAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    var options = new DispatchOptions();

    // Act
    var result = await dispatcher.LocalInvokeAsync<TestResult>(command, options);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Id).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task LocalInvokeAsync_Void_WithDispatchOptions_CompletesSuccessfullyAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    var options = new DispatchOptions();

    // Act & Assert - should not throw
    await dispatcher.LocalInvokeAsync(command, options);
  }

  [Test]
  public async Task LocalInvokeAsync_WithDispatchOptionsAndCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test data");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var options = new DispatchOptions { CancellationToken = cts.Token };

    // Act & Assert
    await Assert.That(async () => await dispatcher.LocalInvokeAsync<TestResult>(command, options))
      .ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // IROUTED MESSAGE HANDLING TESTS
  // ========================================

  [Test]
  public async Task SendAsync_NonGeneric_WithRoutedLocalMessage_UnwrapsAndDispatchesAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var innerCommand = new TestCommand("routed data");
    object routedMessage = Route.Local(innerCommand);
    var context = MessageContext.New();

    // Act - Non-generic SendAsync unwraps IRouted before dispatch
    var receipt = await dispatcher.SendAsync(routedMessage, context);

    // Assert
    await Assert.That(receipt).IsNotNull();
    await Assert.That(receipt.Status).IsEqualTo(DeliveryStatus.Delivered);
  }

  [Test]
  public async Task SendAsync_NonGeneric_WithRouteNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    object routedNone = Route.None();
    var context = MessageContext.New();

    // Act & Assert - Non-generic SendAsync checks for RoutedNone
    var exception = await Assert.That(async () => await dispatcher.SendAsync(routedNone, context))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(exception!.Message).Contains("RoutedNone");
    await Assert.That(exception!.Message).Contains("Route.None()");
  }

  [Test]
  public async Task SendAsync_WithOptionsAndRouteNone_ThrowsArgumentExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    object routedNone = Route.None();
    var context = MessageContext.New();
    var options = new DispatchOptions();

    // Act & Assert
    var exception = await Assert.That(async () => await dispatcher.SendAsync(routedNone, context, options))
      .ThrowsExactly<ArgumentException>();

    await Assert.That(exception!.Message).Contains("RoutedNone");
  }

  [Test]
  public async Task LocalInvokeAsync_NonGeneric_WithRoutedLocalMessage_UnwrapsAndDispatchesAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var innerCommand = new TestCommand("routed data");
    object routedMessage = Route.Local(innerCommand);
    var context = MessageContext.New();

    // Act - Non-generic LocalInvokeAsync unwraps IRouted and dispatches inner message
    var result = await dispatcher.LocalInvokeAsync<TestResult>(routedMessage, context);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Id).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // WAITFORPERSPECTIVES TESTS
  // Note: WaitForPerspectives is only supported by LocalInvokeAsync, not SendAsync.
  // SendAsync uses the inbox pattern and doesn't wait for perspectives.
  // ========================================

  [Test]
  [NotInParallel] // Uses static ScopedEventTrackerAccessor.CurrentTracker
  public async Task LocalInvokeAsync_WithWaitForPerspectivesTrue_WaitsForEventsAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new TestCommand("test data");
    var options = new DispatchOptions { WaitForPerspectives = true };

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAsync<TestResult>(command, options);

      // Assert
      await Assert.That(result).IsNotNull();
      await Assert.That(eventCompletionAwaiter.WaitForEventsWasCalled).IsTrue();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithWaitForPerspectivesFalse_DoesNotWaitAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new TestCommand("test data");
    var options = new DispatchOptions { WaitForPerspectives = false };

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAsync<TestResult>(command, options);

      // Assert
      await Assert.That(result).IsNotNull();
      await Assert.That(eventCompletionAwaiter.WaitForEventsWasCalled).IsFalse();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithWaitForPerspectives_NoEvents_DoesNotWaitAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new FakeScopedEventTracker(); // Empty - no events
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new TestCommand("test data");
    var options = new DispatchOptions { WaitForPerspectives = true };

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      // Act - no events tracked
      var result = await dispatcher.LocalInvokeAsync<TestResult>(command, options);

      // Assert
      await Assert.That(result).IsNotNull();
      await Assert.That(eventCompletionAwaiter.WaitForEventsWasCalled).IsFalse();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_WithWaitForPerspectives_Timeout_ThrowsPerspectiveSyncTimeoutExceptionAsync() {
    // Arrange
    var eventCompletionAwaiter = new FakeEventCompletionAwaiter(completesImmediately: false);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter);
    var command = new TestCommand("test data");
    var options = new DispatchOptions {
      WaitForPerspectives = true,
      PerspectiveWaitTimeout = TimeSpan.FromMilliseconds(10)
    };

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(TestEvent), Guid.NewGuid());

      // Act & Assert
      await Assert.That(async () => await dispatcher.LocalInvokeAsync<TestResult>(command, options))
        .ThrowsExactly<PerspectiveSyncTimeoutException>();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  // ========================================
  // NULL MESSAGE VALIDATION TESTS
  // ========================================

  [Test]
  public async Task SendAsync_WithNullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var context = MessageContext.New();

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(null!, context))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task SendAsync_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestCommand("test");

    // Act & Assert
    await Assert.That(async () => await dispatcher.SendAsync(command, (IMessageContext)null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // HELPER METHODS AND FAKES
  // ========================================

  private static IDispatcher _createDispatcher(IEventCompletionAwaiter? eventCompletionAwaiter = null) {
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

  // Test receptors (need to be registered via source generator)
  public class TestCommandReceptor : IReceptor<TestCommand, TestResult> {
    public ValueTask<TestResult> HandleAsync(TestCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new TestResult(Guid.NewGuid()));
    }
  }

  public class TestCommandVoidReceptor : IReceptor<TestCommand> {
    public ValueTask HandleAsync(TestCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  // Fake implementations
  private sealed class FakeEventCompletionAwaiter : IEventCompletionAwaiter {
    private readonly bool _completesImmediately;

    public bool WaitForEventsWasCalled { get; private set; }
    public IReadOnlyList<Guid>? LastEventIds { get; private set; }

    public FakeEventCompletionAwaiter(bool completesImmediately) {
      _completesImmediately = completesImmediately;
    }

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
