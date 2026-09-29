using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The row-version reads behind a per-stream apply's lost-update guard (issue #928). A perspective row's
/// version is its PostgreSQL <c>xmin</c>: the id of the transaction that wrote the row's current version.
/// Every <c>UPDATE</c> produces a new row version with a new <c>xmin</c>, whichever path issued it (a
/// per-stream write, a collective apply on either driver, a consumer's own SQL) and whether or not it bumped
/// the <c>version</c> column, which apply hooks may leave alone or set explicitly. So no writer has to
/// cooperate, the column needs no migration, and existing rows are covered as they stand.
/// </summary>
/// <remarks>
/// <para>
/// A locking read (<c>FOR SHARE</c>, <c>FOR UPDATE</c>) does not move <c>xmin</c>, a rolled-back update does
/// not either (the old version stays current), and <c>VACUUM</c>, freezing and table rewrites preserve it.
/// Two different transactions never share an id within the 2^32-transaction wraparound horizon, far beyond
/// the milliseconds between an apply's read and its write.
/// </para>
/// <para>
/// Every read here runs on the context's own connection and joins its ambient transaction, like the atomic
/// upsert, so a caller that wrapped its writes together reads its own writes.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRowVersionIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveRowVersionSqlTests.cs</tests>
internal static class PerspectiveRowVersionSql {
  /// <summary>
  /// True when <paramref name="context"/> talks to PostgreSQL, the only provider whose rows carry a version
  /// this guard can read. Any other provider (the InMemory test provider) is unchecked.
  /// </summary>
  internal static bool Supports(DbContext context) =>
    context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

  /// <summary>
  /// The quoted, schema-qualified table <c>PerspectiveRow&lt;TModel&gt;</c> is mapped to in the context's model.
  /// The model is the authority on where the rows live, so the version is read from the same table the
  /// model itself is read from.
  /// </summary>
  /// <exception cref="InvalidOperationException">The context does not map the perspective to a table.</exception>
  internal static string QualifiedTable<TModel>(DbContext context) where TModel : class {
    var entityType = context.Model.FindEntityType(typeof(PerspectiveRow<TModel>));
    if (entityType?.GetTableName() is not { } table) {
      throw new InvalidOperationException(
        $"PerspectiveRow<{typeof(TModel).Name}> is not mapped to a table in {context.GetType().Name}, so its row version cannot be read.");
    }
    return PgIdentifier.QualifyPrefix(entityType.GetSchema()) + PgIdentifier.Quote(table);
  }

  /// <summary>
  /// Read the row's version together with the metadata the runner's idempotency filter needs, in one
  /// statement: <see cref="PerspectiveRowVersion.Absent"/> when there is no row.
  /// </summary>
  internal static async Task<PerspectiveApplyRead> ReadForApplyAsync(
      DbContext context, string qualifiedTable, Guid id, CancellationToken cancellationToken) {
    // The statement and the mapping are the shared ones, so both drivers read a version the same way.
    return await _withCommandAsync(context, PerspectiveRowVersionCommands.ReadForApplySql(qualifiedTable), id,
      async command => {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await PerspectiveRowVersionCommands.ReadApplyAsync(reader, cancellationToken).ConfigureAwait(false);
      }, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Read the row's current version. With <paramref name="lockRow"/>, the row is locked for the rest of the
  /// caller's transaction (<c>FOR UPDATE</c>), so no writer can move it between this check and the write
  /// that follows: a collective arriving meanwhile waits, then applies on top of the row that write leaves.
  /// </summary>
  internal static Task<PerspectiveRowVersion> ReadVersionAsync(
      DbContext context, string qualifiedTable, Guid id, bool lockRow, CancellationToken cancellationToken) {
    return _withCommandAsync(context, PerspectiveRowVersionCommands.ReadVersionSql(qualifiedTable, lockRow), id,
      async command =>
      await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is uint xmin
        ? PerspectiveRowVersion.Of(xmin)
        : PerspectiveRowVersion.Absent,
      cancellationToken);
  }

  /// <summary>
  /// Read one stored document column of the row as text, or null when there is no row or the column is null.
  /// Used only to explain a read that already failed (<see cref="Perspectives.MappedDocumentReadFailure"/>).
  /// </summary>
  internal static Task<string?> ReadDocumentTextAsync(
      DbContext context, string qualifiedTable, string column, Guid id, CancellationToken cancellationToken) =>
    _withCommandAsync(context, $"SELECT {PgIdentifier.Quote(column)}::text FROM {qualifiedTable} WHERE id = @id", id,
      async command => await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string,
      cancellationToken);

  /// <summary>
  /// The parameter a conditional write binds its expected version to. <c>xmin</c> is an <c>xid</c>; the
  /// version stored its unsigned value.
  /// </summary>
  internal static NpgsqlParameter ExpectedVersionParameter(string name, PerspectiveRowVersion expected) =>
    PerspectiveRowVersionCommands.ExpectedVersionParameter(name, expected);

  private static async Task<T> _withCommandAsync<T>(
      DbContext context, string sql, Guid id, Func<DbCommand, Task<T>> body, CancellationToken cancellationToken) {
    var connection = context.Database.GetDbConnection();
    var openedHere = connection.State != System.Data.ConnectionState.Open;
    if (openedHere) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
    // The result is returned after the try: a return inside a try whose finally awaits leaves a
    // compiler-emitted sequence point no test can reach.
    T result;
    try {
      await using var command = connection.CreateCommand();
      command.CommandText = sql;
      command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
      command.Parameters.Add(new NpgsqlParameter(nameof(id), id));
      result = await body(command).ConfigureAwait(false);
    } finally {
      if (openedHere) {
        await connection.CloseAsync().ConfigureAwait(false);
      }
    }
    return result;
  }
}
