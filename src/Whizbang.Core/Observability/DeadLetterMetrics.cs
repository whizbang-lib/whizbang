using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metrics for the Whizbang internal DLQ subsystem. Meter name:
/// <c>Whizbang.DeadLetters</c>.
/// </summary>
/// <remarks>
/// <para>
/// Surface:
/// </para>
/// <list type="bullet">
///   <item><description><b>Added</b> — count of rows moved into <c>wh_dead_letters</c>
///   (failure-path side, fired by <see cref="Whizbang.Core.Messaging.IDeadLetterStore"/>
///   consumers). Tagged by <c>source_table</c> (<c>wh_outbox</c> / <c>wh_inbox</c> /
///   <c>wh_perspective_events</c>) and <c>reason</c>
///   (<see cref="Whizbang.Core.Messaging.MessageFailureReason"/>).</description></item>
///   <item><description><b>Recovered</b> — successful re-emit by the recovery worker.
///   Tagged by <c>source_table</c>.</description></item>
///   <item><description><b>Held</b> — transition to HoldForReview (policy exhausted on a
///   reason that needs human attention).</description></item>
///   <item><description><b>PermanentlyFailed</b> — transition to PermanentlyFailed.</description></item>
///   <item><description><b>RecoveryAttempts</b> — per scan-cycle recovery attempts that
///   were dispatched (whether successful, raced, or failed). Tagged by <c>reason</c>.</description></item>
///   <item><description><b>GenerationReplayScheduled</b> — count of rows scheduled by the
///   generation-replay sweep on worker startup. Tagged by <c>generation</c>.</description></item>
/// </list>
/// </remarks>
/// <docs>operations/observability/metrics</docs>
public sealed class DeadLetterMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.DeadLetters";
#pragma warning restore CA1707

  /// <summary>Rows moved into wh_dead_letters (incremented per atomic Move). Tagged by source_table + reason.</summary>
  public Counter<long> Added { get; }

  /// <summary>Successful recoveries — row re-emitted into source table. Tagged by source_table.</summary>
  public Counter<long> Recovered { get; }

  /// <summary>Transitions to HoldForReview. Tagged by policy_name + reason.</summary>
  public Counter<long> Held { get; }

  /// <summary>Transitions to PermanentlyFailed. Tagged by policy_name + reason.</summary>
  public Counter<long> PermanentlyFailed { get; }

  /// <summary>Recovery attempts dispatched (success or fail). Tagged by reason.</summary>
  public Counter<long> RecoveryAttempts { get; }

  /// <summary>Rows scheduled by the generation-replay sweep. Tagged by generation.</summary>
  public Counter<long> GenerationReplayScheduled { get; }

  /// <summary>Initializes a new instance of <see cref="DeadLetterMetrics"/>.</summary>
  public DeadLetterMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    Added = meter.CreateCounter<long>(
      "whizbang.dead_letters.added",
      description: "Rows moved into wh_dead_letters; tagged by source_table + reason");
    Recovered = meter.CreateCounter<long>(
      "whizbang.dead_letters.recovered",
      description: "Successful recovery re-emits; tagged by source_table");
    Held = meter.CreateCounter<long>(
      "whizbang.dead_letters.held",
      description: "Transitions to HoldForReview; tagged by policy_name + reason");
    PermanentlyFailed = meter.CreateCounter<long>(
      "whizbang.dead_letters.permanently_failed",
      description: "Transitions to PermanentlyFailed; tagged by policy_name + reason");
    RecoveryAttempts = meter.CreateCounter<long>(
      "whizbang.dead_letters.recovery_attempts",
      description: "Recovery attempts dispatched (any outcome); tagged by reason");
    GenerationReplayScheduled = meter.CreateCounter<long>(
      "whizbang.dead_letters.generation_replay_scheduled",
      description: "Rows scheduled by the generation-replay sweep on worker startup");
  }
}
