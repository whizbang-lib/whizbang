using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Resilience;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Tests for ResilientDatabaseReadinessCheck — a decorator that wraps IDatabaseReadinessCheck
/// with a CircuitBreaker to prevent connection storms during sustained outages.
/// </summary>
public class ResilientDatabaseReadinessCheckTests {

  [Test]
  public async Task IsReadyAsync_DatabaseReady_ReturnsTrueAsync() {
    var inner = new TestReadinessCheck(isReady: true);
    var sut = new ResilientDatabaseReadinessCheck(inner);

    var result = await sut.IsReadyAsync();
    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task IsReadyAsync_DatabaseNotReady_ReturnsFalseAsync() {
    var inner = new TestReadinessCheck(isReady: false);
    var sut = new ResilientDatabaseReadinessCheck(inner);

    var result = await sut.IsReadyAsync();
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task IsReadyAsync_RepeatedFailures_OpensCircuitAsync() {
    var inner = new TestReadinessCheck(isReady: false);
    var options = new CircuitBreakerOptions { FailureThreshold = 3, InitialCooldownSeconds = 1 };
    var sut = new ResilientDatabaseReadinessCheck(inner, options);

    // Trigger failures to open circuit
    for (var i = 0; i < 3; i++) {
      await sut.IsReadyAsync();
    }

    // Circuit should be open — inner check should NOT be called
    inner.CallCount = 0;
    await sut.IsReadyAsync();

    await Assert.That(inner.CallCount).IsEqualTo(0)
      .Because("Circuit is open — inner check should be skipped");
  }

  [Test]
  public async Task IsReadyAsync_CircuitOpens_ThenRecovers_ReturnsTrueAsync() {
    var inner = new TestReadinessCheck(isReady: false);
    var options = new CircuitBreakerOptions {
      FailureThreshold = 2,
      InitialCooldownSeconds = 1,
      SuccessCacheDurationSeconds = 0
    };
    var sut = new ResilientDatabaseReadinessCheck(inner, options);

    // Trip the circuit
    await sut.IsReadyAsync();
    await sut.IsReadyAsync();

    // Wait for cooldown, then fix the database
    await Task.Delay(1100);
    inner.IsReady = true;

    var result = await sut.IsReadyAsync();
    await Assert.That(result).IsTrue()
      .Because("Database recovered after circuit cooldown");
  }

  [Test]
  public async Task IsReadyAsync_SuccessCached_SkipsInnerCheckAsync() {
    var inner = new TestReadinessCheck(isReady: true);
    var options = new CircuitBreakerOptions { SuccessCacheDurationSeconds = 2 };
    var sut = new ResilientDatabaseReadinessCheck(inner, options);

    await sut.IsReadyAsync();
    await sut.IsReadyAsync();
    await sut.IsReadyAsync();

    await Assert.That(inner.CallCount).IsEqualTo(1)
      .Because("Subsequent calls should use cached result");
  }

  [Test]
  public async Task IsReadyAsync_InnerThrowsException_ReturnsFalseAsync() {
    var inner = new ThrowingReadinessCheck();
    var sut = new ResilientDatabaseReadinessCheck(inner);

    var result = await sut.IsReadyAsync();
    await Assert.That(result).IsFalse()
      .Because("Exception should be caught by circuit breaker and return false");
  }

  private sealed class TestReadinessCheck(bool isReady) : IDatabaseReadinessCheck {
    public bool IsReady { get; set; } = isReady;
    public int CallCount { get; set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      CallCount++;
      return IsReady ? Task.FromResult(true) : throw new InvalidOperationException("Database unreachable");
    }
  }

  private sealed class ThrowingReadinessCheck : IDatabaseReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      throw new TimeoutException("Connection timed out");
  }
}
