using Whizbang.LanguageServer.Debugging;

namespace Whizbang.LanguageServer.Tests.Debug;

public class DebugSessionManagerTests {
  [Test]
  public async Task IsPaused_Initially_ReturnsFalseAsync() {
    // Arrange
    using var manager = new DebugSessionManager();

    // Act & Assert
    await Assert.That(manager.IsPaused).IsFalse();
  }

  [Test]
  public async Task NotifyPaused_SetsPausedStateAsync() {
    // Arrange
    using var manager = new DebugSessionManager();

    // Act
    manager.NotifyPaused();

    // Assert
    await Assert.That(manager.IsPaused).IsTrue();
  }

  [Test]
  public async Task NotifyResumed_ClearsPausedStateAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    manager.NotifyPaused();

    // Act
    manager.NotifyResumed();

    // Assert
    await Assert.That(manager.IsPaused).IsFalse();
  }

  [Test]
  public async Task NotifyPaused_EmitsPausedEventAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    var tcs = new TaskCompletionSource<bool>();
    manager.OnPaused += () => tcs.TrySetResult(true);

    // Act
    manager.NotifyPaused();

    // Assert
    var fired = await tcs.Task;
    await Assert.That(fired).IsTrue();
  }

  [Test]
  public async Task NotifyResumed_EmitsResumedEventAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    manager.NotifyPaused();

    var tcs = new TaskCompletionSource<bool>();
    manager.OnResumed += () => tcs.TrySetResult(true);

    // Act
    manager.NotifyResumed();

    // Assert
    var fired = await tcs.Task;
    await Assert.That(fired).IsTrue();
  }

  [Test]
  public async Task PauseDuration_ReturnsNonNegativeTimeSpanSincePauseAsync() {
    // Arrange
    using var manager = new DebugSessionManager();

    // Act
    manager.NotifyPaused();
    var duration = manager.CurrentPauseDuration;

    // Assert — just verify it's non-negative (no Task.Delay)
    await Assert.That(duration).IsGreaterThanOrEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task NotifyPaused_MultipleTimes_OnlyCountsOnceAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    var fireCount = 0;
    manager.OnPaused += () => Interlocked.Increment(ref fireCount);

    // Act
    manager.NotifyPaused();
    manager.NotifyPaused();
    manager.NotifyPaused();

    // Assert — still paused, event only fired once, pause count is 1
    await Assert.That(manager.IsPaused).IsTrue();
    await Assert.That(fireCount).IsEqualTo(1);
    await Assert.That(manager.PauseCount).IsEqualTo(1);
  }

  [Test]
  public async Task NotifyResumed_WithoutPause_NoOpAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    var fireCount = 0;
    manager.OnResumed += () => Interlocked.Increment(ref fireCount);

    // Act — resume without ever pausing
    manager.NotifyResumed();

    // Assert
    await Assert.That(manager.IsPaused).IsFalse();
    await Assert.That(fireCount).IsEqualTo(0);
  }

  [Test]
  public async Task TotalPausedTime_AccumulatesAcrossPausesAsync() {
    // Arrange
    using var manager = new DebugSessionManager();

    // Act — two pause/resume cycles
    manager.NotifyPaused();
    manager.NotifyResumed();

    manager.NotifyPaused();
    manager.NotifyResumed();

    // Assert — total paused time is non-negative and pause count is 2
    await Assert.That(manager.TotalPausedTime).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    await Assert.That(manager.PauseCount).IsEqualTo(2);
  }

  [Test]
  public async Task SessionStats_ReturnsCorrectCountsAsync() {
    // Arrange
    using var manager = new DebugSessionManager();

    // Act — one complete cycle, then pause again
    manager.NotifyPaused();
    manager.NotifyResumed();
    manager.NotifyPaused();

    var stats = manager.GetStats();

    // Assert
    await Assert.That(stats.IsPaused).IsTrue();
    await Assert.That(stats.PauseCount).IsEqualTo(2);
    await Assert.That(stats.TotalPausedTime).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    await Assert.That(stats.CurrentPauseDuration).IsGreaterThanOrEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task CurrentPauseDuration_WhenNotPaused_ReturnsZeroAsync() {
    // Arrange
    using var manager = new DebugSessionManager();

    // Act & Assert
    await Assert.That(manager.CurrentPauseDuration).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task Dispose_ClearsEventHandlersAsync() {
    // Arrange
    var manager = new DebugSessionManager();
    var fireCount = 0;
    manager.OnPaused += () => Interlocked.Increment(ref fireCount);
    manager.OnResumed += () => Interlocked.Increment(ref fireCount);

    // Act
    manager.Dispose();

    // After dispose, calling methods should not fire events
    manager.NotifyPaused();
    manager.NotifyResumed();

    // Assert
    await Assert.That(fireCount).IsEqualTo(0);
  }
}
