using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How a deferral ended.
/// </summary>
/// <docs>operations/infrastructure/migrations</docs>
public enum SchemaDeferralOutcome {
  /// <summary>
  /// The migrating instance finished and the schema is current, so this instance has nothing to
  /// apply.
  /// </summary>
  AnotherInstanceMigrated,

  /// <summary>
  /// Nothing holds the schema lock any more and the schema is still behind, so whoever was
  /// migrating is not coming back and this instance must take the work over.
  /// </summary>
  MigratingInstanceGone,
}

/// <summary>
/// What an instance does after losing the race to migrate a schema: wait for the winner's result
/// instead of queuing behind it.
/// </summary>
/// <remarks>
/// <para>
/// The advisory lock already elects a migrator, because exactly one instance can win
/// <c>pg_try_advisory_xact_lock</c>. What the losers do with that answer is the part worth
/// designing. Re-contending for the lock buys nothing they want: the prize is an in-lock hash
/// re-check that finds the work already done, and the cost is a transaction per attempt per
/// replica, each one pinning a pooled backend for the round trip. Waiting for the schema to be
/// marked current instead costs one lock-free query per poll and no transaction at all.
/// </para>
/// <para>
/// Two questions, asked in this order. Is the schema current yet? If so the winner committed and
/// there is nothing left to do. If not, does anything still hold the lock? A held lock means the
/// migrator is alive and working, and the right move is to keep waiting however long that takes,
/// because a large migration legitimately runs for minutes and a deadline short enough to be
/// useful would be short enough to be wrong. A released lock with the schema still behind means
/// the migrator died, and this instance takes over.
/// </para>
/// <para>
/// Order matters: cleanliness is checked first because a committing winner releases its lock and
/// marks the schema current in the same instant. Asking about the lock first would read "released"
/// and mistake a successful migration for a dead migrator, which costs a redundant pass rather than
/// correctness, but costs it on every single startup.
/// </para>
/// <para>
/// This needs no elector, no instance registration, and no connection but the one already open,
/// which is why the lock is the election here rather than a duty. A duty is recorded through a
/// function that schema initialization itself creates, so at this point in startup there is
/// nothing yet to ask.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaMigrationDeferralTests.cs</tests>
public static class SchemaMigrationDeferral {
  /// <summary>The first wait between polls.</summary>
  /// <remarks>
  /// Short, because the common case is a small migration that is over almost at once and every
  /// unnecessary fraction of a second here is added to a cold start.
  /// </remarks>
  internal static readonly TimeSpan PollFloor = TimeSpan.FromMilliseconds(250);

  /// <summary>The longest wait between polls.</summary>
  /// <remarks>
  /// Bounded so a long migration is still noticed promptly on completion, and so a dead migrator is
  /// detected within one ceiling rather than after an unbounded backoff. Low enough to stay
  /// responsive, high enough that a fleet waiting out a genuinely slow migration is not polling in
  /// a tight loop.
  /// </remarks>
  internal static readonly TimeSpan PollCeiling = TimeSpan.FromSeconds(5);

  /// <summary>
  /// The wait after one that lasted <paramref name="current"/>.
  /// </summary>
  /// <param name="current">The previous wait.</param>
  /// <returns>Twice it, never past <see cref="PollCeiling"/>.</returns>
  internal static TimeSpan NextPollDelay(TimeSpan current) {
    var doubled = current.Ticks <= 0 ? PollFloor.Ticks : current.Ticks * 2;
    return TimeSpan.FromTicks(Math.Min(doubled, PollCeiling.Ticks));
  }

