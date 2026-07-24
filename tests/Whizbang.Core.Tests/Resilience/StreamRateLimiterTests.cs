using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Tests for StreamRateLimiter — per-stream event rate limiting with throttle/cooldown.
/// </summary>
public class StreamRateLimiterTests {

  private static StreamRateLimiterOptions _defaultOptions() => new() {
    MaxEventsPerWindow = 5,   // Low for fast tests
    WindowDuration = TimeSpan.FromSeconds(2),
    CooldownDuration = TimeSpan.FromSeconds(1),
    StaleEntryTimeout = TimeSpan.FromSeconds(3)
  };

  // ========================================
  // Threshold behavior
  // ========================================

  [Test]
  public async Task TryAcquire_BelowThreshold_ReturnsTrueAsync() {
    var limiter = new StreamRateLimiter(_defaultOptions());
    var streamId = Guid.NewGuid();

    for (var i = 0; i < 4; i++) {
      await Assert.That(limiter.TryAcquire(streamId)).IsTrue();
    }
  }

  [Test]
  public async Task TryAcquire_AtThreshold_ReturnsTrueAsync() {
    var limiter = new StreamRateLimiter(_defaultOptions());
    var streamId = Guid.NewGuid();

    for (var i = 0; i < 5; i++) {
      await Assert.That(limiter.TryAcquire(streamId)).IsTrue();
    }
  }

  [Test]
  public async Task TryAcquire_ExceedsThreshold_ReturnsFalseAsync() {
    var limiter = new StreamRateLimiter(_defaultOptions());
    var streamId = Guid.NewGuid();

    for (var i = 0; i < 5; i++) { limiter.TryAcquire(streamId); }

    await Assert.That(limiter.TryAcquire(streamId)).IsFalse()
      .Because("6th call exceeds threshold of 5");
  }

  // ========================================
  // Cooldown behavior
  // ========================================

  [Test]
  public async Task TryAcquire_DuringCooldown_ReturnsFalseAsync() {
    var limiter = new StreamRateLimiter(_defaultOptions());
    var streamId = Guid.NewGuid();

    // Exhaust threshold
    for (var i = 0; i < 6; i++) { limiter.TryAcquire(streamId); }

    // During cooldown — should still return false
    await Assert.That(limiter.TryAcquire(streamId)).IsFalse();
    await Assert.That(limiter.TryAcquire(streamId)).IsFalse();
  }

  [Test]
  public async Task TryAcquire_AfterCooldownExpires_ReturnsTrueAsync() {
    var options = _defaultOptions();
    options.CooldownDuration = TimeSpan.FromSeconds(1);
    var limiter = new StreamRateLimiter(options);
    var streamId = Guid.NewGuid();

    for (var i = 0; i < 6; i++) { limiter.TryAcquire(streamId); }
    await Assert.That(limiter.TryAcquire(streamId)).IsFalse();

    // Wait for cooldown to expire
    await Task.Delay(1100);

    await Assert.That(limiter.TryAcquire(streamId)).IsTrue()
      .Because("Stream should resume after cooldown expires");
  }

  [Test]
  public async Task TryAcquire_AfterCooldownExpires_ResetsCountAsync() {
    var options = _defaultOptions();
    options.CooldownDuration = TimeSpan.FromSeconds(1);
    var limiter = new StreamRateLimiter(options);
    var streamId = Guid.NewGuid();

    // Hit limit → cooldown
    for (var i = 0; i < 6; i++) { limiter.TryAcquire(streamId); }
    await Task.Delay(1100);

    // After cooldown, should get another full window
    for (var i = 0; i < 5; i++) {
      await Assert.That(limiter.TryAcquire(streamId)).IsTrue();
    }

    // 6th again triggers throttle
    await Assert.That(limiter.TryAcquire(streamId)).IsFalse();
  }

  [Test]
  public async Task TryAcquire_RepeatedThreshold_RepeatedCooldownAsync() {
    var options = _defaultOptions();
    options.CooldownDuration = TimeSpan.FromSeconds(1);
    var limiter = new StreamRateLimiter(options);
    var streamId = Guid.NewGuid();

    // First cycle: hit limit → cooldown → resume
    for (var i = 0; i < 6; i++) { limiter.TryAcquire(streamId); }
    await Task.Delay(1100);
    await Assert.That(limiter.TryAcquire(streamId)).IsTrue();

    // Second cycle: hit limit again → cooldown again
    for (var i = 0; i < 5; i++) { limiter.TryAcquire(streamId); }
    await Assert.That(limiter.TryAcquire(streamId)).IsFalse()
      .Because("Throttle pattern: repeated threshold → repeated cooldown");
  }

