// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>One event type an operation's history walk covers.</summary>
/// <param name="EventType">The stored event type name the walk reads (normalized by the SQL).</param>
/// <param name="DefinitionName">The name the type-definition registry knows the type by; a newer definition restarts the
/// walk.</param>
/// <param name="Variant">The operation's setting the walk applies (the superseding group); a change restarts the walk.</param>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
internal record TypeHistoryTarget(string EventType, string DefinitionName, string? Variant);

/// <summary>The window one batch of a walk applies: the type's stamped events after <paramref name="After"/> through
/// <paramref name="Through"/>, in commit order.</summary>
/// <param name="After">The commit sequence the walk had passed, or null at its start.</param>
/// <param name="Through">The window's last commit sequence.</param>
/// <param name="Scanned">The type's events in the window.</param>
/// <param name="ScanSize">The most events a window holds; a window that holds fewer read to the type's end.</param>
internal readonly record struct TypeHistoryWindow(long? After, long Through, long Scanned, int ScanSize);

/// <summary>What one batch's apply did, as the operation's SQL reports it.</summary>
/// <param name="Status">ok, finished, waiting (...), held (...) or skipped (...).</param>
/// <param name="Changed">Events the batch changed.</param>
/// <param name="Queued">Events the batch queued for a reaper.</param>
/// <param name="Blocked">Events the batch left as they were because a rule held them back.</param>
/// <param name="Total">The type's events when the walk started.</param>
/// <param name="Matched">The type's events the walk has read.</param>
internal readonly record struct TypeHistoryBatch(string Status, long Changed, long Queued, long Blocked, long Total, long Matched);

/// <summary>What one cycle did for one type.</summary>
internal readonly record struct TypeHistoryTypeCycle(
  TypeHistoryTarget Target, int Batches, long Scanned, long Changed, long Queued, long Blocked, long Left, long Total,
  bool Finished, string Status);

/// <summary>What one cycle did across every type.</summary>
internal sealed record TypeHistoryWalkResult(
  int Batches, long Scanned, long Changed, long Queued, long Blocked, int TypesFinished, string Status,
  IReadOnlyList<TypeHistoryTypeCycle> Types);

/// <summary>Applies one window of one type's walk, inside the transaction that began it.</summary>
internal delegate Task<TypeHistoryBatch> TypeHistoryApply<in TTarget>(
  NpgsqlTransaction transaction, string prefix, TTarget target, TypeHistoryWindow window, CancellationToken cancellationToken)
  where TTarget : TypeHistoryTarget;

/// <summary>
/// The one mechanism every change to a type's stored history goes through (#2205): the superseding reconcile and the
/// ephemeral reclassify (#2206) each supply only what a batch does to its rows. The walker reads the type's own stamped
/// events in commit order through the type's index (<c>wh_type_history_begin</c>), a window of <c>scanSize</c> events per
/// batch and each batch its own transaction, so a type with N events costs ceil(N / scanSize) batches whatever the size of
/// the store. One instance walks a type at a time (the walk's row is taken <c>SKIP LOCKED</c>), a crash resumes from the
/// last committed cursor, and a cycle runs at most <c>maxBatches</c> batches across all its types; a type that is
/// complete, held by another instance or waiting for the stamper spends none of them.
/// </summary>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/TypeHistoryWalkSqlTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by SqlText.Identifier; every value is a bound parameter.")]
internal sealed class TypeHistoryWalker(string operation, int scanSize, int maxBatches) {
  /// <summary>The status of a batch after which the walk goes on.</summary>
  internal const string OK = "ok";

  /// <summary>The status of the batch that completed a walk.</summary>
  internal const string FINISHED = "finished";

