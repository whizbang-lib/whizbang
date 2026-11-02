// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Follows one worker's run of transient database failures and says, once, when it has gone on long enough to be
/// worth an alert, and once more when the database answers again.
/// </summary>
/// <docs>fundamentals/workers/transient-database-failures#when-the-database-keeps-failing</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DatabaseFailureEscalationTests.cs</tests>
public sealed class DatabaseFailureEscalation {
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

  private int _failures;

  /// <summary>Creates the tracker for one worker.</summary>
  /// <param name="worker">The worker's name, as the escalation and the recovery lines write it.</param>
  /// <param name="options">The threshold.</param>
  /// <param name="timeProvider">The clock the run's length is measured on.</param>
  /// <param name="logger">The worker's logger; both lines are logged under its category.</param>
  public DatabaseFailureEscalation(
      string worker, DatabaseFailureEscalationOptions options, TimeProvider timeProvider, ILogger logger) {
    ArgumentNullException.ThrowIfNull(worker);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);
  }

  /// <summary>Records one transient database failure the worker met.</summary>
  /// <param name="failure">The failure, as the shared classifier named it.</param>
  public void Failed(TransientDatabaseFailure failure) {
    ArgumentNullException.ThrowIfNull(failure);
    _failures++;
  }

  /// <summary>Records that an iteration of the worker reached the database and succeeded.</summary>
  public void Succeeded() {
    if (_failures == 0) {
      return;
    }
    _failures = 0;
  }
}
