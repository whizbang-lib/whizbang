using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="IPerspectiveSyncSignaler"/> and <see cref="LocalSyncSignaler"/>.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class PerspectiveSyncSignalerTests {
  // Dummy perspective types for testing
  private sealed class TestPerspective { }
  private sealed class PerspectiveA { }
  private sealed class PerspectiveB { }

  // ==========================================================================
  // PerspectiveCheckpointSignal record tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveCheckpointSignal_StoresAllPropertiesAsync() {
    var perspectiveType = typeof(TestPerspective);
    var streamId = Guid.NewGuid();
    var lastEventId = Guid.NewGuid();
    var timestamp = DateTimeOffset.UtcNow;

    var signal = new PerspectiveCheckpointSignal(perspectiveType, streamId, lastEventId, timestamp);

    await Assert.That(signal.PerspectiveType).IsEqualTo(perspectiveType);
    await Assert.That(signal.StreamId).IsEqualTo(streamId);
    await Assert.That(signal.LastEventId).IsEqualTo(lastEventId);
    await Assert.That(signal.Timestamp).IsEqualTo(timestamp);
  }

  [Test]
  public async Task PerspectiveCheckpointSignal_IsValueTypeAsync() {
    var signal = new PerspectiveCheckpointSignal(typeof(TestPerspective), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

    await Assert.That(signal.GetType().IsValueType).IsTrue();
  }

  // ==========================================================================
  // LocalSyncSignaler - SignalCheckpointUpdated tests
  // ==========================================================================

  [Test]
  public async Task LocalSyncSignaler_SignalCheckpointUpdated_NotifiesSubscribersAsync() {
    using var signaler = new LocalSyncSignaler();
    var perspectiveType = typeof(TestPerspective);
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    PerspectiveCheckpointSignal? receivedSignal = null;
    var signalReceived = new TaskCompletionSource<bool>();

    using var subscription = signaler.Subscribe(perspectiveType, signal => {
      receivedSignal = signal;
      signalReceived.TrySetResult(true);
    });

    signaler.SignalCheckpointUpdated(perspectiveType, streamId, eventId);

    // Wait with timeout for signal
    var received = await Task.WhenAny(signalReceived.Task, Task.Delay(1000)) == signalReceived.Task;

    await Assert.That(received).IsTrue();
    await Assert.That(receivedSignal).IsNotNull();
    await Assert.That(receivedSignal!.Value.PerspectiveType).IsEqualTo(perspectiveType);
    await Assert.That(receivedSignal!.Value.StreamId).IsEqualTo(streamId);
    await Assert.That(receivedSignal!.Value.LastEventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task LocalSyncSignaler_SignalCheckpointUpdated_OnlyNotifiesMatchingSubscribersAsync() {
    using var signaler = new LocalSyncSignaler();
    var receivedCount = 0;

    using var subscription = signaler.Subscribe(typeof(PerspectiveA), _ => {
      Interlocked.Increment(ref receivedCount);
    });

    signaler.SignalCheckpointUpdated(typeof(PerspectiveB), Guid.NewGuid(), Guid.NewGuid());

    await Task.Delay(100); // Give time for potential (incorrect) notification

    await Assert.That(receivedCount).IsEqualTo(0);
  }

  [Test]
  public async Task LocalSyncSignaler_MultipleSubscribers_AllReceiveSignalAsync() {
    using var signaler = new LocalSyncSignaler();
    var perspectiveType = typeof(TestPerspective);
    var signal1Received = new TaskCompletionSource<bool>();
    var signal2Received = new TaskCompletionSource<bool>();

    using var subscription1 = signaler.Subscribe(perspectiveType, _ => signal1Received.TrySetResult(true));
    using var subscription2 = signaler.Subscribe(perspectiveType, _ => signal2Received.TrySetResult(true));

    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());

    var received1 = await Task.WhenAny(signal1Received.Task, Task.Delay(1000)) == signal1Received.Task;
    var received2 = await Task.WhenAny(signal2Received.Task, Task.Delay(1000)) == signal2Received.Task;

    await Assert.That(received1).IsTrue();
    await Assert.That(received2).IsTrue();
  }

  // ==========================================================================
  // LocalSyncSignaler - Subscribe tests
  // ==========================================================================

  [Test]
  public async Task LocalSyncSignaler_Subscribe_ReturnsDisposableAsync() {
    using var signaler = new LocalSyncSignaler();

    var subscription = signaler.Subscribe(typeof(TestPerspective), _ => { });

    await Assert.That(subscription).IsNotNull();

    subscription.Dispose();
  }

  [Test]
  public async Task LocalSyncSignaler_DisposeSubscription_StopsReceivingSignalsAsync() {
    using var signaler = new LocalSyncSignaler();
    var perspectiveType = typeof(TestPerspective);
    var signalsReceived = 0;

    var subscription = signaler.Subscribe(perspectiveType, _ => {
      Interlocked.Increment(ref signalsReceived);
    });

    // Send first signal
    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());
    await Task.Delay(100);

    // Dispose subscription
    subscription.Dispose();

    // Send second signal
    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());
    await Task.Delay(100);

    await Assert.That(signalsReceived).IsEqualTo(1);
  }

  // ==========================================================================
  // LocalSyncSignaler - Disposal tests
  // ==========================================================================

  [Test]
  public async Task LocalSyncSignaler_Dispose_CanBeCalledMultipleTimesAsync() {
    var signaler = new LocalSyncSignaler();

    signaler.Dispose();
    signaler.Dispose(); // Should not throw

    // Verify signaler is disposed by checking that new subscriptions don't receive signals
    var received = false;
    using var subscription = signaler.Subscribe(typeof(TestPerspective), _ => received = true);
    signaler.SignalCheckpointUpdated(typeof(TestPerspective), Guid.NewGuid(), Guid.NewGuid());
    await Task.Delay(50);
    await Assert.That(received).IsFalse();
  }

  [Test]
  public async Task LocalSyncSignaler_AfterDispose_SignalingDoesNotThrowAsync() {
    var signaler = new LocalSyncSignaler();
    signaler.Dispose();

    // Should not throw, just silently do nothing
    Exception? caughtException = null;
    try {
      signaler.SignalCheckpointUpdated(typeof(TestPerspective), Guid.NewGuid(), Guid.NewGuid());
    } catch (Exception ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNull();
  }
}
