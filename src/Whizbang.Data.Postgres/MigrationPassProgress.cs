// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The progress lines of one schema pass, counted against the whole pass, as both Postgres drivers write them (#2579):
/// <c>Migration X of Y - Started</c>, then <c>Succeeded</c>, <c>Skipped</c> or <c>Failed</c> for each step, and the pass
/// ends with <c>Migration complete</c> or <c>Migration stopped at</c>. One reporter, so an operator watching either
/// driver's start sees the same lines, and a driver cannot drift into saying nothing while it migrates.
/// </summary>
/// <remarks>
/// The caller decides what a step is and when it is counted; this keeps the count, the clock and the wording. A pass
/// that was never begun (<see cref="Counted"/> false) is not counted, and what it reports is the caller's to skip.
/// </remarks>
/// <docs>operations/infrastructure/migrations#progress-of-long-startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs:AFreshStart_EntersMigrate_AndLogsEveryStep_OnEitherDriverAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupMigrationStepsTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaStepRunnerTests.cs</tests>
/// <param name="logger">Where the lines go; none when null.</param>
/// <param name="time">The clock durations are measured on; the system clock when null.</param>
internal sealed partial class MigrationPassProgress(ILogger? logger, TimeProvider? time) {
  private readonly ILogger _logger = logger ?? NullLogger.Instance;
  private readonly TimeProvider _time = time ?? TimeProvider.System;
  private long _passStarted;

  /// <summary>Every step of the pass, including those it skips; zero before the pass begins.</summary>
  public int Total { get; private set; }

  /// <summary>The step the pass is on, from 1.</summary>
  public int Position { get; private set; }

  /// <summary>Whether a pass has begun, so its steps are counted.</summary>
  public bool Counted => Total > 0;

  private int _applied;
  private int _skipped;

  /// <summary>Begins counting a pass of <paramref name="totalSteps"/> steps.</summary>
  /// <param name="totalSteps">Every step of the pass, including those it will skip.</param>
  public void Begin(int totalSteps) {
    Total = totalSteps;
    Position = 0;
    _applied = 0;
    _skipped = 0;
    _passStarted = _time.GetTimestamp();
  }

  /// <summary>Moves to the next step.</summary>
  public void Next() => Position++;

  /// <summary>Moves to the next step and counts it as one the plan already found current.</summary>
  /// <param name="step">The step's name.</param>
  public void Skip(string step) {
    Next();
    Current(step);
  }

  /// <summary>The current step began its work.</summary>
  /// <param name="step">The step's name.</param>
  public void Started(string step) => LogStarted(_logger, Position, Total, step);

  /// <summary>The current step applied its work, which began at <paramref name="startedAt"/>.</summary>
  /// <param name="step">The step's name.</param>
  /// <param name="startedAt">A timestamp from <see cref="Timestamp"/>, taken when the step began.</param>
  public void Succeeded(string step, long startedAt) {
    _applied++;
    LogSucceeded(_logger, Position, Total, step, new Duration(_time.GetElapsedTime(startedAt)));
  }

  /// <summary>The current step found its work already done.</summary>
  /// <param name="step">The step's name.</param>
  public void Current(string step) {
    _skipped++;
    LogSkipped(_logger, Position, Total, step);
  }

  /// <summary>A timestamp on this reporter's clock, for <see cref="Succeeded"/>.</summary>
  /// <returns>The timestamp.</returns>
  public long Timestamp() => _time.GetTimestamp();

  /// <summary>The current step failed, and the pass stops there.</summary>
  /// <param name="step">The step's name.</param>
  /// <param name="failure">Why.</param>
  /// <param name="retryDelay">When the initializer retries, or null when it will not.</param>
  public void Failed(string step, Exception failure, TimeSpan? retryDelay) {
    var text = FailureText(failure);
    var space = text.IndexOf(' ', StringComparison.Ordinal);
    var (state, message) = (text[..space], text[(space + 1)..]);
    if (retryDelay is { } delay) {
      var at = (_time.GetUtcNow() + delay).ToString("O", CultureInfo.InvariantCulture);
      LogFailedWillRetry(_logger, failure, Position, Total, step, state, message, new Duration(delay), at);
    } else {
      LogFailed(_logger, failure, Position, Total, step, state, message);
    }
    LogStopped(_logger, Position, Total, step, text);
  }

