using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// EF Core / PostgreSQL implementation of <see cref="IPerspectiveCheckpointCompleter"/>.
/// Upserts <c>wh_perspective_cursors</c> rows so rebuild operations leave cursor state
/// consistent with the replayed event log, without going through the full
/// <c>process_work_batch</c> pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <c>complete_perspective_cursor_work</c> (which requires the cursor row to
/// already exist — normally created by <c>process_work_batch</c>'s auto-cursor step),
/// rebuild may operate against streams whose events were appended outside the live flow.
/// This completer uses <c>INSERT ... ON CONFLICT DO UPDATE</c> so missing cursor rows are
/// created at their replayed end-state.
/// </para>
/// <para>
/// Clears <c>rewind_trigger_event_id</c>, <c>rewind_flagged_at</c>, <c>rewind_first_flagged_at</c>,
/// and <c>error</c> on update — a full rebuild resolves any prior rewind flagging since the
/// entire event log has been replayed.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
public sealed partial class EFCorePostgresPerspectiveCheckpointCompleter(
    DbContext dbContext,
    ILogger<EFCorePostgresPerspectiveCheckpointCompleter>? logger = null)
    : IPerspectiveCheckpointCompleter {

  private const string DEFAULT_SCHEMA = "public";

  private readonly DbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

  /// <inheritdoc />
  public async Task CompleteAsync(
      IReadOnlyList<PerspectiveCursorCompletion> completions,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(completions);
    if (completions.Count == 0) {
      return;
    }

    var schema = _resolveSchema();
    var tableName = _buildQualifiedName(schema, "wh_perspective_cursors");

#pragma warning disable S2077 // schema-qualified table name built from DbContext metadata; values are positional parameters.
    var sql = $@"
      INSERT INTO {tableName}
        (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES ({{0}}, {{1}}, {{2}}, {{3}}, NOW())
      ON CONFLICT (stream_id, perspective_name) DO UPDATE SET
        last_event_id = EXCLUDED.last_event_id,
        status = EXCLUDED.status,
        processed_at = EXCLUDED.processed_at,
        error = NULL,
        rewind_trigger_event_id = NULL,
        rewind_flagged_at = NULL,
        rewind_first_flagged_at = NULL;";
#pragma warning restore S2077

    var transaction = _dbContext.Database.CurrentTransaction;
    var ownsTransaction = transaction == null;
    if (ownsTransaction) {
      transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    var persisted = 0;
    var skippedEmpty = 0;
    try {
      foreach (var completion in completions) {
        // Skip completions with no event processed. A cursor row whose last_event_id references
        // nothing is not useful, and the wh_perspective_cursors.last_event_id FK to
        // wh_event_store.event_id rejects Guid.Empty. This matches the "No events processed —
        // skip checkpoint" branch in the live work coordinator path.
        if (completion.LastEventId == Guid.Empty) {
          skippedEmpty++;
          if (logger?.IsEnabled(LogLevel.Debug) == true) {
            LogSkippedEmptyLastEventId(logger, completion.StreamId, completion.PerspectiveName);
          }
          continue;
        }

        await _dbContext.Database.ExecuteSqlRawAsync(
            sql,
            [
              completion.StreamId,
              completion.PerspectiveName,
              completion.LastEventId,
              (short)completion.Status
            ],
            cancellationToken);
        persisted++;

        if (logger?.IsEnabled(LogLevel.Debug) == true) {
          LogCursorUpsert(logger, completion.StreamId, completion.PerspectiveName,
              completion.LastEventId, completion.Status);
        }
      }

      if (ownsTransaction && transaction != null) {
        await transaction.CommitAsync(cancellationToken);
      }

      if (logger?.IsEnabled(LogLevel.Information) == true && (persisted > 0 || skippedEmpty > 0)) {
        LogBatchPersisted(logger, persisted, skippedEmpty, completions.Count);
      }
    } catch (Exception ex) {
      if (ownsTransaction && transaction != null) {
        await transaction.RollbackAsync(cancellationToken);
      }
      if (logger != null) {
        LogBatchFailed(logger, ex, persisted, completions.Count);
      }
      throw;
    } finally {
      if (ownsTransaction && transaction != null) {
        await transaction.DisposeAsync();
      }
    }
  }

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
    return $"\"{schema}\".{identifier}";
  }
}
