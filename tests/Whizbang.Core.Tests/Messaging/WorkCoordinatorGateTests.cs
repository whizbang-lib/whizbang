using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.654 invariants around <see cref="WorkCoordinatorGate.AcquireAsync"/>.
/// Slot-3 forensic motivation (Jun 2026): with the pre-v0.654 implementation, an
/// exhausted gate's <c>WaitAsync(ct)</c> would hang every caller forever — silently,
/// no exception, no log. Every gated coordinator call AND every <c>MoveAsync</c> in
/// the DLQ pre-publish gate routed through this single chokepoint. The deadline turns
/// silent-saturation into a Warning-level operational signal.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public class WorkCoordinatorGateTests {

  private sealed record _LogEntry(LogLevel Level, string Message);

  private sealed class _CapturingLogger<T> : ILogger<T> {
    public List<_LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add(new _LogEntry(logLevel, formatter(state, exception)));
    }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Happy path: a gate with available slots returns a real <see cref="WorkCoordinatorGate.Releaser"/>.
  /// Disposing it releases the slot so a subsequent call can re-acquire — the "FIFO acquire / dispose"
  /// contract every gated coordinator method relies on.
  /// </summary>
  [Test]
  public async Task AcquireAsync_HasCapacity_ReturnsReleaserThatReleasesOnDisposeAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, acquireTimeoutMilliseconds: 10_000);

    var releaser = await gate.AcquireAsync(CancellationToken.None);
    releaser.Dispose();

    // If the dispose didn't release the slot, this WaitAsync would throw TimeoutException
    // — that's the assertion. The acquire-and-dispose pair is the invariant.
    var second = await gate.AcquireAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    second.Dispose();
    await Assert.That(gate.MaxConcurrent).IsEqualTo(1)
      .Because("Reaching this line means both acquires completed within their deadlines. The MaxConcurrent assertion is a placeholder — the real assertion is the absence of a TimeoutException above. If the first dispose hadn't released the slot, the second acquire would have hung past 1s and the test would have failed there.");
  }

  /// <summary>
  /// v0.654 deadline path: when the semaphore is fully saturated and the deadline elapses,
  /// AcquireAsync returns a no-op Releaser AND logs a Warning. Caller proceeds without holding
  /// a slot — saturation is advisory, not fatal.
  /// </summary>
  [Test]
  public async Task AcquireAsync_SaturatedBeyondDeadline_ReturnsNoopReleaserAndLogsWarningAsync() {
    var logger = new _CapturingLogger<WorkCoordinatorGate>();
    using var gate = new WorkCoordinatorGate(
      maxConcurrent: 1,
      acquireTimeoutMilliseconds: 100,
      logger: logger);

    var holding = await gate.AcquireAsync(CancellationToken.None);
    try {
      var deadlined = await gate.AcquireAsync(CancellationToken.None);
      deadlined.Dispose();

      var secondDeadlined = await gate.AcquireAsync(CancellationToken.None);
      secondDeadlined.Dispose();

      var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
      await Assert.That(warnings.Count).IsGreaterThanOrEqualTo(2)
        .Because("Each deadlined acquire produces exactly one Warning log entry; two deadlined calls produce two entries — operators can count them to spot saturation rate spikes. (v0.656 also emits Debug entry/grant/timeout breadcrumbs; the Warning invariant is what callers/operators key off of.)");
      await Assert.That(warnings[0].Message).Contains("WorkCoordinatorGate")
        .Because("Log must identify the gate by name so operators reading mixed Whizbang logs can correlate the saturation back to the chokepoint.");
      await Assert.That(warnings[0].Message).Contains("100")
        .Because("Including the configured deadline lets operators tell at a glance whether the deadline is the problem (too short for this load) vs whether there's a pool issue.");
    } finally {
      holding.Dispose();
    }
  }

  /// <summary>
  /// Opt-out: passing a non-positive deadline preserves the pre-v0.654 indefinite-wait behavior.
  /// Backward-compat escape hatch.
  /// </summary>
  [Test]
  public async Task AcquireAsync_TimeoutDisabled_BlocksUntilSlotAvailableAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1, acquireTimeoutMilliseconds: 0);

    var holding = await gate.AcquireAsync(CancellationToken.None);

    var pending = gate.AcquireAsync(CancellationToken.None).AsTask();

    var didCompleteEarly = await Task.WhenAny(pending, Task.Delay(200)) == pending;
    await Assert.That(didCompleteEarly).IsFalse()
      .Because("acquireTimeoutMilliseconds=0 must wait indefinitely — proves the opt-out preserves original behavior.");

    holding.Dispose();
    var unblocked = await pending.WaitAsync(TimeSpan.FromSeconds(1));
    unblocked.Dispose();
  }

  /// <summary>
  /// Disabled gate (maxConcurrent <= 0): every call returns default Releaser without
  /// touching a semaphore. No deadline, no logging, no contention.
  /// </summary>
  [Test]
  public async Task AcquireAsync_DisabledGate_AlwaysReturnsDefaultReleaserAsync() {
    var logger = new _CapturingLogger<WorkCoordinatorGate>();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 0, logger: logger);

    var releasers = new List<WorkCoordinatorGate.Releaser>();
    for (var i = 0; i < 100; i++) {
      releasers.Add(await gate.AcquireAsync(CancellationToken.None));
    }
    foreach (var r in releasers) {
      r.Dispose();
    }

    await Assert.That(logger.Entries).IsEmpty()
      .Because("A disabled gate must not log — there's no semaphore to time out on.");
  }

  /// <summary>
  /// Properties reflect constructor args verbatim. Lets operator diagnostics inspect
  /// the configured gate at runtime.
  /// </summary>
  [Test]
  public async Task Constructor_PropertiesReflectArgumentsAsync() {
    using var gate = new WorkCoordinatorGate(maxConcurrent: 17, acquireTimeoutMilliseconds: 42_000);
    await Assert.That(gate.MaxConcurrent).IsEqualTo(17);
    await Assert.That(gate.AcquireTimeoutMilliseconds).IsEqualTo(42_000);
  }
}
