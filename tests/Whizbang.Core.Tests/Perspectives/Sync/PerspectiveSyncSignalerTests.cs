using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Testing.Async;

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

    // Wait for signal with proper timeout (throws TimeoutException if not received)
    await signalReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

    // Assert that no signal is received (more reliable than Task.Delay + assert)
    await AsyncTestHelpers.AssertNeverAsync(
      () => receivedCount > 0,
      TimeSpan.FromMilliseconds(200),
      failureMessage: "PerspectiveA subscriber should not receive signals for PerspectiveB");
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

    // Wait for both signals with proper timeout
    await signal1Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await signal2Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    // Send first signal and wait for it to be processed
    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());
    await AsyncTestHelpers.WaitForConditionAsync(
      () => signalsReceived >= 1,
      TimeSpan.FromSeconds(5),
      timeoutMessage: "First signal was not received");

    // Dispose subscription
    subscription.Dispose();

    // Send second signal
    signaler.SignalCheckpointUpdated(perspectiveType, Guid.NewGuid(), Guid.NewGuid());

    // Assert that no additional signal is received after disposal
    await AsyncTestHelpers.AssertNeverAsync(
      () => signalsReceived > 1,
      TimeSpan.FromMilliseconds(200),
      failureMessage: "Signal received after subscription disposal");

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

    // Assert that no signal is received on disposed signaler
    await AsyncTestHelpers.AssertNeverAsync(
      () => received,
      TimeSpan.FromMilliseconds(100),
      failureMessage: "Signal received on disposed signaler");
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
