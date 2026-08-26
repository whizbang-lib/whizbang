using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A claim loop that is holding work without completing it must say so.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for looks like idleness from outside: a service leases a small working
/// set, leaves the rest of a large backlog unclaimed, and burns almost no CPU. Nothing logs,
/// nothing errors, and the process stays up — so crash alerting, error-rate alerting and a
/// liveness probe all report a healthy service while the backlog does not move.
/// </para>
/// <para>
/// From outside the database, "claimed nothing because nothing was available" and "claimed nothing
/// while work is waiting" are the same observation: no log line either way. The distinguishing
/// signal is already computed inside the claim loop and then discarded — whether a claim returned
/// the SAME work set as the previous claim. A repeat means rows are leased to this instance and
/// are not completing, which is the starvation case; a genuinely empty claim against an empty
/// store is just an idle service.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimCycleReport.cs</code-under-test>
[Category("Workers")]
public class ClaimCycleReportTests {

  private sealed class CapturingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => Noop.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
      => Entries.Add((level, fmt(state, ex)));
    private sealed class Noop : IDisposable { public static readonly Noop Instance = new(); public void Dispose() { } }
  }

  [Test]
  public async Task AnIdleServiceWithAnEmptyStoreStaysSilentAsync() {
    var logger = new CapturingLogger();
    var report = new ClaimCycleReport(repeatStreakThreshold: 3);

    for (var i = 0; i < 25; i++) {
      report.Record(claimedAnything: false, wasRepeat: false, logger);
    }

    await Assert.That(logger.Entries.Count).IsEqualTo(0)
      .Because("an idle service polling an empty store is the normal resting state — warning about "
             + "it would train operators to ignore the one line that means something");
  }

  [Test]
  public async Task ProgressStaysSilentAsync() {
    var logger = new CapturingLogger();
    var report = new ClaimCycleReport(repeatStreakThreshold: 3);

    for (var i = 0; i < 25; i++) {
      report.Record(claimedAnything: true, wasRepeat: false, logger);
    }

    await Assert.That(logger.Entries.Count).IsEqualTo(0)
      .Because("claiming fresh work every cycle is exactly what a healthy loop does");
  }

  [Test]
  public async Task ARepeatStreakReportsStarvationAsync() {
    var logger = new CapturingLogger();
    var report = new ClaimCycleReport(repeatStreakThreshold: 3);

    report.Record(claimedAnything: true, wasRepeat: true, logger);
    report.Record(claimedAnything: true, wasRepeat: true, logger);
    await Assert.That(logger.Entries.Count).IsEqualTo(0)
      .Because("one or two repeats is ordinary — a leased row awaiting its completion flush is "
             + "re-offered by design, so warning immediately would fire constantly");

    report.Record(claimedAnything: true, wasRepeat: true, logger);

    await Assert.That(logger.Entries.Count(e => e.Level == LogLevel.Warning)).IsEqualTo(1)
      .Because("this is the entire bug signature: work leased to this instance, re-offered every "
             + "cycle, never completing — and nothing reports it today");
    await Assert.That(logger.Entries[0].Message.Contains('3')).IsTrue()
      .Because("the streak length tells an operator how long it has been stuck");
  }

  [Test]
  public async Task TheWarningDoesNotRepeatOnEveryCycleAsync() {
    var logger = new CapturingLogger();
    var report = new ClaimCycleReport(repeatStreakThreshold: 3);

    for (var i = 0; i < 60; i++) {
      report.Record(claimedAnything: true, wasRepeat: true, logger);
    }

    var warnings = logger.Entries.Count(e => e.Level == LogLevel.Warning);
    await Assert.That(warnings).IsLessThanOrEqualTo(4)
      .Because("a stalled loop polls continuously; one line per cycle would bury the incident in "
             + "its own alert and cost more than the stall");
    await Assert.That(warnings).IsGreaterThanOrEqualTo(2)
      .Because("it must keep reporting as the stall persists — a single line at the start is "
             + "easy to miss and says nothing about duration");
  }

  [Test]
  public async Task FreshWorkClearsTheStreakAsync() {
    var logger = new CapturingLogger();
    var report = new ClaimCycleReport(repeatStreakThreshold: 3);

    report.Record(claimedAnything: true, wasRepeat: true, logger);
    report.Record(claimedAnything: true, wasRepeat: true, logger);
    report.Record(claimedAnything: true, wasRepeat: false, logger);   // progress
    report.Record(claimedAnything: true, wasRepeat: true, logger);
    report.Record(claimedAnything: true, wasRepeat: true, logger);

    await Assert.That(logger.Entries.Count).IsEqualTo(0)
      .Because("a loop that makes progress between repeats is not stalled, and treating it as "
             + "stalled would make the signal useless on any busy service");
    await Assert.That(report.CurrentRepeatStreak).IsEqualTo(2);
  }

  [Test]
  public async Task RecoveryIsReportedSoTheIncidentHasAnEndAsync() {
    var logger = new CapturingLogger();
    var report = new ClaimCycleReport(repeatStreakThreshold: 3);

    for (var i = 0; i < 5; i++) {
      report.Record(claimedAnything: true, wasRepeat: true, logger);
    }
    logger.Entries.Clear();

    report.Record(claimedAnything: true, wasRepeat: false, logger);

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information)).IsTrue()
      .Because("without a recovery line an operator cannot tell a stall that resolved itself from "
             + "one still in progress, which is the difference between an incident and a blip");
    await Assert.That(report.CurrentRepeatStreak).IsEqualTo(0);
  }

  [Test]
  public async Task TotalsAreExposedForMetricsAsync() {
    var logger = new CapturingLogger();
    var report = new ClaimCycleReport(repeatStreakThreshold: 3);

    report.Record(claimedAnything: false, wasRepeat: false, logger);
    report.Record(claimedAnything: false, wasRepeat: false, logger);
    report.Record(claimedAnything: true, wasRepeat: false, logger);
    report.Record(claimedAnything: true, wasRepeat: true, logger);

    await Assert.That(report.TotalCycles).IsEqualTo(4);
    await Assert.That(report.EmptyCycles).IsEqualTo(2);
    await Assert.That(report.RepeatCycles).IsEqualTo(1);
    await Assert.That(report.ProductiveCycles).IsEqualTo(1)
      .Because("productive vs repeat vs empty is the rows-offered-versus-claimed question the "
             + "issue asks for, answered on the client where no schema change is needed");
  }

  [Test]
  public async Task RejectsANonsensicalThresholdRatherThanNeverFiringAsync() {
    await Assert.That(() => new ClaimCycleReport(repeatStreakThreshold: 0))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a zero threshold would warn on the first ordinary re-offer; a negative one would "
             + "never warn at all, silently disabling the detector this type exists to be");
  }
}
