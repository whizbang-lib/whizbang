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
/// that row. It also watches the duty's session lock, because during a rolling deploy the migrator
/// may be an instance on a release that still holds the duty by session lock.
/// </para>
/// <para>
/// Either one held means "keep waiting"; neither means the migrator finished or is gone, and the
/// caller then reads the schema to tell which. A database whose role functions do not exist yet
/// (one the bootstrap could not bring up) answers from the session lock alone.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorWatchTests.cs</tests>
public static class MigratorWatch {
  /// <summary>Whether an instance other than the caller is migrating <paramref name="schema"/>.</summary>
  /// <param name="connection">An open connection. Its own session's locks do not count.</param>
  /// <param name="schema">The schema, in either the raw or the quoted spelling.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True while the migrator's assignment is live or its session lock is held elsewhere.</returns>
  public static async Task<bool> IsMigratingElsewhereAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);

    if (await AdvisoryLockProbe.IsHeldElsewhereAsync(
        connection, DutyLockKey.Compute(schema, StartupDuties.MIGRATOR), cancellationToken).ConfigureAwait(false)) {
      return true;
    }

    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty, StringComparison.Ordinal);
    var quoted = "\"" + target.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    await using (var exists = new NpgsqlCommand("SELECT to_regprocedure($1) IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue($"{quoted}._role_lapse_reason({quoted}.wh_role_assignments)");
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return false;
      }
    }

    await using var held = new NpgsqlCommand(
      $"SELECT EXISTS (SELECT 1 FROM {quoted}.wh_role_assignments r "
      + $"WHERE r.role = $1 AND r.holder_instance_id IS NOT NULL AND {quoted}._role_lapse_reason(r) IS NULL)", connection);
    held.Parameters.AddWithValue(StartupDuties.MIGRATOR);
    return await held.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
  }
}
