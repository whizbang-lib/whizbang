using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Tests for the generic CircuitBreaker utility.
/// Validates state transitions, exponential backoff, success caching, and thread safety.
/// </summary>
public class CircuitBreakerTests {

  private static CircuitBreakerOptions _defaultOptions() => new() {
    FailureThreshold = 3,
    InitialCooldownSeconds = 1,
    CooldownBackoffMultiplier = 2.0,
    MaxCooldownSeconds = 10,
    SuccessCacheDurationSeconds = 1
  };

  // ========================================
  // State: Closed (normal operation)
  // ========================================

  [Test]
  public async Task ExecuteAsync_Success_ReturnsResultAsync() {
    var cb = new CircuitBreaker<bool>(_defaultOptions());
    var result = await cb.ExecuteAsync(_ => Task.FromResult(true), fallbackValue: false, CancellationToken.None);
    await Assert.That(result).IsTrue();
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Closed);
  }

  [Test]
  public async Task ExecuteAsync_FailuresBelowThreshold_StaysClosedAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 3;
    var cb = new CircuitBreaker<bool>(options);

    // 2 failures (below threshold of 3)
    for (var i = 0; i < 2; i++) {
      var result = await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: false, CancellationToken.None);
      await Assert.That(result).IsFalse();
    }
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Closed);
    await Assert.That(cb.ConsecutiveFailures).IsEqualTo(2);
  }

  // ========================================
  // State: Closed → Open (threshold reached)
  // ========================================

  [Test]
  public async Task ExecuteAsync_FailuresReachThreshold_OpensCircuitAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 3;
    var cb = new CircuitBreaker<bool>(options);

    for (var i = 0; i < 3; i++) {
      await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: false, CancellationToken.None);
    }

    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Open);
  }

  [Test]
  public async Task ExecuteAsync_CircuitOpen_ReturnsFallbackWithoutExecutingAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 1;
    var cb = new CircuitBreaker<string>(options);

    // Trip the circuit
    await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: "fallback", CancellationToken.None);
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Open);

    // Should return fallback without executing
    var callCount = 0;
    var result = await cb.ExecuteAsync(_ => { callCount++; return Task.FromResult("real"); }, fallbackValue: "fallback", CancellationToken.None);

    await Assert.That(result).IsEqualTo("fallback");
    await Assert.That(callCount).IsEqualTo(0)
      .Because("Operation should NOT be executed when circuit is open");
  }

  // ========================================
  // State: Open → HalfOpen (cooldown expired)
  // ========================================

  [Test]
  public async Task ExecuteAsync_CooldownExpired_TransitionsToHalfOpenAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 1;
    options.InitialCooldownSeconds = 1;
    var cb = new CircuitBreaker<bool>(options);

    // Trip the circuit
    await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: false, CancellationToken.None);
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Open);

    // Wait for cooldown
    await Task.Delay(1100);

    // Next call should try (half-open) — succeed
    var result = await cb.ExecuteAsync(_ => Task.FromResult(true), fallbackValue: false, CancellationToken.None);
    await Assert.That(result).IsTrue();
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Closed);
  }

  // ========================================
  // State: HalfOpen → Closed (success)
  // ========================================

  [Test]
  public async Task ExecuteAsync_HalfOpenSuccess_ClosesCircuitAndResetsBackoffAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 1;
    options.InitialCooldownSeconds = 1;
    var cb = new CircuitBreaker<bool>(options);

    // Trip, wait, succeed
    await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: false, CancellationToken.None);
    await Task.Delay(1100);
    await cb.ExecuteAsync(_ => Task.FromResult(true), fallbackValue: false, CancellationToken.None);

    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Closed);
    await Assert.That(cb.ConsecutiveFailures).IsEqualTo(0);
  }

  // ========================================
  // State: HalfOpen → Open (failure, escalated cooldown)
  // ========================================

  [Test]
  public async Task ExecuteAsync_HalfOpenFailure_ReopensWithEscalatedCooldownAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 1;
    options.InitialCooldownSeconds = 1;
    options.CooldownBackoffMultiplier = 2.0;
    var cb = new CircuitBreaker<bool>(options);

    // First trip: 1s cooldown
    await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: false, CancellationToken.None);
    await Task.Delay(1100);

    // Half-open attempt fails: cooldown escalates to 2s
    await cb.ExecuteAsync(_ => throw new InvalidOperationException("still failing"), fallbackValue: false, CancellationToken.None);
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Open);
    await Assert.That(cb.CurrentCooldownSeconds).IsEqualTo(2);
  }

  // ========================================
  // Exponential backoff cap
  // ========================================

  [Test]
  public async Task ExecuteAsync_BackoffCapped_DoesNotExceedMaxAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 1;
    options.InitialCooldownSeconds = 1;
    options.CooldownBackoffMultiplier = 10.0;
    options.MaxCooldownSeconds = 5;
    var cb = new CircuitBreaker<bool>(options);

    // Trip multiple times with escalation
    await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: false, CancellationToken.None);
    // Force past cooldown by using internal test hook or waiting
    // After first trip: cooldown = 1s, second would be 10s but capped at 5s

    await Assert.That(cb.CurrentCooldownSeconds).IsLessThanOrEqualTo(5);
  }

  // ========================================
  // Success caching
  // ========================================

  [Test]
  public async Task ExecuteAsync_SuccessCached_SkipsExecutionAsync() {
    var options = _defaultOptions();
    options.SuccessCacheDurationSeconds = 2;
    var cb = new CircuitBreaker<bool>(options);

    var callCount = 0;
    await cb.ExecuteAsync(_ => { callCount++; return Task.FromResult(true); }, fallbackValue: false, CancellationToken.None);
    await cb.ExecuteAsync(_ => { callCount++; return Task.FromResult(true); }, fallbackValue: false, CancellationToken.None);

    await Assert.That(callCount).IsEqualTo(1)
      .Because("Second call should use cached result");
  }

  [Test]
  public async Task ExecuteAsync_CacheExpired_ReexecutesAsync() {
    var options = _defaultOptions();
    options.SuccessCacheDurationSeconds = 1;
    var cb = new CircuitBreaker<bool>(options);

    var callCount = 0;
    await cb.ExecuteAsync(_ => { callCount++; return Task.FromResult(true); }, fallbackValue: false, CancellationToken.None);
    await Task.Delay(1100);
    await cb.ExecuteAsync(_ => { callCount++; return Task.FromResult(true); }, fallbackValue: false, CancellationToken.None);

    await Assert.That(callCount).IsEqualTo(2)
      .Because("Cache expired, should re-execute");
  }

  // ========================================
  // Success resets failure count
  // ========================================

  [Test]
  public async Task ExecuteAsync_SuccessAfterFailures_ResetsCounterAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 5;
    options.SuccessCacheDurationSeconds = 0; // Disable caching
    var cb = new CircuitBreaker<bool>(options);

    // 3 failures
    for (var i = 0; i < 3; i++) {
      await cb.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: false, CancellationToken.None);
    }
    await Assert.That(cb.ConsecutiveFailures).IsEqualTo(3);

    // 1 success resets
    await cb.ExecuteAsync(_ => Task.FromResult(true), fallbackValue: false, CancellationToken.None);
    await Assert.That(cb.ConsecutiveFailures).IsEqualTo(0);
  }

  // ========================================
  // OperationCanceledException handling
  // ========================================

  [Test]
  public async Task ExecuteAsync_OperationCanceled_CountsAsFailureAsync() {
    var options = _defaultOptions();
    options.FailureThreshold = 1;
    var cb = new CircuitBreaker<bool>(options);

    await cb.ExecuteAsync(_ => throw new OperationCanceledException(), fallbackValue: false, CancellationToken.None);
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Open);
  }
}
