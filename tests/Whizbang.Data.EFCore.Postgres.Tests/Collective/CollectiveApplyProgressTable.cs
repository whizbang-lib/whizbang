// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// Creates the collective apply progress table (#1969) in a test database that runs no migrations, from the migration
/// that defines it, so a fixture's table can never drift from the one a migrated database holds.
/// </summary>
internal static class CollectiveApplyProgressTable {
  private const string CREATE = "CREATE TABLE IF NOT EXISTS";

  /// <summary>Creates the table in <paramref name="schema"/> over <paramref name="connection"/>.</summary>
  public static async Task CreateAsync(NpgsqlConnection connection, string schema = "public") {
    var migration = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, schema)
      .GetMigration("279_CollectiveApplyProgress")!.Sql;
    var start = migration.IndexOf(CREATE + " " + schema + ".wh_collective_apply_progress", StringComparison.Ordinal);
    var end = migration.IndexOf(");", start, StringComparison.Ordinal) + 2;
    await using var command = new NpgsqlCommand(migration[start..end], connection);
    await command.ExecuteNonQueryAsync();
  }
}