  /// <summary>Walks each target in turn until the cycle's batch budget is spent.</summary>
  public async Task<TypeHistoryWalkResult> WalkAsync<TTarget>(
      PostgresArchiveConnection database, IReadOnlyList<TTarget> targets, TypeHistoryApply<TTarget> apply,
      CancellationToken cancellationToken) where TTarget : TypeHistoryTarget {
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    var cycles = new List<TypeHistoryTypeCycle>();
    var batches = 0;
    foreach (var target in targets) {
      if (batches >= maxBatches) {
        break;
      }
      await _ensureAsync(database, prefix, target, cancellationToken).ConfigureAwait(false);
      var cycle = await _walkTypeAsync(database, prefix, target, apply, maxBatches - batches, cancellationToken).ConfigureAwait(false);
      batches += cycle.Batches;
      cycles.Add(cycle);
    }
    return new TypeHistoryWalkResult(
      batches, cycles.Sum(c => c.Scanned), cycles.Sum(c => c.Changed), cycles.Sum(c => c.Queued), cycles.Sum(c => c.Blocked),
      cycles.Count(c => c.Finished), cycles.Count == 0 ? OK : cycles[^1].Status, cycles);
  }

  private async Task _ensureAsync(PostgresArchiveConnection database, string prefix, TypeHistoryTarget target, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var ensure = new NpgsqlCommand($"SELECT {prefix}wh_type_history_ensure($1, $2, $3, $4)", connection);
    ensure.Parameters.AddWithValue(operation);
    ensure.Parameters.AddWithValue(target.EventType);
    ensure.Parameters.AddWithValue(target.DefinitionName);
    ensure.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text, Value = (object?)target.Variant ?? DBNull.Value });
    _ = await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  // Walks one type until a batch is not ok or the budget left to it is spent.
  private async Task<TypeHistoryTypeCycle> _walkTypeAsync<TTarget>(
      PostgresArchiveConnection database, string prefix, TTarget target, TypeHistoryApply<TTarget> apply, int budget,
      CancellationToken cancellationToken) where TTarget : TypeHistoryTarget {
    var cycle = new TypeHistoryTypeCycle(target, 0, 0, 0, 0, 0, 0, 0, false, OK);
    while (cycle.Batches < budget) {
      var (status, scanned, batch) = await _batchAsync(database, prefix, target, apply, cancellationToken).ConfigureAwait(false);
      cycle = cycle with {
        Batches = cycle.Batches + (scanned is null ? 0 : 1),
        Scanned = cycle.Scanned + (scanned ?? 0),
        Changed = cycle.Changed + batch.Changed,
        Queued = cycle.Queued + batch.Queued,
        Blocked = cycle.Blocked + batch.Blocked,
        Left = Math.Max(batch.Total - batch.Matched, 0),
        Total = batch.Total,
        Finished = status == FINISHED,
        Status = status,
      };
      if (status != OK) {
        break;
      }
    }
    return cycle;
  }

  // One batch, one transaction: take the type's walk and its window, apply it. A crash anywhere rolls all of it back.
  // Scanned is null when begin read no window (complete, finished, waiting or skipped): no batch was spent.
  private async Task<(string Status, long? Scanned, TypeHistoryBatch Batch)> _batchAsync<TTarget>(
      PostgresArchiveConnection database, string prefix, TTarget target, TypeHistoryApply<TTarget> apply,
      CancellationToken cancellationToken) where TTarget : TypeHistoryTarget {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    var (status, window, total, matched) = await _beginAsync(transaction, prefix, target, cancellationToken).ConfigureAwait(false);
    if (status != OK) {
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      return (status, null, new TypeHistoryBatch(status, 0, 0, 0, total, matched));
    }
    var batch = await apply(transaction, prefix, target, window, cancellationToken).ConfigureAwait(false);
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return (batch.Status, window.Scanned, batch);
  }

  private async Task<(string Status, TypeHistoryWindow Window, long Total, long Matched)> _beginAsync(
      NpgsqlTransaction transaction, string prefix, TypeHistoryTarget target, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      "SELECT status, after_sequence, through_sequence, rows_scanned, events_total, events_matched " +
      $"FROM {prefix}wh_type_history_begin($1, $2, $3, $4)", transaction.Connection, transaction);
    command.Parameters.AddWithValue(operation);
    command.Parameters.AddWithValue(target.EventType);
    command.Parameters.AddWithValue(target.DefinitionName);
    command.Parameters.AddWithValue(scanSize);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    var window = new TypeHistoryWindow(
      reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? 0 : reader.GetInt64(2), reader.GetInt64(3), scanSize);
    return (reader.GetString(0), window, reader.GetInt64(4), reader.GetInt64(5));
  }
}
