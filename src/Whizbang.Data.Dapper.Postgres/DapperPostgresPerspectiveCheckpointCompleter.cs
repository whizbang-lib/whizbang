// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Dapper / PostgreSQL implementation of <see cref="IPerspectiveCheckpointCompleter"/>.
/// Upserts <c>wh_perspective_cursors</c> rows so rebuild operations leave cursor state
/// consistent with the replayed event log, without going through the live
/// perspective completion path.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>complete_perspective_cursor_work</c> (which requires the cursor row to exist —
/// normally created by <c>update_perspective_cursors</c> when a live perspective run's completion
/// is recorded), rebuild may operate
/// against streams whose events were appended outside the live flow. This completer uses
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> so missing cursor rows are created at their
/// replayed end-state.
/// </para>
/// <para>
/// Clears <c>rewind_trigger_event_id</c>, <c>rewind_flagged_at</c>, <c>rewind_first_flagged_at</c>,
/// and <c>error</c> on update — a full rebuild resolves any prior rewind flagging since the
/// entire event log has been replayed.
/// </para>
/// <para>The cursor of a stream archived whole is parked with the stream (migration 273, #1931): a rebuild that
/// includes such a stream must not name an archived event in the live cursor table, and the restore brings the cursor back.</para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRebuildTests.cs:Completer_TheCursorOfAStreamArchivedWhole_IsParked_AndTheRestoreBringsItBackAsync</tests>
public sealed partial class DapperPostgresPerspectiveCheckpointCompleter(
    string connectionString,
    ILogger<DapperPostgresPerspectiveCheckpointCompleter>? logger = null) : IPerspectiveCheckpointCompleter {

  private readonly string _connectionString = connectionString
      ?? throw new ArgumentNullException(nameof(connectionString));

  /// <inheritdoc />
  public async Task CompleteAsync(
      IReadOnlyList<PerspectiveCursorCompletion> completions,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(completions);
    if (completions.Count == 0) {
      return;
    }

    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

    try {
      // The shared rebuild cursor write (migration 273, #1931): a stream archived whole has its cursor parked with the
      // stream, any other stream's is upserted into wh_perspective_cursors, exactly as the EF Core driver's completer writes it.
      const string sql = "SELECT wh_complete_rebuilt_cursor(@p_stream_id, @p_perspective_name, @p_last_event_id, @p_status)";

      foreach (var completion in completions) {
        // Skip completions with no event processed — same rationale as the EFCore completer:
        // the wh_perspective_cursors.last_event_id FK to wh_event_store.event_id rejects
        // Guid.Empty, and a cursor pointing at nothing carries no useful state.
        if (completion.LastEventId == Guid.Empty) {
          continue;
        }

        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue("p_stream_id", completion.StreamId);
        cmd.Parameters.AddWithValue("p_perspective_name", completion.PerspectiveName);
        cmd.Parameters.AddWithValue("p_last_event_id", completion.LastEventId);
        cmd.Parameters.AddWithValue("p_status", (short)completion.Status);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
      }

      await transaction.CommitAsync(cancellationToken);
    } catch {
      await transaction.RollbackAsync(cancellationToken);
      throw;
    }

    if (logger?.IsEnabled(LogLevel.Debug) == true) {
      LogCompletionsPersisted(logger, completions.Count);
    }
  }

  [LoggerMessage(Level = LogLevel.Debug,
      Message = "Persisted {Count} perspective cursor checkpoint(s)")]
  private static partial void LogCompletionsPersisted(ILogger logger, int count);
}
