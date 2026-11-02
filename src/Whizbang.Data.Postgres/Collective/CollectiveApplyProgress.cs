// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// The record of how far one handler's apply of one collective has reached, and the batch statement that applies the next
/// batch and moves the record in the same statement (#1969). Both Postgres drivers render and run the same statement.
/// </summary>
/// <remarks>
/// <para>
/// A collective is applied a batch at a time, each batch in a transaction of its own. Before this record the cursor of an
/// apply lived only in memory, so every retry (a busy apply lock, a failure, a crash, a re-delivery in the window before
/// the completion was written, a dead-letter recovery, a fan-out whose later handler failed) applied the collective again
/// from its first row, and an increment-style setter reached the rows of the committed batches twice.
/// </para>
/// <para>
/// The record lives in <c>wh_collective_apply_progress</c> (migration 279), one row per collective event and
/// <see cref="Whizbang.Core.Perspectives.CollectiveApplyEntry.EntryKey"/>. Each batch statement reads it, selects the next
/// batch after <c>through_id</c>, moves the record and updates the batch's rows, in one statement, inside the batch's
/// transaction and under its apply lock: no extra round trip, no write per row and no new lock. A finished record skips
/// the apply. The move is compare-and-set on the <c>through_id</c> the batch started after, so two appliers that both
/// believe they own the collective cannot both apply one range: the loser applies nothing and reads the record again.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#resuming-an-apply</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplyProgressTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyProgressEFCoreTests.cs</tests>
public static class CollectiveApplyProgress {
  /// <summary>The table that holds the records.</summary>
  public const string TABLE = "wh_collective_apply_progress";

  /// <summary>
  /// The SQL a store column takes to reuse the apply's first timestamp: a resumed apply stamps the rows it reaches with
  /// the time the apply started, not the time it resumed, so every row of one apply carries one timestamp.
  /// </summary>
  public const string APPLIED_AT_SQL = "(SELECT applied_at FROM wb_mark)";

  private const string EVENT_ID = "wb_event_id";
  private const string ENTRY_KEY = "wb_entry_key";
  private const string FROM = "wb_from";
  private const string APPLIED_AT = "wb_applied_at";

