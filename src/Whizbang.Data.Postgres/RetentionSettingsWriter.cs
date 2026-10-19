// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The one statement both Postgres work coordinators use to publish the host's database-side settings to
/// <c>wh_settings</c>, so the EF Core and Dapper engines cannot drift on what the sweep reads.
/// </summary>
/// <remarks>
/// <para>
/// Every setting a maintenance cycle publishes travels in one <c>INSERT ... SELECT FROM unnest(...)</c> upsert, so
/// the cycle costs one round trip however many options are set. The caller passes each key at most once; a key
/// listed twice would fail the statement ("ON CONFLICT DO UPDATE command cannot affect row a second time").
/// </para>
/// <para>
/// <c>updated_at</c> and <c>updated_by</c> are stamped on every write, so a row the host published says when it
/// last did, and a row an operator wrote by hand is told apart from it. The description is written only when the
/// row is new, naming the configuration key it came from; an existing row keeps the description its migration
/// gave it.
/// </para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RetentionSettingsSyncSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRetentionSettingsSyncTests.cs</tests>
public static class RetentionSettingsWriter {

  /// <summary>Builds the upsert for a settings table name, schema-qualified or bare.</summary>
  /// <param name="settingsTable">The table name as it should appear in the statement.</param>
  /// <returns>The statement, taking <c>text[]</c> parameters <c>keys</c>, <c>vals</c>, <c>types</c> and <c>descs</c>.</returns>
  public static string UpsertSql(string settingsTable) => $@"
      INSERT INTO {settingsTable} (setting_key, setting_value, value_type, description, updated_at, updated_by)
      SELECT s.k, s.v, s.t, 'Published from configuration ' || s.d, NOW(), 'whizbang'
      FROM unnest(@keys, @vals, @types, @descs) AS s(k, v, t, d)
      ON CONFLICT (setting_key) DO UPDATE
        SET setting_value = EXCLUDED.setting_value,
            value_type    = EXCLUDED.value_type,
            updated_at    = NOW(),
            updated_by    = EXCLUDED.updated_by";

  /// <summary>Writes every setting in one statement; nothing at all when there is nothing to write.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="settingsTable">The settings table name as it should appear in the statement.</param>
  /// <param name="settings">The settings to write, each key at most once.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>A task that completes when the settings are stored.</returns>
  public static async Task WriteAsync(
      NpgsqlConnection connection, string settingsTable, IReadOnlyList<RetentionSetting> settings,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(settings);
    if (settings.Count == 0) {
      return;
    }

    await using var cmd = CoordinatorCommandTimeout.Apply(connection.CreateCommand());
#pragma warning disable S2077 // the table name is the framework's own, schema-qualified by the caller; every value is a parameter
    cmd.CommandText = UpsertSql(settingsTable);
#pragma warning restore S2077
    _addTextArray(cmd, "keys", settings.Select(s => s.Key));
    _addTextArray(cmd, "vals", settings.Select(s => s.Value));
    _addTextArray(cmd, "types", settings.Select(s => s.ValueType));
    _addTextArray(cmd, "descs", settings.Select(s => s.Source));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static void _addTextArray(NpgsqlCommand cmd, string name, IEnumerable<string> values) =>
    cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });
}
