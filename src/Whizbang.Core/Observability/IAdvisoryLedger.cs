namespace Whizbang.Core.Observability;

/// <summary>
/// Remembers which advisory findings have already been reported, so a finding that keeps being
/// true is raised once rather than every cycle.
/// </summary>
/// <remarks>
/// <para>
/// The difference between implementations is not an optimization. <see cref="AdvisoryLedger"/> is
/// per-process, which is sound only for a single instance that runs for a long time. Neither half
/// holds for a fleet: every replica reaches its own conclusion about the same table and reports it
/// independently, so the advice arrives once per pod, and every restart clears the memory and
/// starts the count again. A deployment that restarts on a schedule therefore gets the same advice
/// on the same cadence forever, which is how advice stops being read.
/// </para>
/// <para>
/// A durable implementation makes the key the identity of a finding, so "have we already said this"
/// survives a restart and is shared by every replica. The same reasoning, and the same shape, as
/// the integrity ledger in <c>Whizbang.Core.Messaging</c>.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/AdvisoryLedgerTests.cs</tests>
public interface IAdvisoryLedger {
  /// <summary>
  /// True when this finding should be reported now: never reported before, or what it says has
  /// changed, or the cooldown has elapsed since it was last reported. Records the sighting either
  /// way.
  /// </summary>
  /// <param name="findingKey">
  /// What the finding is about, stable across processes and restarts. The table it concerns, not
  /// the object that noticed it.
  /// </param>
  /// <param name="signature">
  /// What the finding says. A change means the advice itself is different -- another field is now
  /// exposed, or one has been indexed since -- which is new advice and is reported immediately
  /// rather than waiting out the cooldown.
  /// </param>
  /// <param name="now">The clock, passed in so the decision is testable.</param>
  /// <param name="cooldown">How long the same unchanged advice stays suppressed.</param>
  /// <param name="cancellationToken">Cancels the consult.</param>
  ValueTask<bool> TryBeginReportAsync(
    string findingKey, string signature, DateTimeOffset now, TimeSpan cooldown,
    CancellationToken cancellationToken = default);
}
