using Whizbang.Core.Health;

namespace Whizbang.Core.Signals;

/// <summary>
/// Shared, lock-free state behind the <c>signal-bus</c> health component: the wire-route probe
/// verdict (startup + periodic re-probe), the last time any wire signal actually arrived, and the
/// doorbell-liveness edge accounting (work discovered by poll when a doorbell should have rung).
/// Writers are the <see cref="SignalBusHostedService"/> probe loop, the wire transports, and the
/// claim loop; the reader is the health source. Every write path is called from hot loops, so the
/// state is Interlocked/Volatile only — no locks.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusProbeTests.cs:Report_ConsecutiveMissedDoorbells_DegradesAtThreshold_DoorbellWakeResetsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusProbeTests.cs:HostedStart_DeadTransport_ProbeMarksWireRouteFailedAsync</tests>
public sealed class SignalBusLivenessState {
  private const int VERDICT_UNKNOWN = 0;
  private const int VERDICT_VERIFIED = 1;
  private const int VERDICT_FAILED = 2;

  private readonly TaskCompletionSource<bool> _firstProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private int _verdict;
  private int _consecutiveMissed;
  private long _lastWireSignalTicks;
  private long _lastProbeTicks;
  private volatile string? _failedTransport;

  /// <summary>Completes with the first probe's verdict — the startup self-test's completion signal.</summary>
  public Task<bool> FirstProbe => _firstProbe.Task;

  /// <summary>Wire-route verdict: <c>null</c> until the first probe completes.</summary>
  public bool? WireRouteVerified => Volatile.Read(ref _verdict) switch {
    VERDICT_VERIFIED => true,
    VERDICT_FAILED => false,
    _ => null,
  };

  /// <summary>When any wire signal last arrived (probe or real doorbell). <c>null</c> until one does.</summary>
  public DateTimeOffset? LastWireSignalAt {
    get {
      var ticks = Volatile.Read(ref _lastWireSignalTicks);
      return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
  }

  /// <summary>When the wire-route probe last ran. <c>null</c> until the first completes.</summary>
  public DateTimeOffset? LastProbeAt {
    get {
      var ticks = Volatile.Read(ref _lastProbeTicks);
      return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }
  }

  /// <summary>Consecutive work batches discovered by poll with no preceding doorbell.</summary>
  public int ConsecutiveMissedDoorbells => Volatile.Read(ref _consecutiveMissed);

  /// <summary>Threshold at which missed doorbells degrade the component (from <see cref="SignalBusOptions"/>).</summary>
  public int MissedDoorbellThreshold { get; init; } = 3;

  /// <summary>Record a probe verdict (startup or periodic re-probe).</summary>
  public void MarkProbeResult(bool success, DateTimeOffset at, string? failedTransport = null) {
    Volatile.Write(ref _lastProbeTicks, at.UtcTicks);
    _failedTransport = success ? null : failedTransport;
    Volatile.Write(ref _verdict, success ? VERDICT_VERIFIED : VERDICT_FAILED);
    _firstProbe.TrySetResult(success);
  }

  /// <summary>Record that a wire signal arrived (called by transports on receive).</summary>
  public void MarkWireSignalReceived(DateTimeOffset at) => Volatile.Write(ref _lastWireSignalTicks, at.UtcTicks);

  /// <summary>Record a claim woken by a new-work doorbell — resets the missed streak.</summary>
  public void RecordDoorbellWake() => Interlocked.Exchange(ref _consecutiveMissed, 0);

  /// <summary>Record work discovered by poll on an edge where a doorbell should have rung.</summary>
  public void RecordMissedDoorbell() => Interlocked.Increment(ref _consecutiveMissed);

  /// <summary>The signal-bus component's self-reported health.</summary>
  public ComponentHealth Report() {
    if (WireRouteVerified == false) {
      var transport = _failedTransport;
      var via = transport is null ? "" : $" (transport {transport})";
      return new ComponentHealth(ComponentState.Degraded,
        $"wire-route self-test failed{via}: doorbells are not being delivered; work pumps are running on polling fallback");
    }
    var missed = ConsecutiveMissedDoorbells;
    if (missed >= MissedDoorbellThreshold) {
      return new ComponentHealth(ComponentState.Degraded,
        $"{missed} consecutive work batches were discovered by poll with no preceding doorbell — NOTIFY delivery suspect");
    }
    return new ComponentHealth(ComponentState.Operational,
      WireRouteVerified is null ? "wire route not yet verified" : null);
  }
}
