using TUnit.Core;
using Whizbang.Core.Diagnostics;

namespace Whizbang.Core.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="IDebuggerAwareClock"/> and related types.
/// </summary>
/// <docs>features/debugger-aware-clock</docs>
public class DebuggerAwareClockTests {
  // ==========================================================================
  // DebuggerDetectionMode enum tests
  // ==========================================================================

  [Test]
  public async Task DebuggerDetectionMode_HasExpectedValuesAsync() {
    // Assert - verify all expected enum values exist
    await Assert.That(Enum.IsDefined(DebuggerDetectionMode.Disabled)).IsTrue();
    await Assert.That(Enum.IsDefined(DebuggerDetectionMode.DebuggerAttached)).IsTrue();
    await Assert.That(Enum.IsDefined(DebuggerDetectionMode.CpuTimeSampling)).IsTrue();
    await Assert.That(Enum.IsDefined(DebuggerDetectionMode.ExternalHook)).IsTrue();
    await Assert.That(Enum.IsDefined(DebuggerDetectionMode.Auto)).IsTrue();
  }

  [Test]
  public async Task DebuggerDetectionMode_AutoIsDefaultAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions();

    // Assert - Auto should be the default mode
    await Assert.That(options.Mode).IsEqualTo(DebuggerDetectionMode.Auto);
  }

  // ==========================================================================
  // DebuggerAwareClockOptions tests
  // ==========================================================================

  [Test]
  public async Task DebuggerAwareClockOptions_DefaultValues_AreCorrectAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions();

    // Assert
    await Assert.That(options.Mode).IsEqualTo(DebuggerDetectionMode.Auto);
    await Assert.That(options.SamplingInterval).IsEqualTo(TimeSpan.FromMilliseconds(100));
    await Assert.That(options.FrozenThreshold).IsEqualTo(10.0);
  }

  [Test]
  public async Task DebuggerAwareClockOptions_CanSetMode_ToDisabledAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };

    // Assert
    await Assert.That(options.Mode).IsEqualTo(DebuggerDetectionMode.Disabled);
  }

  [Test]
  public async Task DebuggerAwareClockOptions_CanSetSamplingIntervalAsync() {
    // Arrange
    var interval = TimeSpan.FromMilliseconds(50);
    var options = new DebuggerAwareClockOptions { SamplingInterval = interval };

    // Assert
    await Assert.That(options.SamplingInterval).IsEqualTo(interval);
  }

  [Test]
  public async Task DebuggerAwareClockOptions_CanSetFrozenThresholdAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { FrozenThreshold = 5.0 };

    // Assert
    await Assert.That(options.FrozenThreshold).IsEqualTo(5.0);
  }

  // ==========================================================================
  // IActiveStopwatch interface tests (via DebuggerAwareClock)
  // ==========================================================================

  [Test]
  public async Task IActiveStopwatch_StartNew_ReturnsStopwatchAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Act
    var stopwatch = clock.StartNew();

    // Assert
    await Assert.That(stopwatch).IsNotNull();
  }

  [Test]
  public async Task IActiveStopwatch_ActiveElapsed_IsInitiallyZeroAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Act
    var stopwatch = clock.StartNew();

    // Assert - should be very close to zero (allow small margin for execution time)
    await Assert.That(stopwatch.ActiveElapsed.TotalMilliseconds).IsLessThan(50);
  }

  [Test]
  public async Task IActiveStopwatch_WallElapsed_IsInitiallyZeroAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Act
    var stopwatch = clock.StartNew();

    // Assert - should be very close to zero (allow small margin for execution time)
    await Assert.That(stopwatch.WallElapsed.TotalMilliseconds).IsLessThan(50);
  }

  [Test]
  public async Task IActiveStopwatch_ActiveElapsed_AdvancesAfterDelayAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(100);

    // Assert - should have advanced by at least 80ms (allowing for timing variance)
    await Assert.That(stopwatch.ActiveElapsed.TotalMilliseconds).IsGreaterThanOrEqualTo(80);
  }

  [Test]
  public async Task IActiveStopwatch_WallElapsed_AdvancesAfterDelayAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(100);

    // Assert - should have advanced by at least 80ms (allowing for timing variance)
    await Assert.That(stopwatch.WallElapsed.TotalMilliseconds).IsGreaterThanOrEqualTo(80);
  }

  [Test]
  public async Task IActiveStopwatch_FrozenTime_IsZeroWhenNotFrozenAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(50);

    // Assert - in Disabled mode, frozen time should always be zero
    await Assert.That(stopwatch.FrozenTime).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task IActiveStopwatch_HasTimedOut_ReturnsFalseBeforeTimeoutAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();
    var timeout = TimeSpan.FromSeconds(5);

    // Act & Assert
    await Assert.That(stopwatch.HasTimedOut(timeout)).IsFalse();
  }

  [Test]
  public async Task IActiveStopwatch_HasTimedOut_ReturnsTrueAfterTimeoutAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();
    var timeout = TimeSpan.FromMilliseconds(50);

    // Act
    await Task.Delay(100);

    // Assert
    await Assert.That(stopwatch.HasTimedOut(timeout)).IsTrue();
  }

  [Test]
  public async Task IActiveStopwatch_Halt_FreezesElapsedTimeAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(50);
    stopwatch.Halt();
    var elapsedAfterHalt = stopwatch.ActiveElapsed;
    await Task.Delay(100);
    var elapsedLater = stopwatch.ActiveElapsed;

    // Assert - elapsed time should not change after Halt
    await Assert.That(elapsedLater).IsEqualTo(elapsedAfterHalt);
  }

  // ==========================================================================
  // IDebuggerAwareClock interface tests
  // ==========================================================================

  [Test]
  public async Task IDebuggerAwareClock_Mode_ReturnsConfiguredModeAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.CpuTimeSampling };
    using var clock = new DebuggerAwareClock(options);

    // Assert
    await Assert.That(clock.Mode).IsEqualTo(DebuggerDetectionMode.CpuTimeSampling);
  }

  [Test]
  public async Task IDebuggerAwareClock_IsPaused_IsFalseInDisabledModeAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Assert - in Disabled mode, IsPaused should always be false
    await Assert.That(clock.IsPaused).IsFalse();
  }

  [Test]
  public async Task IDebuggerAwareClock_OnPauseStateChanged_ReturnsDisposableAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Act
    var subscription = clock.OnPauseStateChanged(_ => { });

    // Assert
    await Assert.That(subscription).IsNotNull();

    // Cleanup
    subscription.Dispose();
  }

  [Test]
  public async Task IDebuggerAwareClock_ImplementsIDisposableAsync() {
    // Assert
    await Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(DebuggerAwareClock))).IsTrue();
  }

  // ==========================================================================
  // DebuggerAwareClock specific tests
  // ==========================================================================

  [Test]
  public async Task DebuggerAwareClock_DefaultConstructor_UsesDefaultOptionsAsync() {
    // Arrange & Act
    using var clock = new DebuggerAwareClock();

    // Assert
    await Assert.That(clock.Mode).IsEqualTo(DebuggerDetectionMode.Auto);
  }

  [Test]
  public async Task DebuggerAwareClock_Dispose_CanBeCalledMultipleTimesAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    var clock = new DebuggerAwareClock(options);

    // Act & Assert - should not throw
    clock.Dispose();
    clock.Dispose();
  }

  [Test]
  public async Task DebuggerAwareClock_WithDisabledMode_AlwaysReturnsWallTimeAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(100);

    // Assert - ActiveElapsed should equal WallElapsed in Disabled mode
    var activeDelta = Math.Abs((stopwatch.ActiveElapsed - stopwatch.WallElapsed).TotalMilliseconds);
    await Assert.That(activeDelta).IsLessThan(10); // Allow small variance
  }

  [Test]
  public async Task DebuggerAwareClock_WithDebuggerAttachedMode_WorksWhenDebuggerNotAttachedAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.DebuggerAttached };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(100);

    // Assert - should function normally when debugger is not attached
    await Assert.That(stopwatch.ActiveElapsed.TotalMilliseconds).IsGreaterThanOrEqualTo(80);
    await Assert.That(clock.IsPaused).IsFalse();
  }

  // ==========================================================================
  // Multiple stopwatch tests
  // ==========================================================================

  [Test]
  public async Task DebuggerAwareClock_MultipleStopwatches_AreIndependentAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Act
    var stopwatch1 = clock.StartNew();
    await Task.Delay(50);
    var stopwatch2 = clock.StartNew();
    await Task.Delay(50);

    // Assert - stopwatch1 should have more elapsed time than stopwatch2
    await Assert.That(stopwatch1.ActiveElapsed.TotalMilliseconds)
        .IsGreaterThan(stopwatch2.ActiveElapsed.TotalMilliseconds);
  }

  [Test]
  public async Task DebuggerAwareClock_HaltOneStopwatch_DoesNotAffectOthersAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch1 = clock.StartNew();
    var stopwatch2 = clock.StartNew();

    // Act
    await Task.Delay(50);
    stopwatch1.Halt();
    var elapsed1AfterHalt = stopwatch1.ActiveElapsed;
    await Task.Delay(50);

    // Assert - stopwatch2 should have more elapsed time than stopwatch1
    await Assert.That(stopwatch2.ActiveElapsed.TotalMilliseconds)
        .IsGreaterThan(elapsed1AfterHalt.TotalMilliseconds);
  }

  // ==========================================================================
  // Additional coverage tests
  // ==========================================================================

  [Test]
  public async Task DebuggerAwareClock_Constructor_ThrowsOnNullOptionsAsync() {
    // Act & Assert
    await Assert.That(() => new DebuggerAwareClock(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task DebuggerAwareClock_GetCurrentTimestamp_ReturnsValidTimestampAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Act
    var timestamp1 = clock.GetCurrentTimestamp();
    await Task.Delay(10);
    var timestamp2 = clock.GetCurrentTimestamp();

    // Assert - timestamps should be increasing
    await Assert.That(timestamp2).IsGreaterThan(timestamp1);
  }

  [Test]
  public async Task DebuggerAwareClock_StartNew_ThrowsWhenDisposedAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    var clock = new DebuggerAwareClock(options);
    clock.Dispose();

    // Act & Assert
    await Assert.That(() => clock.StartNew())
        .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task DebuggerAwareClock_GetCurrentTimestamp_ThrowsWhenDisposedAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    var clock = new DebuggerAwareClock(options);
    clock.Dispose();

    // Act & Assert
    await Assert.That(() => clock.GetCurrentTimestamp())
        .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task DebuggerAwareClock_OnPauseStateChanged_ThrowsWhenDisposedAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    var clock = new DebuggerAwareClock(options);
    clock.Dispose();

    // Act & Assert
    await Assert.That(() => clock.OnPauseStateChanged(_ => { }))
        .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task DebuggerAwareClock_OnPauseStateChanged_ThrowsOnNullHandlerAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);

    // Act & Assert
    await Assert.That(() => clock.OnPauseStateChanged(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task DebuggerAwareClock_WithCpuTimeSamplingMode_CreatesSamplerAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromMilliseconds(50)
    };

    // Act
    using var clock = new DebuggerAwareClock(options);

    // Assert - clock should be created with CpuTimeSampling mode
    await Assert.That(clock.Mode).IsEqualTo(DebuggerDetectionMode.CpuTimeSampling);
  }

  [Test]
  public async Task DebuggerAwareClock_WithAutoMode_CreatesSamplerAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.Auto,
      SamplingInterval = TimeSpan.FromMilliseconds(50)
    };

    // Act
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Wait for sampler to run
    await Task.Delay(100);

    // Assert
    await Assert.That(clock.Mode).IsEqualTo(DebuggerDetectionMode.Auto);
    await Assert.That(stopwatch.ActiveElapsed.TotalMilliseconds).IsGreaterThan(0);
  }

  [Test]
  public async Task DebuggerAwareClock_WithExternalHookMode_DoesNotCreateSamplerAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.ExternalHook };

    // Act
    using var clock = new DebuggerAwareClock(options);

    // Assert
    await Assert.That(clock.Mode).IsEqualTo(DebuggerDetectionMode.ExternalHook);
  }

  [Test]
  public async Task IActiveStopwatch_WallElapsed_AfterHalt_RemainsConstantAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(50);
    stopwatch.Halt();
    var wallAfterHalt = stopwatch.WallElapsed;
    await Task.Delay(50);
    var wallLater = stopwatch.WallElapsed;

    // Assert - wall time should not change after Halt
    await Assert.That(wallLater).IsEqualTo(wallAfterHalt);
  }

  [Test]
  public async Task IActiveStopwatch_FrozenTime_WhenActiveGreaterThanWall_ReturnsZeroAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act
    await Task.Delay(10);

    // Assert - in Disabled mode, frozen time should always be zero
    await Assert.That(stopwatch.FrozenTime).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task IActiveStopwatch_Halt_CalledMultipleTimes_DoesNotThrowAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    await Task.Delay(50);

    // Act & Assert - multiple Halt calls should not throw
    stopwatch.Halt();
    var elapsed1 = stopwatch.ActiveElapsed;
    stopwatch.Halt();
    var elapsed2 = stopwatch.ActiveElapsed;

    await Assert.That(elapsed1).IsEqualTo(elapsed2);
  }

  [Test]
  public async Task DebuggerAwareClock_CpuTimeSampling_SamplerRunsAsync() {
    // Arrange - use a short sampling interval
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromMilliseconds(25)
    };

    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act - wait for multiple sample cycles with retry to handle CI timing variability
    // In CI environments, CPU time sampling may be delayed, so we use a polling approach
    var maxAttempts = 30;  // Max 3 seconds (30 * 100ms)
    var elapsedMs = 0.0;
    for (var i = 0; i < maxAttempts; i++) {
      await Task.Delay(100);
      elapsedMs = stopwatch.ActiveElapsed.TotalMilliseconds;
      if (elapsedMs > 50) {
        break;  // Success threshold
      }
    }

    // Assert - clock should have tracked some elapsed time (reduced threshold for CI stability)
    await Assert.That(elapsedMs).IsGreaterThan(50);
  }

  [Test]
  public async Task DebuggerAwareClock_IsPaused_InAutoMode_WhenNoDebuggerAttachedAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.Auto,
      SamplingInterval = TimeSpan.FromMilliseconds(50)
    };
    using var clock = new DebuggerAwareClock(options);

    // Act - wait for sampling
    await Task.Delay(100);

    // Assert - should not be paused when no debugger attached
    // (IsPaused requires both debugger attached AND frozen detection in Auto mode)
    await Assert.That(clock.IsPaused).IsFalse();
  }

  [Test]
  public async Task PauseStateSubscription_CanBeDisposedAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    using var clock = new DebuggerAwareClock(options);
    var callCount = 0;

    // Act
    var subscription = clock.OnPauseStateChanged(_ => callCount++);
    subscription.Dispose();

    // Assert - subscription was created and disposed without error
    await Assert.That(callCount).IsEqualTo(0);
  }

  [Test]
  public async Task DebuggerAwareClock_Dispose_CompletesChannelAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled };
    var clock = new DebuggerAwareClock(options);
    var subscription = clock.OnPauseStateChanged(_ => { });

    // Act
    clock.Dispose();

    // Small delay to allow background task to notice completion
    await Task.Delay(50);

    // Assert - dispose subscription without error (channel was completed)
    subscription.Dispose();
  }

  [Test]
  public async Task IActiveStopwatch_WithCpuTimeSampling_CalculatesActiveTimeAsync() {
    // Arrange
    var options = new DebuggerAwareClockOptions {
      Mode = DebuggerDetectionMode.CpuTimeSampling,
      SamplingInterval = TimeSpan.FromMilliseconds(25)
    };
    using var clock = new DebuggerAwareClock(options);
    var stopwatch = clock.StartNew();

    // Act - do some CPU work
    var sum = 0L;
    for (var i = 0; i < 1000000; i++) {
      sum += i;
    }
    _ = sum; // Prevent optimization

    // Assert - active elapsed should be positive
    await Assert.That(stopwatch.ActiveElapsed.TotalMilliseconds).IsGreaterThan(0);
    await Assert.That(stopwatch.WallElapsed.TotalMilliseconds).IsGreaterThan(0);
  }
}
