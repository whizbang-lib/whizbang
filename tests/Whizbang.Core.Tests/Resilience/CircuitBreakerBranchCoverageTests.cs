// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Branch backfill for <see cref="CircuitBreaker{TResult}"/>: the half-open arm, reached when a
/// half-open probe escapes with an exception the breaker deliberately does not catch, which leaves
/// the circuit half-open for the next caller.
/// </summary>
public class CircuitBreakerBranchCoverageTests {

  [Test]
  public async Task ExecuteAsync_WhileHalfOpenAfterAnEscapedProbe_ReturnsFallbackWithoutCallingTheOperationAsync() {
    var cb = new CircuitBreaker<int>(
      options: new CircuitBreakerOptions {
        FailureThreshold = 1,
        InitialCooldownSeconds = 0, // the cooldown is over the moment the circuit opens
        CooldownBackoffMultiplier = 2.0,
        MaxCooldownSeconds = 10,
        SuccessCacheDurationSeconds = 0,
      },
      logger: NullLogger.Instance);

    // 1. One failure opens the circuit.
    _ = await cb.ExecuteAsync(_ => throw new InvalidOperationException("down"), fallbackValue: -1, CancellationToken.None);
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.Open);

    // 2. The next call finds the cooldown expired, goes half-open and probes. The probe throws an
    //    exception the breaker's filter lets escape, so neither the success nor the failure
    //    transition runs and the circuit is left half-open.
#pragma warning disable CA2201, S112 // The breaker's catch filter excludes exactly this reserved type; it is the case under test.
    await Assert.That(async () => await cb.ExecuteAsync(_ => throw new OutOfMemoryException("probe escaped"), fallbackValue: -1, CancellationToken.None))
      .ThrowsExactly<OutOfMemoryException>();
#pragma warning restore CA2201, S112
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.HalfOpen);

    // 3. A caller arriving while half-open gets the fallback; only one probe is allowed.
    var operationCalls = 0;
    var result = await cb.ExecuteAsync(_ => { operationCalls++; return Task.FromResult(42); }, fallbackValue: -1, CancellationToken.None);

    await Assert.That(result).IsEqualTo(-1)
      .Because("a call during half-open returns the fallback instead of starting a second probe");
    await Assert.That(operationCalls).IsEqualTo(0)
      .Because("the operation must not run while a probe is outstanding");
    await Assert.That(cb.State).IsEqualTo(CircuitBreakerState.HalfOpen);
  }
}
