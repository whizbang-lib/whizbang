// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The stream lifecycle ledger (<c>wh_stream_lifecycle</c>) arrives in migration 260 (#1732), which ships with the
/// archive store (#1736) and before it. Until that migration is on the branch these tests run on, the archive tests
/// create the table with the ledger's planned columns (a no-op once 260 has created it) and install the archived-version
/// guard the way migration 262 does once the table exists.
/// </summary>
internal static class StreamLifecycleTestTable {
  internal static async Task EnsureAsync(NpgsqlConnection connection) {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      CREATE TABLE IF NOT EXISTS wh_stream_lifecycle (
        stream_id UUID PRIMARY KEY,
        stream_type TEXT,
        tenant TEXT,
        first_appended_at TIMESTAMPTZ,
        last_appended_bucket TIMESTAMPTZ,
        last_version BIGINT,
        approx_bytes BIGINT,
        state TEXT,
        sealed_at TIMESTAMPTZ,
        disposition_due_at TIMESTAMPTZ,
        archive_locator TEXT,
        origin_generation BIGINT);
      SELECT _wh_install_archived_version_guard();
      """;
    await command.ExecuteNonQueryAsync();
  }
}
