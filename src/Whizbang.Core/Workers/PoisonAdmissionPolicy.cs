namespace Whizbang.Core.Workers;

/// <summary>
/// Keeps a bounded population of failing rows from consuming the entire claim working set.
/// </summary>
/// <remarks>
/// <para>
/// Rows that fail are re-claimed once their lease lapses. Because they are re-claimed continuously
/// they always occupy the working set, so the claim never reaches the rows behind them. The
/// dead-letter path does retire them, but slowly enough that fresh failures replace them at roughly
/// the retirement rate, so the set never frees and healthy work is never claimed.
/// </para>
/// <para>
/// Measured side by side on identical framework and configuration: a consumer whose working set had
/// been retried into the teens held about ten thousand leases and drained roughly twenty-nine rows
/// per minute, with ninety-five percent of its inbox never claimed at all. A comparison consumer
/// whose rows were all at first delivery drained the same shape of backlog at about eight thousand
/// rows per minute — a difference of more than two orders of magnitude, from the attempt
/// distribution alone.
/// </para>
/// <para>
/// Widening the claim does not address it. Raising the claim floor tenfold on a stalled consumer
/// changed its drain rate by less than noise, because the constraint is WHAT occupies the set
/// rather than how large the set is.
/// </para>
/// <para>
/// The gate is a share, not a ban. Retried rows must still get through, or they never reach their
/// attempt ceiling, never retire, and the condition becomes permanent — trading a starvation
/// problem for a leak.
/// </para>
/// </remarks>
/// <docs>operations/workers/claim-backpressure</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PoisonAdmissionPolicyTests.cs</tests>
public sealed class PoisonAdmissionPolicy {

  /// <summary>Why a row was or was not admitted to the working set.</summary>
  public enum Verdict {
    /// <summary>Admitted.</summary>
    Admit,

    /// <summary>Past its attempt ceiling; it should be retired rather than re-claimed.</summary>
    PastAttemptCeiling,

    /// <summary>The working set is already dominated by retried rows.</summary>
    SetSaturatedByRetries,
  }

  /// <summary>Tuning for <see cref="PoisonAdmissionPolicy"/>.</summary>
  public sealed class Settings {
    /// <summary>Attempts after which a row is retired instead of re-claimed (default 10).</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Attempts at which a row counts as "retried" for share purposes (default 3).</summary>
    public int HighAttemptThreshold { get; set; } = 3;

    /// <summary>
    /// Fraction of the working set that may be retried rows before further ones yield to fresh work
    /// (default 0.5).
    /// </summary>
    public double MaxHighAttemptShare { get; set; } = 0.5;
  }

  /// <summary>The admission decision.</summary>
  /// <param name="Admit">Whether the row may enter the working set.</param>
  /// <param name="Reason">Why, for logs and metrics.</param>
  /// <param name="ObservedHighAttemptShare">The share that informed the decision.</param>
  public readonly record struct Decision(bool Admit, Verdict Reason, double ObservedHighAttemptShare);

  /// <summary>
  /// Marker the acquisition SQL writes into an inbox row's <c>error</c> when an attempt ended because its
  /// lease expired without any reported outcome (the stamp is guarded on <c>error IS NULL</c>, so a real
  /// recorded failure is never overwritten by it).
  /// </summary>
  private const string LEASE_EXPIRY_STAMP_MARKER = "ended without a reported outcome";

  /// <summary>
  /// True when the row's recorded error is the framework's own abandonment stamp rather than a handler
  /// failure: its attempts were spent on expired leases (restarts, deadlocks, timeouts), so it is a
  /// casualty, not poison, and must not count toward the high-attempt share.
  /// </summary>
  public static bool IsLeaseExpiryCasualty(string? error) =>
    error is not null
    && error.StartsWith("Attempt ", StringComparison.Ordinal)
    && error.Contains(LEASE_EXPIRY_STAMP_MARKER, StringComparison.Ordinal);

  private readonly Settings _settings;

  /// <summary>Initializes a new instance of the <see cref="PoisonAdmissionPolicy"/> class.</summary>
  /// <param name="settings">Tuning; defaults are production-safe.</param>
  public PoisonAdmissionPolicy(Settings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    _settings = settings;
  }

  /// <summary>
  /// Decides whether a row may enter the claim working set.
  /// </summary>
  /// <param name="attempts">Attempts already charged to this row.</param>
  /// <param name="workingSetSize">Rows currently held.</param>
  /// <param name="highAttemptShare">
  /// Fraction of the working set at or above <see cref="Settings.HighAttemptThreshold"/>.
  /// </param>
  /// <returns>The decision and the evidence behind it.</returns>
  public Decision Evaluate(int attempts, int workingSetSize, double highAttemptShare) {
    // A share outside [0,1] means the caller computed it wrong. Clamping would hide a miscount in
    // the very signal this gate depends on.
    ArgumentOutOfRangeException.ThrowIfLessThan(highAttemptShare, 0.0);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(highAttemptShare, 1.0);

    // Past the ceiling the row is done. Re-admitting it is how rows reached attempts far beyond
    // their configured maximum while healthy work sat unclaimed.
    if (attempts > _settings.MaxAttempts) {
      return new Decision(false, Verdict.PastAttemptCeiling, highAttemptShare);
    }

    // With nothing in flight there is nothing to starve, and refusing here would deadlock a consumer
    // whose entire remaining backlog is retried rows.
    if (workingSetSize <= 0) {
      return new Decision(true, Verdict.Admit, highAttemptShare);
    }

    // Fresh work is never gated. This policy protects it.
    if (attempts < _settings.HighAttemptThreshold) {
      return new Decision(true, Verdict.Admit, highAttemptShare);
    }

    if (highAttemptShare > _settings.MaxHighAttemptShare) {
      return new Decision(false, Verdict.SetSaturatedByRetries, highAttemptShare);
    }

    return new Decision(true, Verdict.Admit, highAttemptShare);
  }
}
