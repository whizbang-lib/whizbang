// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Whether a relation exists, read from <c>pg_class</c> under the statement's snapshot rather than resolved by name.
/// </summary>
/// <remarks>
/// <para>
/// A name lookup (<c>to_regclass</c>, <c>to_regprocedure</c>, a <c>::regclass</c> cast) is answered from the session's
/// catalog cache, and a transaction refreshes that cache only when it starts, when it takes a lock on a relation it has
/// not locked yet, and when the parser fails to resolve a name. Inside a transaction that looked a name up before
/// another session committed a create or a drop, the lookup keeps the old answer. A read of the catalog table takes the
/// statement's snapshot, which a read committed statement takes fresh, so it sees the commit (#2009, #2038).
/// </para>
/// <para>
/// Used where a method takes its caller's connection, which may carry an open transaction, and turns the answer into a
/// decision. A lookup that is the first of its name in a transaction of its own is fresh and stays a name lookup; the
/// audit that decided which is which is <c>ai-docs/catalog-existence-checks.md</c>.
/// </para>
/// </remarks>
/// <docs>contributors/data-engines/writing-migrations#catalog-existence-checks</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcilerTests.cs:InsideATransactionThatBeganBeforeTheLedgerWasCommitted_NamesTheTableForRebuildingAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBootstrapPhaseTests.cs:AWaiterWhoseSessionCachedTheRecordTableAsMissing_StillFindsTheHoldersRecord_AndAppliesNothingAsync</tests>
internal static class CatalogPresence {
  /// <summary>The query: <c>$1</c> is the schema, bare; <c>$2</c> the relation's name, bare.</summary>
  internal const string RELATION_EXISTS_SQL =
    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class AS c JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace "
    + "WHERE n.nspname = $1 AND c.relname = $2)";

  /// <summary>Whether <paramref name="schema"/> holds a relation named <paramref name="relation"/>.</summary>
  /// <param name="connection">An open connection, in a transaction or not.</param>
  /// <param name="schema">The schema, bare (unquoted).</param>
  /// <param name="relation">The relation's name, bare (unquoted).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns><see langword="true"/> when the relation is committed, or created earlier in the caller's transaction.</returns>
  internal static async Task<bool> RelationExistsAsync(
      NpgsqlConnection connection, string schema, string relation, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(RELATION_EXISTS_SQL, connection);
    command.Parameters.AddWithValue(schema);
    command.Parameters.AddWithValue(relation);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }
}
