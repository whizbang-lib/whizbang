// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Follows one worker's run of transient database failures and says, once, when it has gone on long enough to be
/// worth an alert, and once more when the database answers again.
/// </summary>
/// <remarks>
/// <para>A worker loop logs each transient database failure it meets at Warning, with its reason and SQLSTATE, and
/// reports it here (<see cref="Failed"/>); it reports every iteration that reached the database and succeeded too
/// (<see cref="Succeeded"/>). A success ends the run. A run that has lasted at least
/// <see cref="DatabaseFailureEscalationOptions.FailingFor"/>, measured from its first failure on the worker's
/// <see cref="TimeProvider"/>, and counted at least <see cref="DatabaseFailureEscalationOptions.ConsecutiveFailures"/>
/// failures logs one Error, EventId <see cref="ESCALATED_EVENT_ID"/>, naming the worker, how long the database has
/// been failing, how many failures in a row, and the latest failure's reason and SQLSTATE, with its exception. The
/// success that ends an escalated run logs one Information line, EventId <see cref="RECOVERED_EVENT_ID"/>. A run that
/// never escalated ends silently: its Warnings already told the story.</para>
/// <para>A failure that is not the database's (a defect) is not reported here, so it neither counts toward a run nor
/// ends one; the worker keeps logging it at Error.</para>
/// <para>Both lines are logged under the worker's own category, and every worker uses the same two EventIds, so one
/// query or one alert finds a persistent database failure whichever worker met it. Safe to share between the loops of
/// one worker: the run is then the worker's, not each loop's.</para>
/// </remarks>
/// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs</tests>
public sealed partial class DatabaseFailureEscalation {
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>The EventId of the one Error a run logs once it passes the threshold.</summary>
  public const int ESCALATED_EVENT_ID = 1986;

  /// <summary>The EventId name of the escalation.</summary>
  public const string ESCALATED_EVENT_NAME = "DatabaseFailurePersisting";

  /// <summary>The EventId of the Information line an escalated run logs when the database answers again.</summary>
  public const int RECOVERED_EVENT_ID = 1987;

  /// <summary>The EventId name of the recovery.</summary>
  public const string RECOVERED_EVENT_NAME = "DatabaseFailureRecovered";
#pragma warning restore CA1707

  private readonly string _worker;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;
  private readonly Lock _gate = new();
  private int _failures;
  private long _runStartedAt;
  private bool _escalated;

  /// <summary>Creates the tracker for one worker.</summary>
  /// <param name="worker">The worker's name, as the escalation and the recovery lines write it.</param>
  /// <param name="options">The threshold.</param>
  /// <param name="timeProvider">The clock the run's length is measured on.</param>
  /// <param name="logger">The worker's logger; both lines are logged under its category.</param>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:NullArguments_AreRefusedAsync</tests>
  public DatabaseFailureEscalation(
      string worker, DatabaseFailureEscalationOptions options, TimeProvider timeProvider, ILogger logger) {
    ArgumentNullException.ThrowIfNull(worker);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);
    _worker = worker;
    Options = options;
    _timeProvider = timeProvider;
    _logger = logger;
  }

  /// <summary>The threshold this tracker escalates at.</summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:Options_AreTheOnesItWasGivenAsync</tests>
  public DatabaseFailureEscalationOptions Options { get; }

  /// <summary>
  /// Records one transient database failure the worker met, and logs the escalation if this failure takes the run
  /// past the threshold for the first time.
  /// </summary>
  /// <param name="failure">The failure, as the shared classifier named it.</param>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:ARunPastBothThresholds_LogsOneErrorNamingHowLongTheDatabaseHasBeenFailingAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:FailuresAfterTheEscalation_DoNotLogItAgainAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:ManyFailuresInsideTheTime_DoNotEscalateAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:AFewFailuresOverALongTime_DoNotEscalateAsync</tests>
  public void Failed(TransientDatabaseFailure failure) {
    ArgumentNullException.ThrowIfNull(failure);
    var now = _timeProvider.GetTimestamp();
    int failures;
    TimeSpan failingFor;
    lock (_gate) {
      if (_failures == 0) {
        _runStartedAt = now;
      }
      _failures++;
      failures = _failures;
      failingFor = _timeProvider.GetElapsedTime(_runStartedAt, now);
      if (_escalated || failures < Options.ConsecutiveFailures || failingFor < Options.FailingFor) {
        return;
      }
      _escalated = true;
    }
    LogEscalated(_logger, _worker, failingFor, failures, failure.Reason, failure.SqlStateOrNone, failure.Cause);
  }

  /// <summary>
  /// Records that an iteration of the worker reached the database and succeeded, ending the run; logs the recovery
  /// when the run had escalated.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:ARecoveryAfterAnEscalation_LogsOneInformationLineAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:ARecoveryWithoutAnEscalation_LogsNothingAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs:ASuccess_EndsTheRun_SoTheNextOneStartsFromNothingAsync</tests>
  public void Succeeded() {
    int failures;
    long startedAt;
    lock (_gate) {
      if (_failures == 0) {
        return;
      }
      failures = _failures;
      startedAt = _runStartedAt;
      var escalated = _escalated;
      _failures = 0;
      _escalated = false;
      if (!escalated) {
        return;
      }
    }
    var failingFor = _timeProvider.GetElapsedTime(startedAt);
    LogRecovered(_logger, _worker, failingFor, failures);
  }

  [LoggerMessage(EventId = ESCALATED_EVENT_ID, EventName = ESCALATED_EVENT_NAME, Level = LogLevel.Error,
    Message = "{Worker} has been failing on transient database failures for {FailingFor} ({Failures} in a row; the "
            + "latest {Reason}, SQLSTATE {SqlState}); it keeps retrying and logs once more when the database answers")]
  static partial void LogEscalated(
    ILogger logger, string worker, TimeSpan failingFor, int failures, string reason, string sqlState, Exception exception);

  [LoggerMessage(EventId = RECOVERED_EVENT_ID, EventName = RECOVERED_EVENT_NAME, Level = LogLevel.Information,
    Message = "{Worker} reached the database again after {FailingFor} of transient database failures ({Failures} in a row)")]
  static partial void LogRecovered(ILogger logger, string worker, TimeSpan failingFor, int failures);
}
