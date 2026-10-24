// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// Issue #1646: the partition manager keeps a table range-partitioned on a version-7 <c>uuid</c> as it keeps one on a
/// <c>timestamptz</c>. A version-7 id begins with its creation time in Unix milliseconds, so the first id of a day is
/// that day's midnight in the first 48 bits and zeros after; a partition is the ids from one midnight to the next.
/// </summary>
/// <remarks>
/// Each test owns a probe table and passes the moment explicitly, and every id is made for a fixed moment
/// (<see cref="Guid.CreateVersion7(DateTimeOffset)"/>), so no test reads a clock.
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#time-ordered-ids</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/233_PartitionSignalsAndLifecycleCompletions.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class UuidPartitionSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_partition_uuid_probe";
  private const string NOW = "2026-10-21 12:00:00+00";
  private const string DAY = "1 day";
  private const string CREATED = "created";
  private const string DEFAULT_PARTITION = TABLE + "_default";

  // A version-4 id: its first 48 bits read as a moment in the year 8,000-odd, so no daily partition ever covers it.
  private static readonly Guid _version4 = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479");

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("SET statement_timeout = '30s'");
    return connection;
  }

  private static Task _createProbeAsync(NpgsqlConnection connection) =>
    connection.ExecuteAsync($"CREATE TABLE {TABLE} (id UUID NOT NULL PRIMARY KEY, note TEXT) PARTITION BY RANGE (id)");

  private static async Task<List<(string Action, string Name)>> _maintainAsync(
      NpgsqlConnection connection, string now, int ahead, string? keep = null) {
    var results = new List<(string Action, string Name)>();
    await using var command = connection.CreateCommand();
    command.CommandText =
      $"SELECT action, partition_name FROM wh_partition_maintain('{TABLE}', '{DAY}', @ahead, @keep::interval, @now::timestamptz)";
    command.Parameters.AddWithValue(nameof(ahead), ahead);
    command.Parameters.Add(new NpgsqlParameter(nameof(keep), NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)keep ?? DBNull.Value });
    command.Parameters.AddWithValue(nameof(now), now);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      results.Add((reader.GetString(0), reader.GetString(1)));
    }
    return results;
  }

  private static Guid _idAt(string moment) =>
    Guid.CreateVersion7(DateTimeOffset.Parse(moment, CultureInfo.InvariantCulture));

  private static Task _insertAsync(NpgsqlConnection connection, Guid id, string note) =>
    connection.ExecuteAsync($"INSERT INTO {TABLE} (id, note) VALUES (@id, @note)", new { id, note });

  private static Task<string> _partitionOfAsync(NpgsqlConnection connection, string note) =>
    connection.ExecuteScalarAsync<string>($"SELECT tableoid::regclass::text FROM {TABLE} WHERE note = @note", new { note });

  private static Task<List<string>> _partitionsAsync(NpgsqlConnection connection) =>
    connection.QueryAsync<string>(
      $"SELECT c.relname::text FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid WHERE i.inhparent = '{TABLE}'::regclass ORDER BY 1");

  private static List<string> _named(List<(string Action, string Name)> results, string action) =>
    [.. results.Where(r => r.Action == action).Select(r => r.Name)];

  [Test]
  public async Task AUuidBound_IsTheFirstIdOfItsMoment_AndReadsBackAsThatMomentAsync() {
    await using var connection = await _openAsync();
    var midnight = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    var hex = midnight.ToUnixTimeMilliseconds().ToString("x12", CultureInfo.InvariantCulture);
    var expected = $"{hex[..8]}-{hex[8..]}-0000-0000-000000000000";

    var bound = await connection.ExecuteScalarAsync<string>(
      "SELECT _wh_partition_bound('2026-10-21 00:00:00+00'::timestamptz, 'uuid'::regtype)");
    var back = await connection.ExecuteScalarAsync<DateTime>(
      "SELECT _wh_partition_bound_at(quote_literal(@bound), 'uuid'::regtype)", new { bound });
    var timeBack = await connection.ExecuteScalarAsync<DateTime>(
      "SELECT _wh_partition_bound_at(quote_literal(_wh_partition_bound('2026-10-21 00:00:00+00'::timestamptz, 'timestamptz'::regtype)), " +
      "'timestamptz'::regtype)");

    await Assert.That(bound).IsEqualTo(expected);
    await Assert.That(back).IsEqualTo(midnight.UtcDateTime);
    await Assert.That(timeBack).IsEqualTo(midnight.UtcDateTime)
      .Because("a timestamptz bound round-trips the same way, so one parser serves both key types");
    await Assert.That(await connection.ExecuteScalarAsync<bool>(
        "SELECT _wh_partition_bound_at('MINVALUE', 'uuid'::regtype) = '-infinity' AND _wh_partition_bound_at('MAXVALUE', 'uuid'::regtype) = 'infinity'"))
      .IsTrue();
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        "SELECT _wh_partition_bound('1969-12-31 00:00:00+00'::timestamptz, 'uuid'::regtype)"))
      .IsEqualTo("00000000-0000-0000-0000-000000000000")
      .Because("no version-7 id is older than the Unix epoch, so a moment before it bounds at the smallest id");
  }

  [Test]
  public async Task ACycle_CreatesDailyIdPartitions_AndAVersion7IdLandsInItsDayAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);

    var results = await _maintainAsync(connection, NOW, ahead: 1);
    await _insertAsync(connection, _idAt("2026-10-21 23:59:59.999+00"), "late-on-the-21st");
    await _insertAsync(connection, _idAt("2026-10-22 00:00:00+00"), "first-of-the-22nd");

    await Assert.That(_named(results, CREATED))
      .IsEquivalentTo([DEFAULT_PARTITION, TABLE + "_p20261021", TABLE + "_p20261022"]);
    await Assert.That(await _partitionOfAsync(connection, "late-on-the-21st")).IsEqualTo(TABLE + "_p20261021");
    await Assert.That(await _partitionOfAsync(connection, "first-of-the-22nd")).IsEqualTo(TABLE + "_p20261022")
      .Because("an id made at midnight is at or above that day's lower bound");
    await Assert.That(await _maintainAsync(connection, NOW, ahead: 1)).IsEmpty();
  }

  [Test]
  public async Task AVersion4Id_LandsInTheDefaultPartitionAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, NOW, ahead: 3);

    await _insertAsync(connection, _version4, "random");

    await Assert.That(await _partitionOfAsync(connection, "random")).IsEqualTo(DEFAULT_PARTITION)
      .Because("an id that is not time-ordered is accepted, and kept by the default partition, which nothing drops");
  }

  [Test]
  public async Task APartitionWhollyPastTheWindow_IsDropped_OneStraddlingItIsKeptAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, "2026-10-15 12:00:00+00", ahead: 3);
    await _insertAsync(connection, _idAt("2026-10-15 08:00:00+00"), "expired");
    await _insertAsync(connection, _idAt("2026-10-17 18:00:00+00"), "straddling");

    // Four days before the moment is 2026-10-17 12:00: the 15th and the 16th ended before it, the 17th straddles it.
    var results = await _maintainAsync(connection, NOW, ahead: 0, keep: "4 days");

    await Assert.That(_named(results, "dropped")).IsEquivalentTo([TABLE + "_p20261015", TABLE + "_p20261016"]);
    await Assert.That(await _partitionsAsync(connection)).Contains(TABLE + "_p20261017");
    await Assert.That(await connection.QueryAsync<string>($"SELECT note FROM {TABLE}")).IsEquivalentTo(["straddling"]);
  }

  [Test]
  public async Task IdsInTheDefaultPartition_MoveIntoThePartitionCreatedForThemAsync() {
    await using var connection = await _openAsync();
    await _createProbeAsync(connection);
    await _maintainAsync(connection, NOW, ahead: 0);
    await _insertAsync(connection, _idAt("2026-10-22 09:00:00+00"), "early");
    await _insertAsync(connection, _version4, "random");

    var results = await _maintainAsync(connection, "2026-10-22 12:00:00+00", ahead: 0);

    await Assert.That(_named(results, CREATED)).IsEquivalentTo([TABLE + "_p20261022"]);
    await Assert.That(await _partitionOfAsync(connection, "early")).IsEqualTo(TABLE + "_p20261022");
    await Assert.That(await _partitionOfAsync(connection, "random")).IsEqualTo(DEFAULT_PARTITION);
  }

  [Test]
  public async Task AConversion_ByUuid_MovesIdsPastItsBoundToTheDefault_InsteadOfFailingAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync($"CREATE TABLE {TABLE} (id UUID NOT NULL PRIMARY KEY, note TEXT)");
    await _insertAsync(connection, _idAt("2026-10-21 08:00:00+00"), "today");
    await _insertAsync(connection, _version4, "random");
    await _insertAsync(connection, _idAt("2026-10-25 08:00:00+00"), "ahead-of-the-clock");

    var outcome = await connection.ExecuteScalarAsync<string>($"SELECT wh_partition_convert('{TABLE}', 'id', '{DAY}', '{NOW}')");

    await Assert.That(outcome).IsEqualTo("converted");
    await Assert.That(await _partitionOfAsync(connection, "today")).IsEqualTo(TABLE + "_legacy");
    await Assert.That(await _partitionOfAsync(connection, "random")).IsEqualTo(DEFAULT_PARTITION)
      .Because("the legacy table covers every id below tomorrow's first; one above it would fail the attach, so it moves first");
    await Assert.That(await _partitionOfAsync(connection, "ahead-of-the-clock")).IsEqualTo(DEFAULT_PARTITION);
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '{TABLE}'::regclass AND contype = 'p'"))
      .IsEqualTo("PRIMARY KEY (id)")
      .Because("a key that is already the partition column stays as it was");
  }

  [Test]
  public async Task ATimestampConversion_MovesRowsPastItsBoundToTheDefaultAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(
      $"CREATE TABLE {TABLE} (id BIGSERIAL PRIMARY KEY, at TIMESTAMPTZ NOT NULL, note TEXT); " +
      $"INSERT INTO {TABLE} (at, note) VALUES ('2026-10-21 08:00:00+00', 'today'), ('2026-10-30 08:00:00+00', 'future')");

    var outcome = await connection.ExecuteScalarAsync<string>($"SELECT wh_partition_convert('{TABLE}', 'at', '{DAY}', '{NOW}')");

    await Assert.That(outcome).IsEqualTo("converted");
    await Assert.That(await _partitionOfAsync(connection, "today")).IsEqualTo(TABLE + "_legacy");
    await Assert.That(await _partitionOfAsync(connection, "future")).IsEqualTo(DEFAULT_PARTITION)
      .Because("a row stamped past the bound, by a clock running ahead, no longer stops the conversion");
  }

  [Test]
  public async Task AUuidColumnThatAllowsNull_IsRejectedAsync() {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync($"CREATE TABLE {TABLE} (id UUID, note TEXT)");

    var error = await Assert.That(async () =>
      await connection.ExecuteScalarAsync<string>($"SELECT wh_partition_convert('{TABLE}', 'id', '{DAY}')"))
      .Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.InvalidParameterValue);
  }
}
