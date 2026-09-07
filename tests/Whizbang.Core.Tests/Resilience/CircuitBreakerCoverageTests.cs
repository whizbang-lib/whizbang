using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Targeted coverage for two <see cref="CircuitBreaker{TResult}"/> branches the broader
/// <see cref="CircuitBreakerTests"/> suite never reaches: the in-lock cache re-check that protects
/// a caller who loses the fast-path race, and the "only one probe allowed" fallback for a caller
/// that finds the breaker already stuck mid-probe. Both are deterministic — driven by
/// <see cref="TaskCompletionSource"/> signals and a zero cooldown, never a sleep.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Resilience/CircuitBreaker.cs</code-under-test>
public class CircuitBreakerCoverageTests {

  private static CircuitBreakerOptions _defaultOptions() => new() {
    FailureThreshold = 1,
    InitialCooldownSeconds = 0,
    CooldownBackoffMultiplier = 2.0,
    MaxCooldownSeconds = 10,
    SuccessCacheDurationSeconds = 60,
  };

  [Test]
  public async Task ExecuteAsync_ConcurrentCallLosesFastPathButWinsTheLock_ReturnsFreshCacheInsteadOfReexecutingAsync() {
    // If a caller that loses the outer (lock-free) cache check went on to re-invoke the operation
    // just because a sibling call's success landed a few microseconds too late for it to see, every
    // burst of concurrent callers on a just-recovered dependency would pile up redundant calls
    // instead of the one the circuit breaker exists to collapse them into.
    var options = _defaultOptions();
    var breaker = new CircuitBreaker<int>(options);

    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var firstTask = breaker.ExecuteAsync(async ct => {
      firstStarted.SetResult();
      await releaseFirst.Task;
      return 42;
    }, fallbackValue: -1, CancellationToken.None);

    await firstStarted.Task; // first call now holds the internal lock, mid-operation

    var secondCallCount = 0;
    // Starts while _lastSuccessAt is still null (outer fast-path check misses), then blocks
    // acquiring the lock the first call is holding.
    var secondTask = breaker.ExecuteAsync(_ => {
      secondCallCount++;
      return Task.FromResult(999);
    }, fallbackValue: -2, CancellationToken.None);

    releaseFirst.SetResult(); // first call succeeds, populates the cache, releases the lock
    var firstResult = await firstTask;
    var secondResult = await secondTask;

    await Assert.That(firstResult).IsEqualTo(42);
    await Assert.That(secondResult).IsEqualTo(42)
      .Because("once the second caller acquires the lock, the freshly-populated cache from the first call's success must win");
    await Assert.That(secondCallCount).IsEqualTo(0)
      .Because("the second caller must never redundantly re-invoke the operation once a fresh success is visible inside the lock");
  }

  // CA2201 forbids throwing a reserved exception type, and normally it is right. Here the
  // production filter is literally `catch (Exception ex) when (ex is not OutOfMemoryException)`,
  // so OutOfMemoryException is the ONLY exception that escapes the breaker — testing that
  // carve-out requires throwing exactly it. A substitute type gets caught and the test proves
  // nothing (which is what happened when one was tried).
#pragma warning disable CA2201 // deliberately throwing the one type the production filter excludes
  [Test]
  public async Task ExecuteAsync_BreakerStuckHalfOpenAfterUncaughtCatastrophicFailure_SubsequentCallFailsFastWithoutProbingAgainAsync() {
    // OutOfMemoryException is deliberately excluded from the breaker's catch filter (never swallow
    // a catastrophic failure), which means a probe that throws one leaves the breaker permanently
    // stuck in HalfOpen — the state transition to Closed/Open never runs. If a later caller treated
    // that as "safe to probe again", every subsequent call would launch another probe against a
    // dependency that just failed catastrophically instead of failing fast.
    var options = _defaultOptions();
    var breaker = new CircuitBreaker<string>(options);

    // Trip the circuit, then let the (zero) cooldown expire immediately so the very next call
    // transitions Open -> HalfOpen and runs the probe.
    await breaker.ExecuteAsync(_ => throw new InvalidOperationException("fail"), fallbackValue: "fallback", CancellationToken.None);
    await Assert.That(breaker.State).IsEqualTo(CircuitBreakerState.Open);

    await Assert.That(async () => {
      await breaker.ExecuteAsync(
        _ => throw new OutOfMemoryException("simulated catastrophic failure"),
        fallbackValue: "fallback",
        CancellationToken.None);
    }).ThrowsExactly<OutOfMemoryException>();

    await Assert.That(breaker.State).IsEqualTo(CircuitBreakerState.HalfOpen)
      .Because("the uncaught OOM bypassed both _onSuccess and _onFailure, so the state transition out of HalfOpen never ran");

    var secondCallCount = 0;
    var result = await breaker.ExecuteAsync(_ => {
      secondCallCount++;
      return Task.FromResult("real");
    }, fallbackValue: "fallback", CancellationToken.None);

    await Assert.That(result).IsEqualTo("fallback")
      .Because("only one probe is allowed at a time; a breaker stuck mid-probe must fail fast, not launch another");
    await Assert.That(secondCallCount).IsEqualTo(0)
      .Because("the operation must not run again while the breaker is stuck in HalfOpen");
  }

}
#pragma warning restore CA2201
