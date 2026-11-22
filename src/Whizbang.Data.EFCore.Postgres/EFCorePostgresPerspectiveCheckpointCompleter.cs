// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core / PostgreSQL implementation of <see cref="IPerspectiveCheckpointCompleter"/>.
/// Upserts <c>wh_perspective_cursors</c> rows so rebuild operations leave cursor state
/// consistent with the replayed event log, without going through the live
/// perspective completion path.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>complete_perspective_cursor_work</c> (which requires the cursor row to
/// already exist, normally created by <c>update_perspective_cursors</c> when a live perspective
/// run's completion is recorded),
/// rebuild may operate against streams whose events were appended outside the live flow.
/// This completer uses <c>INSERT ... ON CONFLICT DO UPDATE</c> so missing cursor rows are
/// created at their replayed end-state.
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
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildInPlaceAsync_WritesCursorCheckpointAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildStreamsAsync_WritesCursorsForTargetedStreamsOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildInPlaceAsync_WithExistingProcessingCursor_UpdatesToCompletedAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Completer_TheCursorOfAStreamArchivedWhole_IsParked_AndTheRestoreBringsItBackAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Completer_WritesEveryCursorOfAFlush_WithOneStatement_ParkedAndLiveAlikeAsync</tests>
public sealed partial class EFCorePostgresPerspectiveCheckpointCompleter(
    DbContext dbContext,
    ILogger<EFCorePostgresPerspectiveCheckpointCompleter>? logger = null)
    : IPerspectiveCheckpointCompleter {

  private const string DEFAULT_SCHEMA = "public";

  private readonly DbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

  /// <inheritdoc />
  [SuppressMessage("Major Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "The method owns its transaction only when one was not supplied, so every exit (success, a per-completion skip, a failure) has to commit, roll back or leave it alone accordingly. The branching is that ownership.")]
  public async Task CompleteAsync(
      IReadOnlyList<PerspectiveCursorCompletion> completions,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(completions);
    if (completions.Count == 0) {
      return;
    }

    var sql = _buildCursorWriteSql(_buildQualifiedName(_resolveSchema(), "wh_complete_rebuilt_cursors"));

    var transaction = _dbContext.Database.CurrentTransaction;
    var ownsTransaction = transaction == null;
    if (ownsTransaction) {
      transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    var persisted = 0;
    var skippedEmpty = 0;
    Exception? failure = null;
    try {
      var written = _withEvents(completions);
      skippedEmpty = completions.Count - written.Count;
      if (written.Count > 0) {
        await _writeCursorsAsync(written, sql, cancellationToken);
        persisted = written.Count;
        _logUpserts(written);
      }

      if (ownsTransaction && transaction != null) {
        await transaction.CommitAsync(cancellationToken);
      }

      if (logger?.IsEnabled(LogLevel.Information) == true && (persisted > 0 || skippedEmpty > 0)) {
        LogBatchPersisted(logger, persisted, skippedEmpty, completions.Count);
      }
    } catch (Exception ex) {
      // Captured rather than rethrown here. A rethrow from an async catch that also awaits makes
      // the compiler hoist this handler out of the IL catch region and rewrite `throw;` as a
      // capture-and-throw, and the brace's sequence point then lands on state-machine cleanup that
      // nothing reaches. Doing the capture ourselves and throwing after the finally keeps the same
      // order — roll back, log, dispose, propagate — with no line that cannot run.
      failure = ex;
      if (ownsTransaction && transaction != null) {
        await transaction.RollbackAsync(cancellationToken);
      }
      if (logger != null) {
        LogBatchFailed(logger, ex, persisted, completions.Count);
      }
    } finally {
      if (ownsTransaction && transaction != null) {
        await transaction.DisposeAsync();
      }
    }

    if (failure is not null) {
      ExceptionDispatchInfo.Capture(failure).Throw();
    }
  }

  /// <summary>
  /// The completions that name an event, in their order. One with <see cref="Guid.Empty"/> as its last event folded
  /// nothing and is skipped (matches the "no events processed, skip checkpoint" branch of the live work coordinator path;
  /// the wh_perspective_cursors.last_event_id FK to wh_event_store.event_id rejects Guid.Empty).
  /// </summary>
  private List<PerspectiveCursorCompletion> _withEvents(IReadOnlyList<PerspectiveCursorCompletion> completions) {
    var written = new List<PerspectiveCursorCompletion>(completions.Count);
    foreach (var completion in completions) {
      if (completion.LastEventId != Guid.Empty) {
        written.Add(completion);
      } else if (logger?.IsEnabled(LogLevel.Debug) == true) {
        LogSkippedEmptyLastEventId(logger, completion.StreamId, completion.PerspectiveName);
      }
    }
    return written;
  }

  /// <summary>
  /// Writes every cursor of the flush with one statement (migration 556, #2513), each as the one-cursor function wrote it.
  /// </summary>
  private Task<int> _writeCursorsAsync(List<PerspectiveCursorCompletion> written, string sql, CancellationToken cancellationToken) =>
    _dbContext.Database.ExecuteSqlRawAsync(
        sql,
        [
          written.ConvertAll(c => c.StreamId).ToArray(),
          written.ConvertAll(c => c.PerspectiveName).ToArray(),
          written.ConvertAll(c => c.LastEventId).ToArray(),
          written.ConvertAll(c => (short)c.Status).ToArray(),
        ],
        cancellationToken);

  /// <summary>Logs each cursor the flush wrote, at Debug.</summary>
  private void _logUpserts(List<PerspectiveCursorCompletion> written) {
    if (logger?.IsEnabled(LogLevel.Debug) != true) {
      return;
    }
    foreach (var completion in written) {
      LogCursorUpsert(logger, completion.StreamId, completion.PerspectiveName, completion.LastEventId, completion.Status);
    }
  }

  /// <summary>
  /// INSERT ... ON CONFLICT DO UPDATE statement for wh_perspective_cursors. Clears
  /// rewind_trigger_event_id, rewind_flagged_at, rewind_first_flagged_at, and error on update:
  /// a full rebuild resolves any prior rewind flagging since the entire event log has been
  /// replayed.
  /// </summary>
#pragma warning disable S2077 // schema escaped through PgIdentifier; values are positional parameters.
  // The shared rebuild cursor write (migration 273, #1931; a flush in one statement since 556, #2513): a stream archived
  // whole has its cursor parked with the stream, any other stream's is upserted into wh_perspective_cursors, exactly as the
  // Dapper driver's completer writes it.
  private static string _buildCursorWriteSql(string functionName) =>
    $"SELECT {functionName}({{0}}, {{1}}, {{2}}, {{3}})";
#pragma warning restore S2077

  [LoggerMessage(Level = LogLevel.Debug,
      Message = "Checkpoint completer: upserted cursor stream_id={StreamId} perspective={PerspectiveName} last_event_id={LastEventId} status={Status}")]
  private static partial void LogCursorUpsert(ILogger logger, Guid streamId, string perspectiveName,
      Guid lastEventId, PerspectiveProcessingStatus status);

  [LoggerMessage(Level = LogLevel.Debug,
      Message = "Checkpoint completer: skipped stream_id={StreamId} perspective={PerspectiveName} — last_event_id is Guid.Empty (no events processed)")]
  private static partial void LogSkippedEmptyLastEventId(ILogger logger, Guid streamId,
      string perspectiveName);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Checkpoint completer: persisted {Persisted} cursor(s), skipped {SkippedEmpty} empty, total batch {Total}")]
  private static partial void LogBatchPersisted(ILogger logger, int persisted, int skippedEmpty,
      int total);

  [LoggerMessage(Level = LogLevel.Error,
      Message = "Checkpoint completer: batch failed after {Persisted}/{Total} cursor(s); transaction rolled back")]
  private static partial void LogBatchFailed(ILogger logger, Exception ex, int persisted, int total);

  private string _resolveSchema() {
    // Use the same entity as the cursor rows so we pick up whatever schema the DbContext
    // was configured with. Falls back to "public" if metadata is missing.
    var entityType = _dbContext.Model.FindEntityType(typeof(PerspectiveCursorRecord));
    var schema = entityType?.GetSchema();
    return string.IsNullOrWhiteSpace(schema) ? DEFAULT_SCHEMA : schema;
  }

  private static string _buildQualifiedName(string schema, string identifier) {
    if (string.IsNullOrWhiteSpace(schema) || schema == DEFAULT_SCHEMA) {
      return identifier;
    }
    return Whizbang.Data.Postgres.PgIdentifier.Qualify(schema, identifier);
  }
}
