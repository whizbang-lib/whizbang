// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>How much of a batched rewrite one run does: at most so many batches, and for at most so long.</summary>
/// <param name="MaxBatches">The batches the run stops after.</param>
/// <param name="MaxDuration">
/// The time the run stops after: checked between batches, and the last batch's statement is cut off at what is left of
/// it, so a run never goes past it by a whole command timeout.
/// </param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-rewrite</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs:ABatchThatOutrunsTheBudget_IsCutOffAtWhatIsLeftOfItAsync</tests>
public sealed record BatchedRewriteBudget(int MaxBatches, TimeSpan MaxDuration) {
  /// <summary>
  /// The slice startup runs: four fifths of the command timeout, so starting is delayed by no more than a schema pass
  /// already allows for, with no limit on batches.
  /// </summary>
  /// <param name="commandTimeoutSeconds">The schema command timeout.</param>
  /// <returns>The budget.</returns>
  public static BatchedRewriteBudget ForStartup(int commandTimeoutSeconds) =>
    new(int.MaxValue, TimeSpan.FromMilliseconds(commandTimeoutSeconds * 800L));

  /// <summary>The slice one maintenance cycle runs: thirty seconds, with no limit on batches.</summary>
  public static BatchedRewriteBudget ForMaintenance { get; } = new(int.MaxValue, TimeSpan.FromSeconds(30));
}

/// <summary>What one run of a batched rewrite came to.</summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-rewrite</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs</tests>
public enum BatchedRewriteOutcome {
  /// <summary>Every rewrite is settled; nothing is left to run.</summary>
  Settled,

  /// <summary>The budget ran out, or the lock was held elsewhere, with rewrites still unsettled: the next run carries on.</summary>
  Unfinished,

  /// <summary>
  /// The fleet is not ready for the form the rewrite writes (a live instance cannot read it, or still writes the form it
  /// replaces): asked before every batch, so the run stopped before the batch it was not ready for.
  /// </summary>
  HeldForFleet,
}

/// <summary>One run of a batched rewrite.</summary>
/// <param name="Outcome">What it came to.</param>
/// <param name="Batches">The batches it ran.</param>
/// <param name="Unsettled">The rewrites still unsettled after it, by journal name.</param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-rewrite</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs</tests>
public sealed record BatchedRewriteRun(BatchedRewriteOutcome Outcome, int Batches, IReadOnlyList<string> Unsettled);

/// <summary>The rewrites a batched run works on, and where.</summary>
/// <param name="ConnectionFactory">Produces the connection the run uses.</param>
/// <param name="Schema">The schema the journal lives in, unquoted.</param>
/// <param name="LockId">The rewrite's own advisory lock key (<see cref="BatchedRewriteLockKey"/>).</param>
/// <param name="Rewrites">Each rewrite's journal name and one-batch statement.</param>
/// <param name="CommandTimeoutSeconds">The timeout for one batch.</param>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
public sealed record BatchedRewriteRequest(
    Func<NpgsqlConnection> ConnectionFactory,
    string Schema,
    long LockId,
    IReadOnlyList<(string Name, string Sql)> Rewrites,
    int CommandTimeoutSeconds);

