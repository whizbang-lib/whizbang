using Microsoft.EntityFrameworkCore;
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
public sealed class EFCorePostgresPerspectiveCheckpointCompleter(DbContext dbContext)
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

    try {
      foreach (var completion in completions) {
        // Skip completions with no event processed. A cursor row whose last_event_id references
        // nothing is not useful, and the wh_perspective_cursors.last_event_id FK to
        // wh_event_store.event_id rejects Guid.Empty. This matches the "No events processed —
        // skip checkpoint" branch in the live work coordinator path.
        if (completion.LastEventId == Guid.Empty) {
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
      }

      if (ownsTransaction && transaction != null) {
        await transaction.CommitAsync(cancellationToken);
      }
    } catch {
      if (ownsTransaction && transaction != null) {
        await transaction.RollbackAsync(cancellationToken);
      }
      throw;
    } finally {
      if (ownsTransaction && transaction != null) {
        await transaction.DisposeAsync();
      }
    }
  }

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
