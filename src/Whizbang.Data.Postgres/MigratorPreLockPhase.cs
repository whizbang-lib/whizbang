// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Data.Postgres;

/// <summary>What the pre-lock phase asks of the initializer that runs it.</summary>
/// <param name="WaitAsync">
/// Waits for whoever is doing the schema work now. Given this instance's stage: a waiter watches the migrator's
/// duty, anyone else watches the schema lock (a sibling's bootstrap or DDL). Answers whether work remains for this
/// instance: for a waiter, true when the migrator is gone with the schema still behind and false when it finished;
/// for anyone else, true once the wait is over, whichever way it ended. A wait that throws is read as "work remains".
/// </param>
/// <param name="ElectAsync">Votes for the migrator duty again.</param>
/// <param name="PauseAsync">Waits before the given re-election (from 1), so a vote that keeps losing does not spin.</param>
/// <param name="WorkAsync">The pre-lock phases themselves: the stored-form scans, rewrites and index rebuild.</param>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
public sealed record PreLockPhaseSteps(
  Func<SchemaStage, CancellationToken, Task<bool>> WaitAsync,
  Func<CancellationToken, Task<SchemaStaging>> ElectAsync,
  Func<int, CancellationToken, Task> PauseAsync,
  Func<CancellationToken, Task> WorkAsync);

/// <summary>
/// Runs the phases that come before the schema lock (the declared-key scan, the stored-form rewrites, the index
/// rebuild and the settle scan) on the one instance that holds the migrator role, and makes every other instance wait
/// for its result (#2127).
/// </summary>
/// <remarks>
/// <para>
/// These phases take no schema lock for most of what they do, so the role is the only thing that keeps two instances
/// from scanning and rewriting the same tables, and from building and dropping the same indexes concurrently. A
/// waiter whose migrator is gone therefore does not start the work: it votes for the role first, and does the work
/// only once it holds it. Losing that vote makes it a waiter again, for whoever won.
/// </para>
/// <para>
/// The migrator verifies its grant before it starts. A grant it no longer holds is given up and voted for again,
/// rather than worked under. An instance that could not be staged at all (no election was possible) still does the
/// work, as every instance did before an election existed: the schema lock that follows still excludes.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorPreLockPhaseTests.cs</tests>
public static partial class MigratorPreLockPhase {
  /// <summary>The pause before re-election <paramref name="attempt"/> (from 1): the deferral's poll backoff.</summary>
  /// <param name="attempt">Which re-election, from 1.</param>
  /// <returns>250 ms doubling to a 5 s ceiling.</returns>
  public static TimeSpan ReelectionDelay(int attempt) {
    var delay = SchemaMigrationDeferral.PollFloor;
    for (var i = 1; i < attempt; i++) {
      delay = SchemaMigrationDeferral.NextPollDelay(delay);
    }
    return delay;
  }

  /// <summary>Runs the pre-lock phases on the role holder; waits, or takes the role over, everywhere else.</summary>
  /// <param name="duty">The migrator duty this instance holds, if any; replaced when this instance takes it over.</param>
  /// <param name="steps">The waits, votes and work, supplied by the initializer.</param>
  /// <param name="schema">The schema, for reporting.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes once the work is done, or another instance did it.</returns>
  public static async Task RunAsync(
      MigratorDuty duty, PreLockPhaseSteps steps, string schema, ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(duty);
    ArgumentNullException.ThrowIfNull(steps);
    var log = logger ?? NullLogger.Instance;
    var votes = 0;

    while (true) {
      cancellationToken.ThrowIfCancellationRequested();
      var stage = duty.Stage;
      var workRemains = await _waitAsync(steps, stage, schema, log, cancellationToken).ConfigureAwait(false);

      if (stage == SchemaStage.Waiter) {
        if (!workRemains) {
          // The migrator finished: the schema pass that follows finds nothing to apply.
          return;
        }
        LogMigratorGone(log, schema);
        votes = await _voteAsync(duty, steps, votes, cancellationToken).ConfigureAwait(false);
        if (duty.Stage == SchemaStage.Waiter) {
          LogAnotherHolder(log, schema, duty.Staging.Detail);
        }
        continue;
      }

      if (stage == SchemaStage.Migrator && !await duty.Grant!.VerifyStillHeldAsync(cancellationToken).ConfigureAwait(false)) {
        // Lost to a takeover, or not reachable now: either way not a role to work under. Given up, and voted for again.
        LogRoleLost(log, schema);
        await duty.ReleaseAsync().ConfigureAwait(false);
        votes = await _voteAsync(duty, steps, votes, cancellationToken).ConfigureAwait(false);
        continue;
      }

      if (stage == SchemaStage.Migrator) {
        LogRoleHolderRuns(log, schema);
      } else {
        LogUnstagedRuns(log, schema, duty.Staging.Detail);
      }
      await steps.WorkAsync(cancellationToken).ConfigureAwait(false);
      return;
    }
  }

  /// <summary>Votes again, after a pause from the second vote on; the duty then holds whatever the vote decided.</summary>
  private static async Task<int> _voteAsync(MigratorDuty duty, PreLockPhaseSteps steps, int votes, CancellationToken cancellationToken) {
    if (votes > 0) {
      await steps.PauseAsync(votes, cancellationToken).ConfigureAwait(false);
    }
    await duty.HoldAsync(await steps.ElectAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    return votes + 1;
  }

  /// <summary>The initializer's wait; one that throws is read as "work remains", as the initializer always did.</summary>
  private static async Task<bool> _waitAsync(
      PreLockPhaseSteps steps, SchemaStage stage, string schema, ILogger log, CancellationToken cancellationToken) {
    try {
      return await steps.WaitAsync(stage, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogWaitFailed(log, ex, schema);
      return true;
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "This instance holds the migrator role for schema {Schema}; it brings stored documents to their current form before the schema pass")]
  static partial void LogRoleHolderRuns(ILogger logger, string schema);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "No migrator role is held for schema {Schema} ({Detail}); this instance brings stored documents to their current form, as every instance did before an election existed")]
  static partial void LogUnstagedRuns(ILogger logger, string schema, string detail);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "The migrator of schema {Schema} is gone and the schema is still behind; voting for the migrator role before doing its work")]
  static partial void LogMigratorGone(ILogger logger, string schema);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information,
    Message = "Another instance won the migrator role for schema {Schema}; waiting for its result: {Detail}")]
  static partial void LogAnotherHolder(ILogger logger, string schema, string detail);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "This instance no longer holds the migrator role for schema {Schema}; voting for it again rather than bringing stored documents to their current form without it")]
  static partial void LogRoleLost(ILogger logger, string schema);

  [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
    Message = "Could not wait for the instance doing the schema work for {Schema}; going on as if it were gone")]
  static partial void LogWaitFailed(ILogger logger, Exception exception, string schema);
}