/// <summary>
/// Runs stored-form rewrites a batch at a time, each batch in a transaction of its own under the schema lock, so a
/// table of any size is converted across as many runs as it takes and a restart redoes nothing (#2518).
/// </summary>
/// <remarks>
/// <para>
/// Each rewrite is a statement that converts one batch from the key its journal row records and records where it stopped
/// (<c>resume_key</c> in <c>wh_stored_form_migrations</c>), settling the row once a complete pass converts nothing. This
/// runs those statements: the unsettled rewrites in turn, one batch each, until every one settles or the budget is
/// spent, so each table's progress is its own and one large table does not hold the others back.
/// </para>
/// <para>
/// Every batch takes the rewrite's own lock (<see cref="BatchedRewriteLockKey"/>) at transaction scope, so no two
/// instances convert at once. It is not the schema lock: a starting instance reads that one as a migration in progress
/// and waits, and a batch is not one. A batch that cannot take it ends the run; the next run, at the next startup or
/// maintenance cycle, carries on. The statement also locks its journal row, so a second session that reached it reads the
/// key the first recorded.
/// </para>
/// <para>
/// A conversion the fleet is not ready for waits: <c>hold</c> is asked before every batch, and when it gives a reason the
/// run stops there, so an instance that cannot read the new form and joins during a run stops it before the next batch.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs</tests>
public static class BatchedRewriteRunner {
  /// <summary>Runs the unsettled rewrites within the budget.</summary>
  /// <param name="request">The rewrites, and where.</param>
  /// <param name="budget">How much this run does.</param>
  /// <param name="hold">
  /// Why the fleet is not ready for the conversion, asked before each batch: null when it is. A null function holds
  /// nothing.
  /// </param>
  /// <param name="applier">Who the conversions record as applying them.</param>
  /// <param name="timeProvider">The clock the budget is measured on.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What the run came to.</returns>
  public static async Task<BatchedRewriteRun> RunAsync(
      BatchedRewriteRequest request,
      BatchedRewriteBudget budget,
      Func<NpgsqlConnection, CancellationToken, Task<string?>>? hold,
      StoredFormApplier? applier,
      TimeProvider timeProvider,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(budget);
    ArgumentNullException.ThrowIfNull(timeProvider);
    var log = logger ?? NullLogger.Instance;

    if (request.Rewrites.Count == 0) {
      return new BatchedRewriteRun(BatchedRewriteOutcome.Settled, 0, []);
    }

    await using var connection = request.ConnectionFactory();
    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    var schema = string.IsNullOrEmpty(request.Schema) ? "public" : request.Schema;
    var batch = new Batch(connection, $"{PgIdentifier.Quote(schema)}.{StoredFormMigrationSql.JOURNAL}", request, applier, log);

    var pending = await _unsettledAsync(batch, cancellationToken).ConfigureAwait(false);
    if (pending.Count == 0) {
      return new BatchedRewriteRun(BatchedRewriteOutcome.Settled, 0, []);
    }

    var (batches, held) = await _roundsAsync(batch, pending, budget, hold, timeProvider, cancellationToken).ConfigureAwait(false);
    var unsettled = await _unsettledAsync(batch, cancellationToken).ConfigureAwait(false);
    if (unsettled.Count == 0) {
      return new BatchedRewriteRun(BatchedRewriteOutcome.Settled, batches, []);
    }
    if (held is not null) {
      BatchedRewriteLog.HeldForFleet(log, unsettled.Count, held);
      return new BatchedRewriteRun(BatchedRewriteOutcome.HeldForFleet, batches, [.. unsettled.Select(p => p.Name)]);
    }
    BatchedRewriteLog.Unfinished(log, batches, unsettled.Count);
    return new BatchedRewriteRun(BatchedRewriteOutcome.Unfinished, batches, [.. unsettled.Select(p => p.Name)]);
  }

  /// <summary>What every batch of one run shares.</summary>
  private sealed record Batch(
      NpgsqlConnection Connection, string Journal, BatchedRewriteRequest Request, StoredFormApplier? Applier, ILogger Log);

  private enum Step {
    Converted,
    Settled,
    LockHeldElsewhere,
    BudgetSpent,
    Failed,
  }

