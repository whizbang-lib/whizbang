using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Passive meters for the periodic probes that make up a service's idle footprint on its
/// database: how many probe ticks each worker ran and whether they found work, and how many duty
/// attempts were answered from memory while a contention window was open. The idle footprint is
/// the sum of the <c>idle</c> series; a service with an empty queue should see them grow slowly.
/// Meter name: <c>Whizbang.Probes</c>.
/// </summary>
/// <docs>fundamentals/workers/idle-footprint</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/ProbeCadenceMetricsTests.cs</tests>
public sealed class ProbeCadenceMetrics {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Probes";

  /// <summary>Tag key naming the probing worker.</summary>
  public const string PROBE_TAG = "probe";

  /// <summary>Tag key for the probe outcome: <c>work</c> or <c>idle</c>.</summary>
  public const string OUTCOME_TAG = "outcome";

  /// <summary>Outcome value when the probe found work.</summary>
  public const string OUTCOME_WORK = "work";

  /// <summary>Outcome value when the probe found nothing.</summary>
  public const string OUTCOME_IDLE = "idle";

  /// <summary>Probe name for the durable signal tail.</summary>
  public const string PROBE_DURABLE_SIGNAL_TAIL = "durable-signal-tail";

  /// <summary>Probe name for the instance lifecycle monitor.</summary>
  public const string PROBE_INSTANCE_LIFECYCLE = "instance-lifecycle";

  /// <summary>Probe name for the backlog-age duty.</summary>
  public const string PROBE_BACKLOG_AGE = "backlog-age";
#pragma warning restore CA1707

  private static readonly string[] _knownProbes = [PROBE_DURABLE_SIGNAL_TAIL, PROBE_INSTANCE_LIFECYCLE, PROBE_BACKLOG_AGE];

  /// <summary>Initializes the probe meters on the shared Whizbang meter factory.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public ProbeCadenceMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    ProbeTicks = meter.CreatePassiveCounter<long>("whizbang.probes.ticks",
      description: "Periodic probe ticks by worker and outcome; the idle series are the service's idle footprint");
    SuppressedDutyAttempts = meter.CreatePassiveCounter<long>("whizbang.probes.suppressed_duty_attempts",
      description: "Duty attempts answered from memory while a contention window was open, so no lock round trip was made");

    foreach (var probe in _knownProbes) {
      ProbeTicks.Touch(new TagList { { PROBE_TAG, probe }, { OUTCOME_TAG, OUTCOME_WORK } });
      ProbeTicks.Touch(new TagList { { PROBE_TAG, probe }, { OUTCOME_TAG, OUTCOME_IDLE } });
    }
  }

  /// <summary>Probe ticks by worker and outcome.</summary>
  public PassiveCounter<long> ProbeTicks { get; }

  /// <summary>Duty attempts suppressed by a contention window.</summary>
  public PassiveCounter<long> SuppressedDutyAttempts { get; }

  /// <summary>Records one probe tick.</summary>
  /// <param name="probe">The probing worker.</param>
  /// <param name="foundWork">Whether the probe found work.</param>
  public void RecordTick(string probe, bool foundWork) {
    ArgumentException.ThrowIfNullOrEmpty(probe);
    ProbeTicks.Add(1,
      new KeyValuePair<string, object?>(PROBE_TAG, probe),
      new KeyValuePair<string, object?>(OUTCOME_TAG, foundWork ? OUTCOME_WORK : OUTCOME_IDLE));
  }
}
