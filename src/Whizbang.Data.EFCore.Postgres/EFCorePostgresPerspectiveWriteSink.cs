// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The PostgreSQL write sink of the Entity Framework driver: writes a batch of prepared perspective rows through the
/// context's connection, one statement per table and statement shape.
/// </summary>
/// <remarks>
/// It joins the context's current transaction, as the per-stream write does, and otherwise lets each statement
/// commit on its own. The statements, the outcomes and the isolation of a rejected row are the shared PostgreSQL
/// ones (<c>Whizbang.Data.Postgres</c>), so a second driver writes the same way.
/// </remarks>
/// <param name="context">The context whose connection and transaction the batch is written through.</param>
/// <param name="timeProvider">The clock the system-time columns are stamped from; the system clock by default.</param>
/// <docs>fundamentals/perspectives/write-modes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SetBasedPerspectiveWriteSinkTests.cs</tests>
public sealed class EFCorePostgresPerspectiveWriteSink(DbContext context, TimeProvider? timeProvider = null) : IPerspectiveWriteSink {
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <summary>The context this sink writes through.</summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverRegistrationTests.cs:Postgres_WithTwoContexts_RegistersASinkOverEachAsync</tests>
  public DbContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

  /// <inheritdoc/>
  public async Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
      IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(writes);
    if (writes.Count == 0) {
      return [];
    }
    if (!PerspectiveRowVersionSql.Supports(Context)) {
      throw new InvalidOperationException(
        $"{nameof(EFCorePostgresPerspectiveWriteSink)} writes through Npgsql, and {Context.GetType().Name} is configured with another provider.");
    }
    var connection = (NpgsqlConnection)Context.Database.GetDbConnection();

    var openedHere = connection.State != System.Data.ConnectionState.Open;
    if (openedHere) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
    // The result is returned after the try: a return inside a try whose finally awaits leaves a
    // compiler-emitted sequence point no test can reach.
    IReadOnlyList<PerspectiveWriteOutcome> outcomes;
    try {
      outcomes = await PostgresPerspectiveWriter.WriteAsync(
        connection,
        Context.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction,
        writes,
        _timeProvider.GetUtcNow().UtcDateTime,
        cancellationToken).ConfigureAwait(false);
    } finally {
      if (openedHere) {
        await connection.CloseAsync().ConfigureAwait(false);
      }
    }
    return outcomes;
  }
}