  /// <summary>
  /// Renders the batch statement. It returns one row: <c>"Applied"</c> (rows updated), <c>"Recorded"</c> (whether the
  /// record moved), <c>"ThroughId"</c> (the record's <c>through_id</c> after the batch) and <c>"Completed"</c> (whether
  /// the apply is finished, by this batch or before it).
  /// </summary>
  /// <param name="batch">The parts of the batch.</param>
  /// <returns>The statement, with <c>@wb_event_id</c>, <c>@wb_entry_key</c>, <c>@wb_from</c> and <c>@wb_applied_at</c>
  /// to bind through <see cref="Bind"/>.</returns>
  public static string Render(CollectiveApplyBatch batch) {
    ArgumentNullException.ThrowIfNull(batch);
    var limit = batch.BatchSize.ToString(CultureInfo.InvariantCulture);
    // FOR UPDATE when applies are not serialized by the apply lock: a second applier waits on the record instead of
    // computing a batch it would then lose at the compare-and-set.
    var lockRecord = batch.SerializedByTheApplyLock ? string.Empty : " FOR UPDATE";
    return "WITH wb_prior AS (SELECT through_id, completed, applied_at FROM " + batch.ProgressTable
      + " WHERE event_id = @" + EVENT_ID + " AND entry_key = @" + ENTRY_KEY + lockRecord + "), "
      + "wb_start AS (SELECT COALESCE(@" + FROM + "::uuid, (SELECT through_id FROM wb_prior)) AS through_id), "
      + "wb_batch AS (SELECT id FROM " + batch.Table + " WHERE (" + batch.Where + ")"
      + " AND id > COALESCE((SELECT through_id FROM wb_start), '00000000-0000-0000-0000-000000000000'::uuid)"
      + " AND NOT EXISTS (SELECT 1 FROM wb_prior WHERE completed) ORDER BY id LIMIT " + limit + "), "
      + "wb_mark AS (INSERT INTO " + batch.ProgressTable + " AS wb_p (event_id, entry_key, through_id, completed, abandoned, applied_at)"
      + " SELECT @" + EVENT_ID + ", @" + ENTRY_KEY + ","
      + " COALESCE((SELECT id FROM wb_batch ORDER BY id DESC LIMIT 1), (SELECT through_id FROM wb_start)),"
      + " (SELECT count(*) FROM wb_batch) < " + limit + ", false,"
      + " COALESCE((SELECT applied_at FROM wb_prior), @" + APPLIED_AT + ")"
      + " WHERE NOT EXISTS (SELECT 1 FROM wb_prior WHERE completed)"
      + " ON CONFLICT (event_id, entry_key) DO UPDATE SET through_id = EXCLUDED.through_id, completed = EXCLUDED.completed, abandoned = false"
      + " WHERE NOT wb_p.completed AND wb_p.through_id IS NOT DISTINCT FROM (SELECT through_id FROM wb_start)"
      + " RETURNING through_id, completed, applied_at), "
      + "wb_applied AS (UPDATE " + batch.UpdateTarget + " SET " + batch.SetList
      + " WHERE id IN (SELECT id FROM wb_batch) AND EXISTS (SELECT 1 FROM wb_mark) RETURNING 1) "
      + "SELECT (SELECT count(*) FROM wb_applied)::int AS \"Applied\", EXISTS (SELECT 1 FROM wb_mark) AS \"Recorded\","
      + " (SELECT through_id FROM wb_mark) AS \"ThroughId\","
      + " COALESCE((SELECT completed FROM wb_mark), EXISTS (SELECT 1 FROM wb_prior WHERE completed)) AS \"Completed\"";
  }

  /// <summary>Binds the record's identity, the start the batch expects and the apply's timestamp.</summary>
  /// <param name="command">The batch command.</param>
  /// <param name="cursor">The apply and where its next batch starts.</param>
  public static void Bind(DbCommand command, CollectiveApplyCursor cursor) {
    ArgumentNullException.ThrowIfNull(command);
    ArgumentNullException.ThrowIfNull(cursor);
    _add(command, EVENT_ID, DbType.Guid, cursor.EventId);
    _add(command, ENTRY_KEY, DbType.String, cursor.EntryKey);
    _add(command, FROM, DbType.Guid, cursor.From is Guid from ? from : DBNull.Value);
    _add(command, APPLIED_AT, DbType.DateTimeOffset, cursor.AppliedAt);
  }

  /// <summary>The record's parameters as name and value pairs, for a driver that binds through its own command type.</summary>
  /// <param name="cursor">The apply and where its next batch starts.</param>
  /// <returns>The four parameters, a missing start as <see cref="DBNull"/>.</returns>
  public static IReadOnlyList<KeyValuePair<string, object>> Parameters(CollectiveApplyCursor cursor) {
    ArgumentNullException.ThrowIfNull(cursor);
    return [
      new(EVENT_ID, cursor.EventId),
      new(ENTRY_KEY, cursor.EntryKey),
      new(FROM, cursor.From is Guid from ? from : DBNull.Value),
      new(APPLIED_AT, cursor.AppliedAt),
    ];
  }

