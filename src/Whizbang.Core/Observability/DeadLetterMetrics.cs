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

  /// <summary>
  /// Per-process cap on distinct <c>stack_id</c> tag values. Stack dedup bounds any one
  /// storm naturally, but a process lifetime is unbounded — past the cap, arrivals count
  /// under the single "overflow" bucket instead of growing the series forever.
  /// </summary>
#pragma warning disable CA1707
  public const int MAX_DISTINCT_STACK_TAGS = 500;
#pragma warning restore CA1707

  private readonly Counter<long> _arrivalsByStack;
  private readonly Counter<long> _cohortVerdicts;
  private readonly Counter<long> _releaseWaves;
  private readonly Counter<long> _stackHistoryPruned;
  private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenStacks = new();

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
    _arrivalsByStack = meter.CreateCounter<long>(
      "whizbang.dead_letters.arrivals_by_stack",
      description: "Dead-letter arrivals tagged by normalized stack_id + reason — the real-time "
                 + "half of the stack telemetry contract; a stack_id with no prior history right "
                 + "after a deploy is the new-failure-mode alarm");
    _cohortVerdicts = meter.CreateCounter<long>(
      "whizbang.dead_letters.cohort_verdicts",
      description: "Canary campaign verdicts tagged by cohort + verdict (Pass/Fail/Mixed)");
    _releaseWaves = meter.CreateCounter<long>(
      "whizbang.dead_letters.release_waves",
      description: "Trickle release waves for Mixed cohorts, tagged by cohort + outcome (clean/halted)");
    _stackHistoryPruned = meter.CreateCounter<long>(
      "whizbang.dead_letters.stack_history_pruned",
      description: "Rolling stack-history rows pruned by the recovery worker's idle-gated cleanup — the maintenance facet of the stack layer");
  }

  /// <summary>Counts rolling stack-history rows pruned in one cleanup pass.</summary>
  /// <param name="pruned">Rows removed (only recorded when positive).</param>
  public void RecordStackHistoryPruned(long pruned) {
    if (pruned > 0) {
      _stackHistoryPruned.Add(pruned);
    }
  }

  /// <summary>Counts one trickle wave outcome for a Mixed cohort.</summary>
  /// <param name="cohort">The cohort key (error fingerprint).</param>
  /// <param name="clean">Whether the wave stayed out (true) or washed back (false).</param>
  public void RecordReleaseWave(string cohort, bool clean) {
    _releaseWaves.Add(1,
      new KeyValuePair<string, object?>("cohort", cohort),
      new KeyValuePair<string, object?>("outcome", clean ? "clean" : "halted"));
  }

  /// <summary>
  /// Counts a dead-letter arrival under its normalized stack identity — computed inline
  /// via the SAME <see cref="Whizbang.Core.DeadLetters.StackNormalizer"/> the async
  /// backfill uses, so the dashboard's stack_id joins the relational layer verbatim.
  /// No text tags "none"; past-the-cap identities tag "overflow" (counted, never dropped).
  /// </summary>
  /// <param name="sourceTable">Origin work table.</param>
  /// <param name="failureReason">The MessageFailureReason numeric value.</param>
  /// <param name="errorText">The failure text, when available.</param>
  public void RecordArrival(string sourceTable, int failureReason, string? errorText) {
    var stack = Whizbang.Core.DeadLetters.StackNormalizer.Normalize(errorText);
    string stackId;
    if (stack is null) {
      stackId = "none";
    } else if (_seenStacks.ContainsKey(stack.SequenceHash)
               || (_seenStacks.Count < MAX_DISTINCT_STACK_TAGS
                   && _seenStacks.TryAdd(stack.SequenceHash, 0))) {
      stackId = stack.SequenceHash;
    } else {
      stackId = "overflow";
    }
    _arrivalsByStack.Add(1,
      new KeyValuePair<string, object?>("source_table", sourceTable),
      new KeyValuePair<string, object?>("reason", failureReason.ToString(System.Globalization.CultureInfo.InvariantCulture)),
      new KeyValuePair<string, object?>("stack_id", stackId));
  }

  /// <summary>Counts a canary campaign verdict for a cohort.</summary>
  /// <param name="cohort">The cohort key (error fingerprint).</param>
  /// <param name="verdict">How the probes resolved.</param>
  public void RecordCohortVerdict(string cohort, Whizbang.Core.Messaging.CanaryVerdictKind verdict) {
    _cohortVerdicts.Add(1,
      new KeyValuePair<string, object?>("cohort", cohort),
      new KeyValuePair<string, object?>("verdict", verdict.ToString()));
  }
}
