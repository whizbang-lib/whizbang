using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres.Observability;

/// <summary>
/// Writes the document fields a statement filters on into a leading comment, so the database's
/// statement statistics keep them.
/// </summary>
/// <remarks>
/// <para>
/// Without this the advisory can say a perspective is being read by scanning it and cannot say what
/// was doing the reading, because PostgreSQL replaces a statement's constants with placeholders
/// before recording it and a JSON key is a constant. That is the difference between a finding
/// someone has to investigate and one they can act on.
/// </para>
/// <para>
/// Off unless asked for. It reads the text of every command the context sends, which is a cost paid
/// on the query path for a diagnostic, and a deployment that does not collect statement statistics
/// gets nothing back for it.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
public sealed class DocumentFilterTagInterceptor : DbCommandInterceptor {
  /// <inheritdoc/>
  public override InterceptionResult<DbDataReader> ReaderExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) {
    _tag(command);
    return result;
  }

  /// <inheritdoc/>
  public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
      CancellationToken cancellationToken = default) {
    _tag(command);
    return ValueTask.FromResult(result);
  }

  /// <inheritdoc/>
  public override InterceptionResult<object> ScalarExecuting(
      DbCommand command, CommandEventData eventData, InterceptionResult<object> result) {
    _tag(command);
    return result;
  }

  /// <inheritdoc/>
  public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
      DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
      CancellationToken cancellationToken = default) {
    _tag(command);
    return ValueTask.FromResult(result);
  }

  private static void _tag(DbCommand command) {
    ArgumentNullException.ThrowIfNull(command);

    var tagged = DocumentFilterNaming.Tagged(command.CommandText);
    if (!ReferenceEquals(tagged, command.CommandText)) {
      command.CommandText = tagged;
    }
  }
}
