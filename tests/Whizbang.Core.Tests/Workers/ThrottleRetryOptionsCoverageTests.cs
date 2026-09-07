using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="ThrottleRetryOptions.ComputeDelay"/>'s guard against a
/// non-positive attempt number.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ThrottleRetryOptions.cs</code-under-test>
public class ThrottleRetryOptionsCoverageTests {

  /// <summary>
  /// Attempt 0 is the initial (non-retry) attempt and must never be delayed — a positive delay
  /// here would stall the very first publish attempt before any throttle has even been observed.
  /// </summary>
  [Test]
  public async Task ComputeDelay_NonPositiveAttemptNumber_ReturnsZeroAsync() {
    var options = new ThrottleRetryOptions();

    var delay = options.ComputeDelay(0);

    await Assert.That(delay).IsEqualTo(TimeSpan.Zero)
      .Because("attempt 0 is the initial try, not a retry — it must never be delayed");
  }
}