  /// <summary>The current step lost a deadlock and runs again after <paramref name="delay"/>.</summary>
  /// <param name="step">The step's name.</param>
  /// <param name="deadlock">The deadlock.</param>
  /// <param name="delay">The back-off before it runs again.</param>
  /// <param name="deadlocks">How many deadlocks this step has lost.</param>
  /// <param name="maxDeadlocks">How many it may lose before it fails.</param>
  public void Deadlocked(string step, PostgresException deadlock, TimeSpan delay, int deadlocks, int maxDeadlocks) =>
    LogDeadlocked(_logger, Position, Total, step, deadlock.SqlState, deadlock.MessageText, new Duration(delay), deadlocks, maxDeadlocks);

  /// <summary>Every step of the pass ended; logs the summary.</summary>
  public void Complete() =>
    LogComplete(_logger, Total, Total, _applied, _skipped, new Duration(_time.GetElapsedTime(_passStarted)));

  /// <summary>A failure as the ledger and the step lines give it: the SQLSTATE, or the exception's type, and its message.</summary>
  /// <param name="exception">The failure.</param>
  /// <returns>For example <c>40P01 deadlock detected</c>.</returns>
  public static string FailureText(Exception exception) {
    ArgumentNullException.ThrowIfNull(exception);
    for (var inner = exception; inner is not null; inner = inner.InnerException) {
      if (inner is PostgresException postgres) {
        return $"{postgres.SqlState} {postgres.MessageText}";
      }
    }
    return $"{TypeNameFormatter.ForLog(exception.GetType())} {exception.Message}";
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Migration {Position} of {Total} - Started : {Step}")]
  private static partial void LogStarted(ILogger logger, int position, int total, string step);

  [LoggerMessage(Level = LogLevel.Information, Message = "Migration {Position} of {Total} - Succeeded : {Step} ({Duration})")]
  private static partial void LogSucceeded(ILogger logger, int position, int total, string step, Duration duration);

  [LoggerMessage(Level = LogLevel.Information, Message = "Migration {Position} of {Total} - Skipped : {Step} (already current)")]
  private static partial void LogSkipped(ILogger logger, int position, int total, string step);

  [LoggerMessage(Level = LogLevel.Error, Message = "Migration {Position} of {Total} - Failed : {Step} : {SqlState} {Error}")]
  private static partial void LogFailed(ILogger logger, Exception ex, int position, int total, string step, string sqlState, string error);

  [LoggerMessage(
      Level = LogLevel.Error,
      Message = "Migration {Position} of {Total} - Failed : {Step} : {SqlState} {Error}; retrying in {Delay} at {RetryAt}")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "A LoggerMessage method takes the logger, the exception and one parameter per placeholder, and the line names seven. A parameter object would change the structured fields the line logs.")]
  private static partial void LogFailedWillRetry(
      ILogger logger, Exception ex, int position, int total, string step, string sqlState, string error, Duration delay, string retryAt);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Migration {Position} of {Total} - Deadlocked : {Step} : {SqlState} {Error}; retrying the step in {Delay} (deadlock {Deadlock} of {MaxDeadlocks})")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "A LoggerMessage method takes the logger and one parameter per placeholder, and the line names eight. A parameter object would change the structured fields the line logs.")]
  private static partial void LogDeadlocked(
      ILogger logger, int position, int total, string step, string sqlState, string error, Duration delay, int deadlock, int maxDeadlocks);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Migration complete: {Succeeded} of {Total} succeeded ({Applied} applied, {Skipped} skipped) in {Duration}")]
  private static partial void LogComplete(ILogger logger, int succeeded, int total, int applied, int skipped, Duration duration);

  [LoggerMessage(Level = LogLevel.Error, Message = "Migration stopped at {Position} of {Total}: {Step} : {Error}")]
  private static partial void LogStopped(ILogger logger, int position, int total, string step, string error);

  /// <summary>A step's or a pass's duration as the step lines give it: seconds to the millisecond, as in <c>1.234s</c>.</summary>
  /// <param name="elapsed">The duration.</param>
  internal readonly struct Duration(TimeSpan elapsed) {
    /// <inheritdoc/>
    public override string ToString() => elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";
  }
}