  /// <summary>
  /// Gives each unsettled rewrite one batch per round until all settle, the budget is spent, the lock is held elsewhere
  /// or the hold gives a reason, then analyzes the tables the batches converted rows in.
  /// </summary>
  /// <returns>The batches run, and the hold's reason when it stopped the run.</returns>
  private static async Task<(int Batches, string? Held)> _roundsAsync(
      Batch batch, List<(string Name, string Sql)> pending, BatchedRewriteBudget budget,
      Func<NpgsqlConnection, CancellationToken, Task<string?>>? hold, TimeProvider timeProvider,
      CancellationToken cancellationToken) {
    var started = timeProvider.GetTimestamp();
    var batches = 0;
    var converted = new HashSet<string>(StringComparer.Ordinal);
    bool spent() => batches >= budget.MaxBatches || timeProvider.GetElapsedTime(started) >= budget.MaxDuration;
    void relay(object sender, NpgsqlNoticeEventArgs e) => BatchedRewriteLog.Notice(batch.Log, e.Notice.MessageText);
    batch.Connection.Notice += relay;
    try {
      var queue = new Queue<(string Name, string Sql)>(pending);
      while (queue.Count > 0 && !spent()) {
        // Asked before every batch: an instance that cannot read the new form can join while the run is going.
        if (hold is not null && await hold(batch.Connection, cancellationToken).ConfigureAwait(false) is { } reason) {
          return (batches, reason);
        }
        var rewrite = queue.Dequeue();
        var left = budget.MaxDuration - timeProvider.GetElapsedTime(started);
        var (step, marked) = await _batchAsync(batch, rewrite, left, cancellationToken).ConfigureAwait(false);
        if (step is Step.LockHeldElsewhere or Step.BudgetSpent) {
          break;
        }
        // Back of the queue while unsettled, so each rewrite gets a batch in turn. A failed batch is reported and rolled
        // back: this run moves on, and the next run tries it again.
        batches += step == Step.Failed ? 0 : 1;
        converted.UnionWith(marked);
        if (step == Step.Converted) {
          queue.Enqueue(rewrite);
        }
      }
      return (batches, null);
    } finally {
      batch.Connection.Notice -= relay;
      await _analyzeAsync(batch, converted).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Analyzes the tables the run converted rows in, once each (#1004): a batch's mark lives only in its own transaction,
  /// so the marks are gathered as each batch commits.
  /// </summary>
  private static async Task _analyzeAsync(Batch batch, HashSet<string> converted) {
    if (converted.Count > 0) {
      await IndexStatistics.AnalyzeAsync(batch.Connection, [.. converted], batch.Log, CancellationToken.None).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// One batch of one rewrite, in its own transaction under the rewrite's lock, and the tables it marked. When less of
  /// the budget is left than a command timeout, the statement is cut off at what is left (a transaction-local
  /// <c>statement_timeout</c>), and a batch cut off that way is rolled back as the budget spent, not as a failure.
  /// </summary>
  private static async Task<(Step Step, IReadOnlyList<string> Marked)> _batchAsync(
      Batch batch, (string Name, string Sql) rewrite, TimeSpan left, CancellationToken cancellationToken) {
    var connection = batch.Connection;
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await using (var take = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction)) {
      take.Parameters.AddWithValue(batch.Request.LockId);
      if (!BooleanScalar.Read(await take.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "batched rewrite lock")) {
        return (Step.LockHeldElsewhere, []);
      }
    }

    var bounded = left < TimeSpan.FromSeconds(batch.Request.CommandTimeoutSeconds);
    try {
      if (bounded) {
        await using var bound = new NpgsqlCommand("SELECT set_config('statement_timeout', $1, true)", connection, transaction);
        bound.Parameters.AddWithValue(Math.Max(1L, (long)left.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await bound.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }
      await StoredFormApplier.StampAsync(connection, transaction, batch.Applier, cancellationToken).ConfigureAwait(false);
      await using (var run = new NpgsqlCommand(rewrite.Sql, connection, transaction) {
        CommandTimeout = batch.Request.CommandTimeoutSeconds,
      }) {
        await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      }

      bool settled;
      await using (var state = new NpgsqlCommand(
          $"SELECT coalesce((SELECT settled_at IS NOT NULL FROM {batch.Journal} WHERE name = $1), true)", connection, transaction)) {
        state.Parameters.AddWithValue(rewrite.Name);
        settled = BooleanScalar.Read(await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "batched rewrite state");
      }

      // The marks this batch made are read before the commit ends the transaction they live in.
      var marked = await IndexStatistics.ReadRewrittenTablesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      // #2165: each batch is progress, so a long startup slice keeps the migrator's role.
      StartupProgress.Note();
      return (settled ? Step.Settled : Step.Converted, marked);
    } catch (PostgresException ex) when (bounded && ex.SqlState == PostgresErrorCodes.QueryCanceled) {
      await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
      BatchedRewriteLog.BudgetSpent(batch.Log, rewrite.Name);
      return (Step.BudgetSpent, []);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
      BatchedRewriteLog.BatchFailed(batch.Log, ex, rewrite.Name);
      return (Step.Failed, []);
    }
  }

  /// <summary>The rewrites whose journal row is not settled, in the order given; all of them when there is no journal.</summary>
  private static async Task<List<(string Name, string Sql)>> _unsettledAsync(Batch batch, CancellationToken cancellationToken) {
    var settled = new HashSet<string>(StringComparer.Ordinal);
    await using (var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", batch.Connection)) {
      exists.Parameters.AddWithValue(batch.Journal);
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) {
        await using var command = new NpgsqlCommand(
          $"SELECT name FROM {batch.Journal} WHERE name = ANY($1) AND settled_at IS NOT NULL", batch.Connection);
        command.Parameters.AddWithValue(batch.Request.Rewrites.Select(r => r.Name).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
          settled.Add(reader.GetString(0));
        }
      }
    }
    return [.. batch.Request.Rewrites.Where(r => !settled.Contains(r.Name))];
  }
}

/// <summary>Source-generated logging for the batched rewrite.</summary>
internal static partial class BatchedRewriteLog {
  [LoggerMessage(Level = LogLevel.Information, Message = "Batched stored-form rewrite: {Message}")]
  public static partial void Notice(ILogger logger, string message);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Batched stored-form rewrite ran {Batches} batch(es); {Remaining} rewrite(s) unsettled, carried on by the "
              + "next startup or maintenance cycle")]
  public static partial void Unfinished(ILogger logger, int batches, int remaining);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "Batched stored-form rewrite of {Pending} table(s) waits for the fleet: {Reason}; queries read both forms "
              + "meanwhile")]
  public static partial void HeldForFleet(ILogger logger, int pending, string reason);

  [LoggerMessage(
      Level = LogLevel.Information,
      Message = "A batch of the stored-form rewrite {Rewrite} reached the end of the run's budget and was rolled back; the "
              + "next run tries it again")]
  public static partial void BudgetSpent(ILogger logger, string rewrite);

  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "A batch of the stored-form rewrite {Rewrite} failed and was rolled back; the next run tries it again")]
  public static partial void BatchFailed(ILogger logger, Exception exception, string rewrite);
}
