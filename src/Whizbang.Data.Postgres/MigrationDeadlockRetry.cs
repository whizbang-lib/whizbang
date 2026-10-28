// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How a migration step that loses a deadlock is retried (#1636), the same on both drivers: in place, after a short
/// back-off, said at Warning, up to <see cref="MAX_RETRIES"/> times; after that it fails like any other step.
/// </summary>
/// <remarks>
/// A deadlock is the server choosing a victim, not a defect in the step: the step's transaction has rolled back, and
/// running it again is the expected recovery. Before this, a deadlocked step failed at Error, ended the attempt, and
/// the whole initializer waited out its own back-off before resuming, which is how a fresh database took minutes.
/// </remarks>
/// <docs>operations/infrastructure/migrations#a-deadlock-is-retried-in-place</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaStepRunnerTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaAgainstRunningWorkersTests.cs</tests>
internal static class MigrationDeadlockRetry {
  /// <summary>How many deadlocks one step is retried in place for.</summary>
  internal const int MAX_RETRIES = 5;

  private const string DEADLOCK_DETECTED = "40P01";

  /// <summary>The server's deadlock error in <paramref name="exception"/> or its inner exceptions, or null.</summary>
  internal static PostgresException? AsDeadlock(Exception exception) {
    for (Exception? inner = exception; inner is not null; inner = inner.InnerException) {
      if (inner is PostgresException { SqlState: DEADLOCK_DETECTED } deadlock) {
        return deadlock;
      }
    }
    return null;
  }

  /// <summary>
  /// The wait before retrying a step after its <paramref name="deadlock"/>th deadlock: 100 ms doubling to a 2 s cap,
  /// plus up to half again of jitter, so two sessions that deadlocked each other do not meet again at once.
  /// </summary>
  internal static TimeSpan Delay(int deadlock, Random random) {
    var delay = Math.Min(100 * (1 << Math.Min(deadlock - 1, 20)), 2_000);
#pragma warning disable CA5394, S2245 // Jitter spreads two sessions' retries apart; it protects nothing.
    return TimeSpan.FromMilliseconds(delay + random.Next(0, delay / 2));
#pragma warning restore CA5394, S2245
  }
}