  /// <summary>Reads the batch statement's one row.</summary>
  /// <param name="reader">A reader positioned before the row.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>What the batch did.</returns>
  public static async Task<CollectiveApplyBatchResult> ReadAsync(DbDataReader reader, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(reader);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    Guid? through = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2);
    return new CollectiveApplyBatchResult(reader.GetInt32(0), reader.GetBoolean(1), through, reader.GetBoolean(3));
  }

  /// <summary>
  /// Runs an apply batch by batch until its record says it is finished. The first batch starts where the record says;
  /// each later one where the previous batch left it. A batch that lost the compare-and-set applied nothing, and the next
  /// one reads the record again.
  /// </summary>
  /// <param name="batch">Runs one batch from the given start (null: read it from the record), in its own transaction.</param>
  /// <param name="onBatchApplied">Called after every batch that applied, so the caller can renew its lease; may be null.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The rows updated and the batches that applied.</returns>
  public static async Task<(int Rows, int Batches)> RunAsync(
      Func<Guid?, Task<CollectiveApplyBatchResult>> batch,
      Func<CancellationToken, ValueTask>? onBatchApplied,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(batch);
    var rows = 0;
    var batches = 0;
    Guid? from = null;
    while (true) {
      var result = await batch(from).ConfigureAwait(false);
      if (!result.Recorded) {
        if (result.Completed) {
          return (rows, batches);
        }
        // Another applier moved the record first: nothing applied, so read where it got to and go on from there.
        from = null;
        continue;
      }
      rows += result.Applied;
      batches++;
      if (onBatchApplied is not null) {
        await onBatchApplied(cancellationToken).ConfigureAwait(false);
      }
      if (result.Completed) {
        return (rows, batches);
      }
      from = result.ThroughId;
    }
  }

  private static void _add(DbCommand command, string name, DbType type, object value) {
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.DbType = type;
    parameter.Value = value;
    command.Parameters.Add(parameter);
  }
}

/// <summary>The parts of one collective apply batch statement (#1969).</summary>
/// <docs>fundamentals/messaging/collective-events#resuming-an-apply</docs>
public sealed record CollectiveApplyBatch {
  /// <summary>The perspective table as the cohort predicate names it (the outer row of a correlated <c>EXISTS</c>).</summary>
  public required string Table { get; init; }

  /// <summary>The perspective table the update writes, qualified when it lives in a named schema.</summary>
  public required string UpdateTarget { get; init; }

  /// <summary>The progress table, qualified like the perspective table.</summary>
  public required string ProgressTable { get; init; }

  /// <summary>The cohort predicate, compiled to SQL.</summary>
  public required string Where { get; init; }

  /// <summary>The update's <c>SET</c> list: the setters and the store columns.</summary>
  public required string SetList { get; init; }

  /// <summary>The most rows one batch updates.</summary>
  public required int BatchSize { get; init; }

  /// <summary>Whether the apply lock serializes the applies; when it does not, the batch locks the record it reads.</summary>
  public required bool SerializedByTheApplyLock { get; init; }
}

/// <summary>One handler's apply of one collective, and where its next batch starts (#1969).</summary>
/// <param name="EventId">The collective event's id.</param>
/// <param name="EntryKey">The handler's <see cref="Whizbang.Core.Perspectives.CollectiveApplyEntry.EntryKey"/>.</param>
/// <param name="From">The <c>through_id</c> the next batch expects; null reads it from the record.</param>
/// <param name="AppliedAt">The apply's timestamp, recorded when the apply starts and reused when it resumes.</param>
/// <docs>fundamentals/messaging/collective-events#resuming-an-apply</docs>
public sealed record CollectiveApplyCursor(Guid EventId, string EntryKey, Guid? From, DateTimeOffset AppliedAt);

/// <summary>What one collective apply batch did (#1969).</summary>
/// <param name="Applied">The rows it updated.</param>
/// <param name="Recorded">Whether it moved the record; false when the apply was finished, or another applier moved it first.</param>
/// <param name="ThroughId">The record's <c>through_id</c> after the batch.</param>
/// <param name="Completed">Whether the apply is finished.</param>
/// <docs>fundamentals/messaging/collective-events#resuming-an-apply</docs>
public sealed record CollectiveApplyBatchResult(int Applied, bool Recorded, Guid? ThroughId, bool Completed);