  /// <summary>
  /// Waits for the instance that won the schema lock to finish, or reports that it is gone.
  /// </summary>
  /// <param name="isSchemaCleanAsync">
  /// Whether the schema is current. Asked repeatedly, so it must be cheap and must take no lock. A
  /// failure is treated as "not current": the schema cannot be assumed good because the question
  /// could not be answered.
  /// </param>
  /// <param name="isLockHeldAsync">
  /// Whether another session still holds the schema lock. A failure ends the deferral, because
  /// waiting on a condition that can no longer be observed is how a fleet stalls silently.
  /// </param>
  /// <param name="timeProvider">The clock the waits are measured on.</param>
  /// <param name="schema">The schema being waited on, for reporting.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Which of the two endings occurred.</returns>
  public static async Task<SchemaDeferralOutcome> DeferAsync(
      Func<CancellationToken, Task<bool>> isSchemaCleanAsync,
      Func<CancellationToken, Task<bool>> isLockHeldAsync,
      TimeProvider timeProvider,
      string schema,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(isSchemaCleanAsync);
    ArgumentNullException.ThrowIfNull(isLockHeldAsync);
    ArgumentNullException.ThrowIfNull(timeProvider);

    // Non-null so the log calls below need no guard. NullLogger discards, at no cost.
    var log = logger ?? NullLogger.Instance;

    var delay = PollFloor;
    var polls = 0;

    while (true) {
      cancellationToken.ThrowIfCancellationRequested();

      if (await _isCurrentAsync(isSchemaCleanAsync, log, schema, cancellationToken)
          .ConfigureAwait(false)) {
        SchemaMigrationDeferralLog.MigratorFinished(log, schema, polls);
        return SchemaDeferralOutcome.AnotherInstanceMigrated;
      }

      bool held;
      try {
        held = await isLockHeldAsync(cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        // Whether anyone is migrating can no longer be observed, so waiting would be a guess with
        // the whole fleet's startup riding on it. Contending for the lock is the guess that cannot
        // strand anything: it is what every instance did before this existed.
        SchemaMigrationDeferralLog.LockProbeFailed(log, ex, schema);
        return SchemaDeferralOutcome.MigratingInstanceGone;
      }

      if (!held) {
        // The lock is gone and the schema is still behind. Whoever held it is not coming back.
        SchemaMigrationDeferralLog.MigratorGone(log, schema, polls);
        return SchemaDeferralOutcome.MigratingInstanceGone;
      }

      if (polls == 0) {
        // Once, not once per poll: a fleet waiting out a long migration would otherwise fill its
        // logs with the fact that it is still waiting.
        SchemaMigrationDeferralLog.DeferringToMigrator(log, schema);
      }

      polls++;
      await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
      delay = NextPollDelay(delay);
    }
  }

  /// <summary>
  /// Asks whether the schema is current, treating an unanswerable question as "no".
  /// </summary>
  /// <remarks>
  /// The tracking tables the answer comes from are created by the very migration being waited for,
  /// so on an empty database the first few attempts legitimately fail. Reading that as "current"
  /// would send an instance off to serve against a schema that does not exist yet.
  /// </remarks>
  private static async Task<bool> _isCurrentAsync(
      Func<CancellationToken, Task<bool>> isSchemaCleanAsync,
      ILogger log,
      string schema,
      CancellationToken cancellationToken) {
    try {
      return await isSchemaCleanAsync(cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      SchemaMigrationDeferralLog.CurrencyProbeFailed(log, ex, schema);
      return false;
    }
  }
}

/// <summary>Source-generated logging for the deferral.</summary>
internal static partial class SchemaMigrationDeferralLog {
  [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Information,
      Message = "Another instance is migrating schema {Schema}; waiting for its result rather than "
              + "contending for the lock")]
  public static partial void DeferringToMigrator(ILogger logger, string schema);

  [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Information,
      Message = "Schema {Schema} was brought up to date by another instance after {Polls} check(s); "
              + "nothing left to apply")]
  public static partial void MigratorFinished(ILogger logger, string schema, int polls);

  [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Warning,
      Message = "Nothing holds the schema {Schema} lock after {Polls} check(s) and the schema is "
              + "still behind, so the instance that was migrating it is gone; taking the work over")]
  public static partial void MigratorGone(ILogger logger, string schema, int polls);

  [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Warning,
      Message = "Could not tell whether another instance still holds the schema {Schema} lock; "
              + "contending for it rather than waiting on a condition that cannot be observed")]
  public static partial void LockProbeFailed(ILogger logger, Exception exception, string schema);

  [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Debug,
      Message = "Could not tell whether schema {Schema} is up to date; treating it as behind and "
              + "asking again (expected while the tracking tables are still being created)")]
  public static partial void CurrencyProbeFailed(ILogger logger, Exception exception, string schema);
}
