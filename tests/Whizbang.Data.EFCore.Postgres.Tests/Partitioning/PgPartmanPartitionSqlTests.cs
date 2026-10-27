// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// Issue #1752: <c>wh_partition_partman</c>, one table's pg_partman pass. It re-bounds a converted table's oldest
/// partition from MINVALUE to the smallest key in three committed steps (pg_partman cannot read MINVALUE), registers
/// the table with <c>create_parent</c> once from the first interval no partition covers, keeps premake, clock-made
/// partitions and retention off in <c>part_config</c>, and calls <c>run_maintenance</c> for the table.
/// </summary>
/// <remarks>
/// <para>
/// The test server has no pg_partman, so these tests install a stand-in schema whose <c>create_parent</c> and
/// <c>run_maintenance</c> have pg_partman 5.5's exact signatures and record each call, and pass it as
/// <c>p_partman_schema</c>. That proves what the pass asks of pg_partman; what pg_partman then does with it was
/// verified by hand against a pg_partman 5.5 server (the CI image has no pg_partman).
/// </para>
/// <para>Every moment is passed explicitly, so no test reads a clock.</para>
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/246_PartitionManagementByPgPartman.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgPartmanPartitionSqlTests : EFCoreTestBase {
  private const string PARTMAN = "fake_partman";
  private const string TABLE = "wh_partition_probe";
  private const string UUID_TABLE = "wh_partition_uuid_probe";
  private const string NOW = "2026-10-21 12:00:00+00";
  private const string DAY = "1 day";
  private const string REBOUNDING = "rebounding";
  private const string MAINTAINED = "maintained";

  // pg_partman 5.5's signatures, recorded rather than acted on.
  private const string FAKE_PARTMAN = $"""
    CREATE SCHEMA {PARTMAN};
    CREATE TABLE {PARTMAN}.part_config (
      parent_table TEXT PRIMARY KEY, control TEXT NOT NULL, partition_interval TEXT NOT NULL, premake INT NOT NULL,
      start_partition TEXT, default_table BOOLEAN, automatic_maintenance TEXT, jobmon BOOLEAN,
      time_encoder TEXT, time_decoder TEXT, retention TEXT, infinite_time_partitions BOOLEAN NOT NULL DEFAULT false);
    CREATE TABLE {PARTMAN}.calls (n SERIAL, fn TEXT NOT NULL, parent_table TEXT, jobmon BOOLEAN);
    CREATE FUNCTION {PARTMAN}.create_parent(
      p_parent_table text, p_control text, p_interval text, p_type text DEFAULT 'range', p_epoch text DEFAULT 'none',
      p_premake integer DEFAULT 4, p_start_partition text DEFAULT NULL, p_default_table boolean DEFAULT true,
      p_automatic_maintenance text DEFAULT 'on', p_constraint_cols text[] DEFAULT NULL, p_template_table text DEFAULT NULL,
      p_jobmon boolean DEFAULT true, p_date_trunc_interval text DEFAULT NULL, p_control_not_null boolean DEFAULT true,
      p_time_encoder text DEFAULT NULL, p_time_decoder text DEFAULT NULL, p_offset_id bigint DEFAULT 0)
    RETURNS boolean AS $$
      INSERT INTO {PARTMAN}.calls (fn, parent_table, jobmon) VALUES ('create_parent', p_parent_table, p_jobmon);
      INSERT INTO {PARTMAN}.part_config (parent_table, control, partition_interval, premake, start_partition,
        default_table, automatic_maintenance, jobmon, time_encoder, time_decoder)
      VALUES (p_parent_table, p_control, p_interval, p_premake, p_start_partition, p_default_table,
        p_automatic_maintenance, p_jobmon, p_time_encoder, p_time_decoder);
      SELECT true;
    $$ LANGUAGE sql;
    CREATE FUNCTION {PARTMAN}.run_maintenance(
      p_parent_table text DEFAULT NULL, p_analyze boolean DEFAULT false, p_jobmon boolean DEFAULT true)
    RETURNS void AS $$
      INSERT INTO {PARTMAN}.calls (fn, parent_table, jobmon) VALUES ('run_maintenance', p_parent_table, p_jobmon);
    $$ LANGUAGE sql;
    """;

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("SET statement_timeout = '30s'");
    return connection;
  }

  private static async Task<List<(string Action, string Name)>> _passAsync(
      NpgsqlConnection connection, string table = TABLE, int ahead = 3, string? partman = PARTMAN, string now = NOW) {
    var results = new List<(string Action, string Name)>();
    await using var command = connection.CreateCommand();
    command.CommandText =
      "SELECT action, partition_name FROM wh_partition_partman(@table, @interval::interval, @ahead, @partman, @now::timestamptz)";
    command.Parameters.AddWithValue(nameof(table), table);
    command.Parameters.AddWithValue("interval", DAY);
    command.Parameters.AddWithValue(nameof(ahead), ahead);
    command.Parameters.Add(new NpgsqlParameter(nameof(partman), NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)partman ?? DBNull.Value });
    command.Parameters.AddWithValue(nameof(now), now);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      results.Add((reader.GetString(0), reader.GetString(1)));
    }
    return results;
  }

  private static List<string> _actions(List<(string Action, string Name)> results) => [.. results.Select(r => r.Action)];

  // A plain table the conversion turns into a partitioned one whose oldest partition is bounded below by MINVALUE.
  private static async Task _convertedProbeAsync(NpgsqlConnection connection) {
    await connection.ExecuteAsync($"CREATE TABLE {TABLE} (id BIGSERIAL PRIMARY KEY, at TIMESTAMPTZ NOT NULL, note TEXT)");
    await connection.ExecuteAsync($"INSERT INTO {TABLE} (at, note) VALUES ('2026-10-01 08:00+00', 'old'), ('2026-10-21 09:00+00', 'today')");
    await connection.ExecuteScalarAsync<string>($"SELECT wh_partition_convert('{TABLE}', 'at', '{DAY}', '{NOW}'::timestamptz)");
  }

  private static Task<string?> _boundOfAsync(NpgsqlConnection connection, string partition) =>
    connection.ExecuteScalarAsync<string?>(
      "SELECT pg_get_expr(c.relpartbound, c.oid) FROM pg_class c WHERE c.relname = @partition", new { partition });

  private static Task<long> _callsAsync(NpgsqlConnection connection, string fn) =>
    connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {PARTMAN}.calls WHERE fn = @fn", new { fn });

  [Test]
  public async Task AConvertedTablesOldestPartition_IsReboundedFromTheSmallestKey_InThreeStepsAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(FAKE_PARTMAN);
    await _convertedProbeAsync(connection);
    var legacy = TABLE + "_legacy";
    await Assert.That(await _boundOfAsync(connection, legacy)).IsEqualTo("FOR VALUES FROM (MINVALUE) TO ('2026-10-22 00:00:00+00')");

    var first = await _passAsync(connection);
    var second = await _passAsync(connection);
    var checkAfterTwo = await connection.ExecuteScalarAsync<bool>(
      $"SELECT convalidated FROM pg_constraint WHERE conrelid = '{legacy}'::regclass AND conname = 'wh_partition_lower_bound'");
    var third = await _passAsync(connection);

    await Assert.That(_actions(first)).IsEquivalentTo([REBOUNDING]).Because("step one adds the check, unvalidated, and nothing else");
    await Assert.That(_actions(second)).IsEquivalentTo([REBOUNDING]).Because("step two validates it");
    await Assert.That(checkAfterTwo).IsTrue();
    await Assert.That(_actions(third)).IsEquivalentTo(["rebounded", "registered", MAINTAINED])
      .Because("only a table pg_partman can read is given to it");
    await Assert.That(await _boundOfAsync(connection, legacy)).IsEqualTo("FOR VALUES FROM ('-infinity') TO ('2026-10-22 00:00:00+00')");
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      $"SELECT count(*) FROM pg_constraint WHERE conrelid = '{legacy}'::regclass AND conname = 'wh_partition_lower_bound'"))
      .IsEqualTo(0L).Because("the check only carried the new bound past the attach");
    await Assert.That(await connection.ExecuteScalarAsync<string>($"SELECT string_agg(note, ',' ORDER BY at) FROM {legacy}"))
      .IsEqualTo("old,today").Because("the partition holds exactly the rows it held");
  }

  [Test]
  public async Task AUuidKeyedTablesOldestPartition_IsReboundedFromTheEmptyUuidAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(FAKE_PARTMAN);
    await connection.ExecuteAsync($"CREATE TABLE {UUID_TABLE} (id UUID NOT NULL PRIMARY KEY, note TEXT)");
    await connection.ExecuteAsync($"INSERT INTO {UUID_TABLE} (id, note) VALUES (@id, 'today')",
      new { id = Guid.CreateVersion7(DateTimeOffset.Parse("2026-10-21 09:00+00", CultureInfo.InvariantCulture)) });
    await connection.ExecuteScalarAsync<string>($"SELECT wh_partition_convert('{UUID_TABLE}', 'id', '{DAY}', '{NOW}'::timestamptz)");

    for (var step = 0; step < 3; step++) {
      await _passAsync(connection, UUID_TABLE);
    }

    await Assert.That(await _boundOfAsync(connection, UUID_TABLE + "_legacy"))
      .IsEqualTo("FOR VALUES FROM ('00000000-0000-0000-0000-000000000000') TO ('01a160e8-5000-0000-0000-000000000000')");
    await Assert.That(await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {UUID_TABLE}_legacy")).IsEqualTo(1L);
  }

  [Test]
  public async Task TheRebound_WhileTheTableIsInUse_IsDeferred_AndChangesNothingAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(FAKE_PARTMAN);
    await _convertedProbeAsync(connection);
    await using var reader = await _openAsync();
    await using var transaction = await reader.BeginTransactionAsync();
    await using (var hold = new NpgsqlCommand($"SELECT count(*) FROM {TABLE}_legacy", reader, transaction)) {
      await hold.ExecuteScalarAsync();
    }

    var results = await _passAsync(connection);

    await Assert.That(_actions(results)).IsEquivalentTo(["deferred"]);
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      $"SELECT count(*) FROM pg_constraint WHERE conrelid = '{TABLE}_legacy'::regclass AND conname = 'wh_partition_lower_bound'"))
      .IsEqualTo(0L);
    await Assert.That(await _callsAsync(connection, "create_parent")).IsEqualTo(0L);
  }

  [Test]
  public async Task TheFirstPass_RegistersTheTableOnce_FromTheFirstUncoveredInterval_AndMaintainsItAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(FAKE_PARTMAN);
    await connection.ExecuteAsync(
      $"CREATE TABLE {TABLE} (id BIGSERIAL, at TIMESTAMPTZ NOT NULL, PRIMARY KEY (id, at)) PARTITION BY RANGE (at)");
    // The built-in manager kept it before: 10-21 to 10-24 exist.
    await connection.ExecuteAsync($"SELECT * FROM wh_partition_maintain('{TABLE}', '{DAY}', 3, NULL, '{NOW}'::timestamptz)");

    var first = await _passAsync(connection);
    var second = await _passAsync(connection);

    await Assert.That(_actions(first)).IsEquivalentTo(["registered", MAINTAINED]);
    await Assert.That(_actions(second)).IsEquivalentTo([MAINTAINED]).Because("a registered table is not registered again");
    await Assert.That(await _callsAsync(connection, "create_parent")).IsEqualTo(1L);
    await Assert.That(await _callsAsync(connection, "run_maintenance")).IsEqualTo(2L);
    var config = await connection.QuerySingleAsync<(string Parent, string Control, string Interval, int Premake, string Start)>(
      $"SELECT parent_table, control, partition_interval, premake, start_partition FROM {PARTMAN}.part_config");
    var flags = await connection.QuerySingleAsync<(bool DefaultTable, string Automatic, bool Jobmon, string? Encoder, string? Retention, bool Infinite)>(
      $"SELECT default_table, automatic_maintenance, jobmon, time_encoder, retention, infinite_time_partitions FROM {PARTMAN}.part_config");
    await Assert.That(config.Parent).IsEqualTo("public." + TABLE);
    await Assert.That(config.Control).IsEqualTo("at");
    await Assert.That(config.Interval).IsEqualTo(DAY);
    await Assert.That(config.Premake).IsEqualTo(3).Because("pg_partman creates as far ahead as the built-in manager would");
    await Assert.That(config.Start).IsEqualTo("2026-10-25 00:00:00+00")
      .Because("pg_partman starts at the first day no partition covers, so it never overlaps one the built-in manager made");
    await Assert.That(flags.DefaultTable).IsFalse().Because("the manager keeps the default partition itself");
    await Assert.That(flags.Automatic).IsEqualTo("off").Because("this pass is the maintenance, on the maintainer duty's holder");
    await Assert.That(flags.Jobmon).IsFalse();
    await Assert.That(flags.Encoder).IsNull();
    await Assert.That(flags.Retention).IsNull().Because("the built-in manager drops, carrying unconsumed signals first");
    await Assert.That(flags.Infinite).IsTrue().Because("partitions are made from the clock, not from the newest row");
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      $"SELECT count(*) FROM {PARTMAN}.calls WHERE fn = 'run_maintenance' AND parent_table = 'public.{TABLE}' AND NOT jobmon"))
      .IsEqualTo(2L).Because("run_maintenance is called for this table alone");
  }

  [Test]
  public async Task AUuidKey_IsRegisteredWithTheVersion7EncoderAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(FAKE_PARTMAN);
    await connection.ExecuteAsync($"CREATE TABLE {UUID_TABLE} (id UUID NOT NULL PRIMARY KEY) PARTITION BY RANGE (id)");

    await _passAsync(connection, UUID_TABLE);

    var config = await connection.QuerySingleAsync<(string? Encoder, string? Decoder, string Start)>(
      $"SELECT time_encoder, time_decoder, start_partition FROM {PARTMAN}.part_config");
    await Assert.That(config.Encoder).IsEqualTo(PARTMAN + ".uuid7_time_encoder");
    await Assert.That(config.Decoder).IsEqualTo(PARTMAN + ".uuid7_time_decoder");
    await Assert.That(config.Start).IsEqualTo("2026-10-21 00:00:00+00").Because("with no partition yet, it starts at the current day");
  }

  [Test]
  public async Task RetentionSetOnTheTable_IsTurnedBackOff_SoTheBuiltInDropCarriesSignalsFirstAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(FAKE_PARTMAN);
    await connection.ExecuteAsync(
      $"CREATE TABLE {TABLE} (id BIGSERIAL, at TIMESTAMPTZ NOT NULL, PRIMARY KEY (id, at)) PARTITION BY RANGE (at)");
    await _passAsync(connection);
    await connection.ExecuteAsync($"UPDATE {PARTMAN}.part_config SET retention = '1 day', premake = 9, infinite_time_partitions = false");

    var results = await _passAsync(connection, ahead: 5);
    var again = await _passAsync(connection, ahead: 5);

    await Assert.That(_actions(results)).IsEquivalentTo(["configured", MAINTAINED]);
    await Assert.That(_actions(again)).IsEquivalentTo([MAINTAINED]).Because("settings already as needed are not reported");
    var config = await connection.QuerySingleAsync<(string? Retention, int Premake, bool Infinite)>(
      $"SELECT retention, premake, infinite_time_partitions FROM {PARTMAN}.part_config");
    await Assert.That(config.Retention).IsNull();
    await Assert.That(config.Premake).IsEqualTo(5).Because("premake follows the create-ahead option");
    await Assert.That(config.Infinite).IsTrue();
  }

  [Test]
  public async Task WithoutPgPartman_ThePassRaisesNamingTheSettingAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      $"CREATE TABLE {TABLE} (id BIGSERIAL, at TIMESTAMPTZ NOT NULL, PRIMARY KEY (id, at)) PARTITION BY RANGE (at)");

    var error = await Assert.That(async () => await _passAsync(connection, partman: null)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.UndefinedObject);
    await Assert.That(error.MessageText).Contains("Whizbang:Workers:Maintenance:Partitioning:Strategy");
  }

  [Test]
  public async Task APlainTable_IsSkippedAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(FAKE_PARTMAN);
    await connection.ExecuteAsync($"CREATE TABLE {TABLE} (id BIGSERIAL PRIMARY KEY, at TIMESTAMPTZ NOT NULL)");

    var results = await _passAsync(connection);

    await Assert.That(results).IsEquivalentTo([("skipped", TABLE)]);
    await Assert.That(await _callsAsync(connection, "create_parent")).IsEqualTo(0L);
  }

  [Test]
  public async Task ANegativeAhead_IsRejectedAsync() {
    await using var connection = await _openAsync();

    var error = await Assert.That(async () => await _passAsync(connection, ahead: -1)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
  }
}
