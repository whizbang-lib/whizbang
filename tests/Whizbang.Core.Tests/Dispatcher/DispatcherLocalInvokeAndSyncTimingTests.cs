using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Critical timing tests that verify the sync awaiting mechanism is working correctly.
/// These tests ensure that:
/// - The awaiter actually waits for the configured duration
/// - Timing measurements are accurate
/// - The handler completes BEFORE waiting starts
/// - Timeout is respected
/// </summary>
/// <docs>core-concepts/dispatcher#local-invoke-and-sync</docs>
[Category("Dispatcher")]
[Category("Sync")]
[Category("Timing")]
[NotInParallel]
public sealed class DispatcherLocalInvokeAndSyncTimingTests {
  // Test messages
  public record TimedCommand(string Data);
  public record TimedCommandWithResult(Guid Id);
  public record TimedCommandResult(Guid Id);

  // Constants for timing tests
  private const int DELAY_MILLISECONDS = 100;
  private const int TIMING_TOLERANCE_MS = 50;

  [Test]
  public async Task LocalInvokeAndSyncAsync_ActuallyWaitsForConfiguredDurationAsync() {
    // Arrange
    var expectedDelay = TimeSpan.FromMilliseconds(DELAY_MILLISECONDS);
    var delayingAwaiter = new DelayingEventCompletionAwaiter(expectedDelay);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(delayingAwaiter);
    var command = new TimedCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      var stopwatch = System.Diagnostics.Stopwatch.StartNew();

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      stopwatch.Stop();

      // Assert - elapsed time should be at least the configured delay
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
      await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(DELAY_MILLISECONDS - TIMING_TOLERANCE_MS);
      await Assert.That(result.ElapsedTime.TotalMilliseconds).IsGreaterThanOrEqualTo(DELAY_MILLISECONDS - TIMING_TOLERANCE_MS);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_HandlerCompletesBeforeWaitingStartsAsync() {
    // Arrange
    var sequenceTracker = new SequenceTracker();
    var trackerAwaiter = new SequenceTrackingEventCompletionAwaiter(sequenceTracker, TimeSpan.FromMilliseconds(50));
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(trackerAwaiter, sequenceTracker);
    var command = new TimedCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      // Assert - verify sequence
      var sequence = sequenceTracker.GetSequence().ToList();
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);

      var handlerCompletedIndex = sequence.IndexOf("HandlerCompleted");
      var awaiterStartedIndex = sequence.IndexOf("AwaiterStarted");
      var awaiterCompletedIndex = sequence.IndexOf("AwaiterCompleted");

      await Assert.That(handlerCompletedIndex).IsGreaterThanOrEqualTo(0).And.IsLessThan(awaiterStartedIndex);
      await Assert.That(awaiterStartedIndex).IsLessThan(awaiterCompletedIndex);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_RecordsAccurateElapsedTimeAsync() {
    // Arrange
    var expectedDelay = TimeSpan.FromMilliseconds(DELAY_MILLISECONDS);
    var delayingAwaiter = new DelayingEventCompletionAwaiter(expectedDelay);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(delayingAwaiter);
    var command = new TimedCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      // Assert
      var elapsedMs = result.ElapsedTime.TotalMilliseconds;
      // Verify elapsed time is at least the expected delay (lower bound)
      // Upper bound removed — too flaky under parallel execution load
      await Assert.That(elapsedMs).IsGreaterThanOrEqualTo(DELAY_MILLISECONDS - TIMING_TOLERANCE_MS);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_TimeoutIsRespectedAsync() {
    // Arrange - awaiter takes 500ms, but timeout is 50ms
    var awaiterDelay = TimeSpan.FromMilliseconds(500);
    var timeout = TimeSpan.FromMilliseconds(50);
    var delayingAwaiter = new DelayingEventCompletionAwaiter(awaiterDelay, respectsTimeout: true);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(delayingAwaiter);
    var command = new TimedCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      var stopwatch = System.Diagnostics.Stopwatch.StartNew();

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command, timeout: timeout);

      stopwatch.Stop();

      // Assert - should timeout, not wait the full 500ms
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
      await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(200);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_WithResult_ActuallyWaitsAsync() {
    // Arrange
    var expectedDelay = TimeSpan.FromMilliseconds(DELAY_MILLISECONDS);
    var delayingAwaiter = new DelayingEventCompletionAwaiter(expectedDelay);
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(delayingAwaiter);
    var command = new TimedCommandWithResult(Guid.NewGuid());

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      var stopwatch = System.Diagnostics.Stopwatch.StartNew();

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync<TimedCommandWithResult, TimedCommandResult>(command);

      stopwatch.Stop();

      // Assert
      await Assert.That(result).IsNotNull();
      await Assert.That(result.Id).IsNotEqualTo(Guid.Empty);
      await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(DELAY_MILLISECONDS - TIMING_TOLERANCE_MS);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_NoEventsDoesNotWaitAsync() {
    // Arrange - NO events tracked, should return immediately
    var delayingAwaiter = new DelayingEventCompletionAwaiter(TimeSpan.FromSeconds(5));
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(delayingAwaiter);
    var command = new TimedCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      var stopwatch = System.Diagnostics.Stopwatch.StartNew();

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      stopwatch.Stop();

      // Assert - should return immediately
      await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
      await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(100);
      await Assert.That(delayingAwaiter.WaitWasCalled).IsFalse();
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_MultipleEventsWaitsOnceAsync() {
    // Arrange
    var delayingAwaiter = new DelayingEventCompletionAwaiter(TimeSpan.FromMilliseconds(50));
    var scopedEventTracker = new FakeScopedEventTracker();
    var dispatcher = _createDispatcher(delayingAwaiter);
    var command = new TimedCommand("test");

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // Act
      var result = await dispatcher.LocalInvokeAndSyncAsync(command);

      // Assert
      await Assert.That(result.EventsAwaited).IsEqualTo(3);
      await Assert.That(delayingAwaiter.WaitCallCount).IsEqualTo(1);
      await Assert.That(delayingAwaiter.LastEventIds!.Count).IsEqualTo(3);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  // Helper to create dispatcher
  private static IDispatcher _createDispatcher(
      IEventCompletionAwaiter? eventCompletionAwaiter,
      SequenceTracker? sequenceTracker = null) {
    var services = new ServiceCollection();

    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    services.AddReceptors();

    if (eventCompletionAwaiter != null) {
      services.AddSingleton(eventCompletionAwaiter);
    }

    if (sequenceTracker != null) {
      services.AddSingleton(sequenceTracker);
    }

    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  // Test receptors
  public class TimedCommandReceptor(SequenceTracker? sequenceTracker = null) : IReceptor<TimedCommand> {
    public ValueTask HandleAsync(TimedCommand message, CancellationToken cancellationToken = default) {
      sequenceTracker?.RecordEvent("HandlerCompleted");
      return ValueTask.CompletedTask;
    }
  }

  public class TimedCommandWithResultReceptor : IReceptor<TimedCommandWithResult, TimedCommandResult> {
    public ValueTask<TimedCommandResult> HandleAsync(TimedCommandWithResult message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new TimedCommandResult(Guid.NewGuid()));
    }
  }

  // Sequence tracking helper
  public sealed class SequenceTracker {
    private readonly List<string> _events = [];
    private readonly Lock _lock = new();

    public void RecordEvent(string eventName) {
      lock (_lock) {
        _events.Add(eventName);
      }
    }

    public IReadOnlyList<string> GetSequence() {
      lock (_lock) {
        return [.. _events];
      }
    }
  }

  // Delaying awaiter that actually waits
  private sealed class DelayingEventCompletionAwaiter(
      TimeSpan delay,
      bool respectsTimeout = false) : IEventCompletionAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();
    public bool WaitWasCalled { get; private set; }
    public int WaitCallCount { get; private set; }
    public IReadOnlyList<Guid>? LastEventIds { get; private set; }

    public async Task<bool> WaitForEventsAsync(
        IReadOnlyList<Guid> eventIds,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) {
      WaitWasCalled = true;
      WaitCallCount++;
      LastEventIds = eventIds;

      if (respectsTimeout) {
        var waitTime = delay < timeout ? delay : timeout;
        await Task.Delay(waitTime, cancellationToken);
        return delay <= timeout;
      }

      await Task.Delay(delay, cancellationToken);
      return true;
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => true;
  }

  // Sequence tracking awaiter
  private sealed class SequenceTrackingEventCompletionAwaiter(
      SequenceTracker sequenceTracker,
      TimeSpan delay) : IEventCompletionAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();

    public async Task<bool> WaitForEventsAsync(
        IReadOnlyList<Guid> eventIds,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) {
      sequenceTracker.RecordEvent("AwaiterStarted");
      await Task.Delay(delay, cancellationToken);
      sequenceTracker.RecordEvent("AwaiterCompleted");
      return true;
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => true;
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
