// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1986: a worker loop's transient database failures are each a Warning (the worker logs those), and the run
/// they make becomes one Error only once it has lasted past the threshold, then one Information line when the
/// database answers again. A failover of a second or two therefore pages nobody, and a database that stays down
/// pages once, with how long it has been down.
/// </summary>
/// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
[Category("Workers")]
public sealed class DatabaseFailureEscalationTests {
  private const string WORKER = "SampleWorker";

  private static TransientDatabaseFailure _failure(string sqlState = "08006") {
    _ = TransientDatabaseFailure.TryClassify(FakeDbException.WithSqlState(sqlState, message: "the connection dropped"), out var failure);
    return failure!;
  }

  private static (DatabaseFailureEscalation Escalation, FakeTimeProvider Time, FakeLogCollector Log) _tracker(
      DatabaseFailureEscalationOptions? options = null) {
    var time = new FakeTimeProvider();
    var logger = new FakeLogger();
    var escalation = new DatabaseFailureEscalation(WORKER, options ?? new DatabaseFailureEscalationOptions {
      ConsecutiveFailures = 3,
      FailingFor = TimeSpan.FromMinutes(1),
    }, time, logger);
    return (escalation, time, logger.Collector);
  }

  private static List<FakeLogRecord> _lines(FakeLogCollector log, int eventId) =>
    [.. log.GetSnapshot().Where(r => r.Id.Id == eventId)];

  [Test]
  public async Task OneFailure_LogsNothingAsync() {
    var (escalation, _, log) = _tracker();

    escalation.Failed(_failure());

    await Assert.That(log.GetSnapshot()).IsEmpty()
      .Because("#1986: a single transient failure is the worker's own Warning and nothing more; the tracker adds no line");
  }

