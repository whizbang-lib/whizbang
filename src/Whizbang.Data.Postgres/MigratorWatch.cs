// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// What an instance that lost the migrator election watches while it waits: whether another
/// instance is migrating the schema right now.
/// </summary>
/// <remarks>
/// <para>
/// The migrator duty is held by assignment (#966 phase 4): a row with a lease, renewed by the
/// migrator and held live while its marked backend runs a statement. A waiter therefore watches
/// that row. During a rolling deploy the migrator may be an instance on an older release that holds
/// the duty by its own session lock; such an instance records the holding, and a waiter watches
/// that record too, judged by the one liveness rule (#2044). No lock is read.
/// </para>
/// <para>
/// Either one means "keep waiting"; neither means the migrator finished or is gone, and the caller
/// then reads the schema to tell which. A database whose role functions do not exist yet (one the
/// bootstrap could not bring up) has no migrator to watch.
/// </para>
/// <para>
/// The functions are found by reading <c>pg_proc</c>, never by <c>to_regprocedure</c>: a waiter may call
/// this inside a transaction that began before another session committed them, and a name lookup is
/// answered from the session's catalog cache, which keeps a "no such function" until a statement opens a
/// relation. The catalog read is answered from the statement's snapshot, and opening <c>pg_proc</c>
/// brings the cache current for the statement that then calls the functions (#2038).
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorWatchTests.cs</tests>
public static class MigratorWatch {
  /// <summary>Whether an instance other than the caller is migrating <paramref name="schema"/>.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema, in either the raw or the quoted spelling.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True while the migrator's assignment is live, or a live instance holds the duty outside the table.</returns>
  public static async Task<bool> IsMigratingElsewhereAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);

    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty, StringComparison.Ordinal);
    var quoted = "\"" + target.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    await using (var exists = new NpgsqlCommand(
        "SELECT count(DISTINCT p.proname) = 2 FROM pg_catalog.pg_proc p "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace "
        + "WHERE n.nspname = $1 AND p.proname IN ('_role_lapse_reason', '_role_held_outside_assignment')", connection)) {
      exists.Parameters.AddWithValue(target);
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return false;
      }
    }

    await using var held = new NpgsqlCommand(
      $"SELECT EXISTS (SELECT 1 FROM {quoted}.wh_role_assignments r "
      + $"WHERE r.role = $1 AND r.holder_instance_id IS NOT NULL AND {quoted}._role_lapse_reason(r) IS NULL) "
      + $"OR {quoted}._role_held_outside_assignment($1, NULL)", connection);
    held.Parameters.AddWithValue(StartupDuties.MIGRATOR);
    return await held.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }
}
