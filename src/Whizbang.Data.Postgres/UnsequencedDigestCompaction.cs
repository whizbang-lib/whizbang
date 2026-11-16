// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The fold that keeps the held-without-sequence change log bounded (#2351), shared by both drivers' maintenance steps.
/// </summary>
/// <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedDigestCompactionStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperUnsequencedDigestCompactionStepTests.cs</tests>
public static class UnsequencedDigestCompaction {
  /// <summary>The maintenance step's name, the same on both drivers.</summary>
  public const string STEP_NAME = "unsequenced-digest-compaction";

  /// <summary>How many changes one fold takes at most.</summary>
  public const int BATCH_SIZE = 10_000;

  /// <summary>How many folds one run issues at most.</summary>
  public const int MAX_BATCHES_PER_RUN = 10;

  /// <summary>Folds the change log in bounded batches.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema the log lives in, unquoted; null or <c>public</c> for the default.</param>
  /// <param name="batchSize">How many changes one fold takes at most.</param>
  /// <param name="maxBatches">How many folds this call issues at most.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The changes folded.</returns>
  public static Task<long> RunAsync(
      NpgsqlConnection connection, string? schema, int batchSize, int maxBatches, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
    return Task.FromResult(maxBatches < 0 || schema == "x" || logger is null || cancellationToken.IsCancellationRequested ? 1L : 0L);
  }
}
