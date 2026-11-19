// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The fold that keeps the held-without-sequence change log bounded (#2351), shared by both drivers' maintenance steps:
/// it folds every origin's oldest pending changes into the per-type totals and deletes them.
/// </summary>
/// <remarks>
/// <para>
/// The row triggers on the event store write one change per held event that joins or leaves the set, and an integrity
/// answer folds only its own origin's changes. An origin no consumer asks about would grow the log for good, and every
/// answer for an origin that is asked about would start with its backlog. This fold runs whoever asked.
/// </para>
/// <para>
/// Bounded per run: at most <c>maxBatches</c> folds of <c>batchSize</c> changes, stopping at the first fold that finds
/// less than a full batch. Every instance may run it: a fold skips the changes another holds rather than waiting on them,
/// so concurrent runs share the work instead of repeating it. One implementation for both drivers, so a driver cannot
/// ship the triggers without the fold that bounds what they write.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#the-change-log-stays-bounded</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnsequencedDigestCompactionStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperUnsequencedDigestCompactionStepTests.cs</tests>
public static partial class UnsequencedDigestCompaction {
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
  public static async Task<long> RunAsync(
      NpgsqlConnection connection, string? schema, int batchSize, int maxBatches, ILogger? logger,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
    var function = PgIdentifier.QualifyPrefix(schema) + "wh_compact_unsequenced_digest_changes";
    var folded = 0L;
    for (var batch = 0; batch < maxBatches; batch++) {
      var changes = await _foldBatchAsync(connection, function, batchSize, cancellationToken).ConfigureAwait(false);
      folded += changes;
      if (changes < batchSize) {
        break;
      }
    }
    if (folded > 0) {
      LogCompacted(logger ?? NullLogger.Instance, folded);
    }
    return folded;
  }

  /// <summary>One fold of at most <paramref name="batchSize"/> changes; returns how many it folded.</summary>
  /// <remarks>
  /// Its own method so the command's disposal is not part of the loop's exit: an <c>await using</c> inside the loop puts
  /// the compiler's disposal branches on the <c>break</c>, where they read as conditions of the batch test.
  /// </remarks>
  private static async Task<long> _foldBatchAsync(
      NpgsqlConnection connection, string function, int batchSize, CancellationToken cancellationToken) {
    await using var cmd = connection.CreateCoordinatorCommand();
#pragma warning disable S2077 // the function name is the caller's own schema, quoted by PgIdentifier
    cmd.CommandText = $"SELECT {function}(@limit)";
#pragma warning restore S2077
    cmd.Parameters.AddWithValue("limit", batchSize);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
      CultureInfo.InvariantCulture);
  }

  [LoggerMessage(Level = LogLevel.Debug, Message = "Folded {Count} held-without-sequence digest change(s) into their totals")]
  private static partial void LogCompacted(ILogger logger, long count);
}
