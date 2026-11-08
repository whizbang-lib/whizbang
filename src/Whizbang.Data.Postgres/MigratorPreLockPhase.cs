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
    _ = schema;
    _ = logger ?? NullLogger.Instance;
    bool workRemains;
    try {
      workRemains = await steps.WaitAsync(duty.Stage, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      workRemains = true;
    }
    if (duty.Stage != SchemaStage.Waiter || workRemains) {
      await steps.WorkAsync(cancellationToken).ConfigureAwait(false);
    }
  }
}
