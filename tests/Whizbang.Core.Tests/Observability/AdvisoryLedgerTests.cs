using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// What the process-local advisory ledger will and will not let be said again.
/// </summary>
/// <remarks>
/// <para>
/// This is the default, and it is also what a durable ledger falls back to when the database cannot
/// be reached, so its semantics have to be the real ones rather than a set of keys. Getting the
/// cooldown wrong in either direction is invisible in production: too short and the advisory
/// returns to a warning every cycle, which is what it was changed to stop doing; too long, or
/// forever, and a finding nobody acted on cannot be told apart from one that was fixed.
/// </para>
/// <para>
/// The signature cases matter most. Advice that has changed is new advice, and suppressing it
/// because the table is the same is how an operator ends up acting on a stale finding.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <code-under-test>src/Whizbang.Core/Observability/AdvisoryLedger.cs</code-under-test>
public class AdvisoryLedgerTests {

  private static readonly DateTimeOffset _t0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _week = TimeSpan.FromDays(7);

  [Test]
  public async Task AFirstSightingIsReportedAsync() {
    var ledger = new AdvisoryLedger();

    await Assert.That(await ledger.TryBeginReportAsync("t", "sig", _t0, _week)).IsTrue()
      .Because("a finding nobody has reported has to be granted, or the first report never happens.");
  }

  [Test]
  public async Task TheSameAdviceWithinTheCooldownIsRefusedAsync() {
    var ledger = new AdvisoryLedger();
    await ledger.TryBeginReportAsync("t", "sig", _t0, _week);

    var again = await ledger.TryBeginReportAsync("t", "sig", _t0.AddDays(6), _week);

    await Assert.That(again).IsFalse()
      .Because("this runs on a 30-second cycle, so granting an unchanged finding again is a wall of "
             + "identical warnings and the reason the ledger exists.");
  }

  [Test]
  public async Task TheSameAdviceAfterTheCooldownIsReportedAgainAsync() {
    var ledger = new AdvisoryLedger();
    await ledger.TryBeginReportAsync("t", "sig", _t0, _week);

    var again = await ledger.TryBeginReportAsync("t", "sig", _t0 + _week, _week);

    await Assert.That(again).IsTrue()
      .Because("a finding suppressed forever cannot be told apart from one that was fixed, and the "
             + "table is still being scanned either way.");
  }

  [Test]
  public async Task AdviceThatHasChangedIsReportedAtOnceAsync() {
    var ledger = new AdvisoryLedger();
    await ledger.TryBeginReportAsync("t", "JobName", _t0, _week);

    var changed = await ledger.TryBeginReportAsync("t", "JobName,Status", _t0.AddMinutes(1), _week);

    await Assert.That(changed).IsTrue()
      .Because("another field being exposed is new advice about the same table, and waiting out a "
             + "week of cooldown would leave the operator acting on the stale version of it.");
  }

  /// <summary>A second finding about something else is not the first one repeating.</summary>
  [Test]
  public async Task ADifferentFindingIsTrackedSeparatelyAsync() {
    var ledger = new AdvisoryLedger();
    await ledger.TryBeginReportAsync("first", "sig", _t0, _week);

    var other = await ledger.TryBeginReportAsync("second", "sig", _t0, _week);

    await Assert.That(other).IsTrue()
      .Because("the key is the identity of a finding; sharing suppression across findings would "
             + "silence every table after the first one.");
  }

  /// <summary>A cooldown of nothing suppresses nothing, which is what a zero means.</summary>
  [Test]
  public async Task AZeroCooldownSuppressesNothingAsync() {
    var ledger = new AdvisoryLedger();
    await ledger.TryBeginReportAsync("t", "sig", _t0, TimeSpan.Zero);

    var again = await ledger.TryBeginReportAsync("t", "sig", _t0, TimeSpan.Zero);

    await Assert.That(again).IsTrue();
  }

  /// <summary>
  /// Past the ceiling the oldest entry goes, and the ledger keeps answering.
  /// </summary>
  /// <remarks>
  /// Reached from a cycle that runs for the life of the process, so unbounded growth here is a slow
  /// leak in every service. Dropping the least recently reported entry at worst repeats one piece
  /// of advice, which is the cheapest thing that can go wrong in this file.
  /// </remarks>
  [Test]
  public async Task PastItsCapacityTheOldestEntryIsForgottenAsync() {
    var ledger = new AdvisoryLedger();

    // The oldest entry, and then exactly enough newer ones to fill the ledger without it.
    await ledger.TryBeginReportAsync("oldest", "sig", _t0, _week);
    for (var i = 0; i < AdvisoryLedger.CAPACITY; i++) {
      await ledger.TryBeginReportAsync($"k{i}", "sig", _t0.AddMinutes(1 + i), _week);
    }

    await Assert.That(await ledger.TryBeginReportAsync("oldest", "sig", _t0.AddMinutes(2), _week))
      .IsTrue()
      .Because("the oldest entry is the one evicted, so the finding it held is reported again "
             + "rather than the ledger growing without limit.");
    await Assert.That(await ledger.TryBeginReportAsync("k1", "sig", _t0.AddMinutes(3), _week))
      .IsFalse()
      .Because("and everything newer than it is still suppressed, or the eviction took the wrong "
             + "entry and the ceiling is not a ceiling.");
  }

  [Test]
  public async Task ANullKeyOrSignatureIsRejectedAsync() {
    var ledger = new AdvisoryLedger();

    await Assert.That(async () => await ledger.TryBeginReportAsync(null!, "sig", _t0, _week))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await ledger.TryBeginReportAsync("t", null!, _t0, _week))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ACanceledConsultDoesNotRecordASightingAsync() {
    var ledger = new AdvisoryLedger();
    using var cancelled = new CancellationTokenSource();
    await cancelled.CancelAsync();

    await Assert.That(async () => await ledger.TryBeginReportAsync("t", "sig", _t0, _week, cancelled.Token))
      .Throws<OperationCanceledException>();
    await Assert.That(await ledger.TryBeginReportAsync("t", "sig", _t0, _week)).IsTrue()
      .Because("a consult that was canceled before it decided anything must not leave the finding "
             + "looking reported, or cancellation would silence it.");
  }
}