  // ========================================
  // Stream independence
  // ========================================

  [Test]
  public async Task TryAcquire_DifferentStreams_IndependentLimitsAsync() {
    var limiter = new StreamRateLimiter(_defaultOptions());
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();

    // Exhaust stream A
    for (var i = 0; i < 6; i++) { limiter.TryAcquire(streamA); }

    // Stream B should be unaffected
    await Assert.That(limiter.TryAcquire(streamB)).IsTrue()
      .Because("Stream B should be independent of stream A's limit");
  }

  [Test]
  public async Task TryAcquire_DifferentStreams_IndependentCooldownsAsync() {
    var limiter = new StreamRateLimiter(_defaultOptions());
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();

    // Stream A in cooldown
    for (var i = 0; i < 6; i++) { limiter.TryAcquire(streamA); }
    await Assert.That(limiter.TryAcquire(streamA)).IsFalse();

    // Stream B still works
    for (var i = 0; i < 5; i++) {
      await Assert.That(limiter.TryAcquire(streamB)).IsTrue();
    }
  }

  // ========================================
  // Window behavior
  // ========================================

  [Test]
  public async Task TryAcquire_WindowExpires_ResetsCountAsync() {
    var options = _defaultOptions();
    options.WindowDuration = TimeSpan.FromSeconds(1);
    var limiter = new StreamRateLimiter(options);
    var streamId = Guid.NewGuid();

    // Use 4 out of 5
    for (var i = 0; i < 4; i++) { limiter.TryAcquire(streamId); }

    // Wait for window to expire
    await Task.Delay(1100);

    // Full quota available again
    for (var i = 0; i < 5; i++) {
      await Assert.That(limiter.TryAcquire(streamId)).IsTrue();
    }
  }

  [Test]
  public async Task TryAcquire_WindowExpires_NoCooldownAsync() {
    var options = _defaultOptions();
    options.WindowDuration = TimeSpan.FromSeconds(1);
    var limiter = new StreamRateLimiter(options);
    var streamId = Guid.NewGuid();

    // Use 4 (below threshold)
    for (var i = 0; i < 4; i++) { limiter.TryAcquire(streamId); }
    await Task.Delay(1100);

    // Natural window expiry — no cooldown triggered
    await Assert.That(limiter.TryAcquire(streamId)).IsTrue()
      .Because("Natural window expiry should not trigger cooldown");
  }

  // ========================================
  // Edge cases
  // ========================================

  [Test]
  public async Task TryAcquire_EmptyGuid_WorksAsync() {
    var limiter = new StreamRateLimiter(_defaultOptions());
    await Assert.That(limiter.TryAcquire(Guid.Empty)).IsTrue();
  }

  [Test]
  public async Task TryAcquire_ZeroCooldown_ImmediateResumeAsync() {
    var options = _defaultOptions();
    options.CooldownDuration = TimeSpan.Zero;
    var limiter = new StreamRateLimiter(options);
    var streamId = Guid.NewGuid();

    // Hit limit
    for (var i = 0; i < 6; i++) { limiter.TryAcquire(streamId); }

    // Zero cooldown — should resume immediately on next call
    await Assert.That(limiter.TryAcquire(streamId)).IsTrue()
      .Because("Zero cooldown means immediate resume");
  }

  [Test]
  public async Task TryAcquire_ConcurrentAccess_ThreadSafeAsync() {
    var limiter = new StreamRateLimiter(new StreamRateLimiterOptions { MaxEventsPerWindow = 1000 });
    var streamId = Guid.NewGuid();

    // 100 concurrent calls
    var tasks = Enumerable.Range(0, 100)
      .Select(_ => Task.Run(() => limiter.TryAcquire(streamId)))
      .ToArray();

    var results = await Task.WhenAll(tasks);

    // All should succeed (threshold is 1000)
    await Assert.That(results.All(r => r)).IsTrue()
      .Because("Concurrent calls should not corrupt state");
  }

  // ========================================
  // Options defaults
  // ========================================

  [Test]
  public async Task Options_Defaults_CorrectAsync() {
    var options = new StreamRateLimiterOptions();
    await Assert.That(options.MaxEventsPerWindow).IsEqualTo(50);
    await Assert.That(options.WindowDuration).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(options.CooldownDuration).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.StaleEntryTimeout).IsEqualTo(TimeSpan.FromMinutes(5));
  }
}