  [Test]
  public async Task ARunPastBothThresholds_LogsOneErrorNamingHowLongTheDatabaseHasBeenFailingAsync() {
    var (escalation, time, log) = _tracker();
    var latest = _failure("57P01");

    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromSeconds(30));
    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromSeconds(31));
    escalation.Failed(latest);

    var escalated = _lines(log, DatabaseFailureEscalation.ESCALATED_EVENT_ID);
    await Assert.That(escalated).Count().IsEqualTo(1);
    await Assert.That(escalated[0].Level).IsEqualTo(LogLevel.Error)
      .Because("#1986: a database that keeps failing past the threshold is the one Error an operator alerts on");
    await Assert.That(escalated[0].Id.Name).IsEqualTo(DatabaseFailureEscalation.ESCALATED_EVENT_NAME);
    await Assert.That(escalated[0].Message).Contains(WORKER, StringComparison.Ordinal)
      .And.Contains("00:01:01", StringComparison.Ordinal)
      .And.Contains("3 in a row", StringComparison.Ordinal)
      .And.Contains(TransientDatabaseFailure.CONNECTION_LOST, StringComparison.Ordinal)
      .And.Contains("57P01", StringComparison.Ordinal);
    await Assert.That(escalated[0].Exception).IsSameReferenceAs(latest.Cause);
  }

  [Test]
  public async Task FailuresAfterTheEscalation_DoNotLogItAgainAsync() {
    var (escalation, time, log) = _tracker();

    for (var i = 0; i < 10; i++) {
      escalation.Failed(_failure());
      time.Advance(TimeSpan.FromSeconds(30));
    }

    await Assert.That(_lines(log, DatabaseFailureEscalation.ESCALATED_EVENT_ID)).Count().IsEqualTo(1)
      .Because("#1986: one Error per run, however long the run goes on; the worker's Warnings carry each failure");
  }

  [Test]
  public async Task ManyFailuresInsideTheTime_DoNotEscalateAsync() {
    var (escalation, time, log) = _tracker();

    for (var i = 0; i < 20; i++) {
      escalation.Failed(_failure());
      time.Advance(TimeSpan.FromSeconds(2));
    }

    await Assert.That(log.GetSnapshot()).IsEmpty()
      .Because("#1986: a fast loop meets many failures in a short failover; the count alone must not page anyone");
  }

  [Test]
  public async Task AFewFailuresOverALongTime_DoNotEscalateAsync() {
    var (escalation, time, log) = _tracker();

    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromMinutes(10));
    escalation.Failed(_failure());

    await Assert.That(log.GetSnapshot()).IsEmpty()
      .Because("#1986: two failures ten minutes apart in a loop that rarely runs say little about the database; both "
             + "thresholds must be met");
  }

  [Test]
  public async Task ARecoveryAfterAnEscalation_LogsOneInformationLineAsync() {
    var (escalation, time, log) = _tracker();
    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromSeconds(40));
    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromSeconds(40));
    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromSeconds(10));

    escalation.Succeeded();
    escalation.Succeeded();

    var recovered = _lines(log, DatabaseFailureEscalation.RECOVERED_EVENT_ID);
    await Assert.That(recovered).Count().IsEqualTo(1)
      .Because("#1986: an escalated run ends with one line saying so, not one per successful iteration");
    await Assert.That(recovered[0].Level).IsEqualTo(LogLevel.Information);
    await Assert.That(recovered[0].Id.Name).IsEqualTo(DatabaseFailureEscalation.RECOVERED_EVENT_NAME);
    await Assert.That(recovered[0].Message).Contains(WORKER, StringComparison.Ordinal)
      .And.Contains("00:01:30", StringComparison.Ordinal)
      .And.Contains("3 in a row", StringComparison.Ordinal);
  }

  [Test]
  public async Task ARecoveryWithoutAnEscalation_LogsNothingAsync() {
    var (escalation, time, log) = _tracker();
    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromSeconds(1));
    escalation.Failed(_failure());

    escalation.Succeeded();

    await Assert.That(log.GetSnapshot()).IsEmpty()
      .Because("#1986: a blip that never escalated has nothing to take back; its Warnings already told the story");
  }

  [Test]
  public async Task ASuccess_EndsTheRun_SoTheNextOneStartsFromNothingAsync() {
    var (escalation, time, log) = _tracker();
    escalation.Failed(_failure());
    time.Advance(TimeSpan.FromSeconds(59));
    escalation.Failed(_failure());
    escalation.Succeeded();
    time.Advance(TimeSpan.FromSeconds(5));

    escalation.Failed(_failure());

    await Assert.That(log.GetSnapshot()).IsEmpty()
      .Because("the success ended the first run; the failure after it is the first of a new run, not the third of the old one");
  }

  [Test]
  public async Task ASecondRunPastTheThreshold_EscalatesAgainAsync() {
    var (escalation, time, log) = _tracker();
    for (var run = 0; run < 2; run++) {
      for (var i = 0; i < 3; i++) {
        escalation.Failed(_failure());
        time.Advance(TimeSpan.FromSeconds(31));
      }
      escalation.Succeeded();
    }

    await Assert.That(_lines(log, DatabaseFailureEscalation.ESCALATED_EVENT_ID)).Count().IsEqualTo(2);
    await Assert.That(_lines(log, DatabaseFailureEscalation.RECOVERED_EVENT_ID)).Count().IsEqualTo(2);
  }

  [Test]
  public async Task AZeroTime_EscalatesOnTheCountAloneAsync() {
    var (escalation, _, log) = _tracker(new DatabaseFailureEscalationOptions { ConsecutiveFailures = 2, FailingFor = TimeSpan.Zero });

    escalation.Failed(_failure());
    await Assert.That(log.GetSnapshot()).IsEmpty();
    escalation.Failed(_failure());

    await Assert.That(_lines(log, DatabaseFailureEscalation.ESCALATED_EVENT_ID)).Count().IsEqualTo(1);
  }

  [Test]
  public async Task OneFailureAsTheCount_EscalatesOnTheTimeAloneAsync() {
    var (escalation, time, log) = _tracker(new DatabaseFailureEscalationOptions { ConsecutiveFailures = 1, FailingFor = TimeSpan.FromSeconds(10) });

    escalation.Failed(_failure());
    await Assert.That(log.GetSnapshot()).IsEmpty()
      .Because("the run is only just starting; ten seconds have not passed");
    time.Advance(TimeSpan.FromSeconds(10));
    escalation.Failed(_failure());

    await Assert.That(_lines(log, DatabaseFailureEscalation.ESCALATED_EVENT_ID)).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Options_AreTheOnesItWasGivenAsync() {
    var options = new DatabaseFailureEscalationOptions { ConsecutiveFailures = 7, FailingFor = TimeSpan.FromMinutes(5) };

    var escalation = new DatabaseFailureEscalation(WORKER, options, new FakeTimeProvider(), new FakeLogger());

    await Assert.That(escalation.Options).IsSameReferenceAs(options);
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    var options = new DatabaseFailureEscalationOptions();
    var time = new FakeTimeProvider();
    var logger = new FakeLogger();

    await Assert.That(() => new DatabaseFailureEscalation(null!, options, time, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new DatabaseFailureEscalation(WORKER, null!, time, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new DatabaseFailureEscalation(WORKER, options, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new DatabaseFailureEscalation(WORKER, options, time, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new DatabaseFailureEscalation(WORKER, options, time, logger).Failed(null!)).Throws<ArgumentNullException>();
  }
}
