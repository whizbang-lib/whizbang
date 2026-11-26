// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Names the database schema a framework write targets, so a strongly-typed id in the documents it serializes takes the
/// form that schema's gate allows rather than the process's (#2518, <see cref="WhizbangIdDocumentForm.WritingTo"/>).
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveUpsertStrongIdFormTests.cs</tests>
internal static class StrongIdDocumentTarget {
  /// <summary>
  /// Targets the schema a table lives in, in the database the context's connection reaches, as the server names it
  /// (<see cref="PostgresServerDatabase"/>); until that name is known, a provisional one no gate opens, so the write takes
  /// the object form every release reads.
  /// </summary>
  /// <param name="context">The context that writes.</param>
  /// <param name="schema">The table's schema; null is <c>public</c>.</param>
  /// <returns>The scope; disposing it restores the target before.</returns>
  internal static IDisposable Of(DbContext context, string? schema) =>
    WhizbangIdDocumentForm.WritingTo(
      context.Database.IsRelational() ? PostgresServerDatabase.KeyOf(context.Database.GetDbConnection()) : null, schema);

  /// <summary>
  /// Targets every schema the context's next save writes into: the tables of each tracked entity added or modified. The
  /// save writes a strongly-typed id as its UUID string only while every one of their gates is open, so one schema's form
  /// never reaches another's rows (#2518). Call <see cref="ResolveAsync"/> first, so the database is known by the name the
  /// server gives it.
  /// </summary>
  /// <param name="context">The context about to save.</param>
  /// <returns>The scope; disposing it restores the target before.</returns>
  internal static IDisposable OfTracked(DbContext context) {
    var schemas = context.ChangeTracker.Entries()
      .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
      .Select(entry => entry.Metadata.GetSchema())
      .Distinct(StringComparer.Ordinal)
      .ToList();
    return WhizbangIdDocumentForm.WritingTo(
      context.Database.IsRelational() ? PostgresServerDatabase.KeyOf(context.Database.GetDbConnection()) : null, schemas);
  }

  /// <summary>
  /// Asks the server for the name of the database the context's connection reaches, unless this process knows it: once for
  /// each host and requested database, opening the connection for it if it is closed. Awaited before a scope is taken,
  /// since a scope set inside an async method would not reach its caller.
  /// </summary>
  /// <param name="context">The context.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes once the name is known.</returns>
  internal static async ValueTask ResolveAsync(DbContext context, CancellationToken cancellationToken) {
    if (!context.Database.IsRelational()) {
      return;
    }
    var connection = context.Database.GetDbConnection();
    if (!PostgresServerDatabase.KeyOf(connection).StartsWith('?')) {
      return;
    }
    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    try {
      await PostgresServerDatabase.ResolveAsync(connection, cancellationToken).ConfigureAwait(false);
    } finally {
      await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
  }
}
