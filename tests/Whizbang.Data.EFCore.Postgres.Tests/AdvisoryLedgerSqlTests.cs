using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The advisory ledger's decision, driven against the real SQL function.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of moving this out of process memory is that the decision is now shared and
/// durable, and neither property can be tested in C#. What can go wrong lives in the round trip:
/// whether the cooldown is compared in the right direction, whether a changed signature really
/// overrides it, whether the row a refusal leaves behind still records that the finding was seen,
/// and whether first_seen_at is preserved by the upsert that moves everything else.
/// </para>
/// <para>
/// The last one matters most and is the easiest to lose. ON CONFLICT DO UPDATE names the columns it
/// moves, so adding first_seen_at to that list would quietly reset the clock on every later report,
/// and the column would still be populated and still look right.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/168_AdvisoryLedger.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PostgresAdvisoryLedger.cs</code-under-test>
[Category("Shard3")]
[Category("Integration")]
public class AdvisoryLedgerSqlTests : EFCoreTestBase {

  private static readonly TimeSpan _week = TimeSpan.FromDays(7);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  /// <summary>Calls the function the way the ledger does, and answers what it answered.</summary>
  private static async Task<bool> _tryBeginAsync(
      NpgsqlConnection connection, string key, string signature, DateTimeOffset now, TimeSpan? cooldown = null) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT wh_advisory_try_begin_report(@key, @sig, @now, @cooldown)";
    cmd.Parameters.AddWithValue("key", key);
    cmd.Parameters.AddWithValue("sig", signature);
    cmd.Parameters.AddWithValue("now", now);
    cmd.Parameters.AddWithValue("cooldown", cooldown ?? _week);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(DateTimeOffset FirstSeen, DateTimeOffset LastReported, DateTimeOffset LastTouched, int Count)>
      _rowAsync(NpgsqlConnection connection, string key) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText =
      "SELECT first_seen_at, last_reported_at, last_touched, report_count FROM wh_advisory_ledger WHERE finding_key = @key";
    cmd.Parameters.AddWithValue("key", key);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (await reader.GetFieldValueAsync<DateTimeOffset>(0),
            await reader.GetFieldValueAsync<DateTimeOffset>(1),
            await reader.GetFieldValueAsync<DateTimeOffset>(2),
            await reader.GetFieldValueAsync<int>(3));
  }

  private static string _key() => $"perspective-index:Probe:{Guid.NewGuid():N}";

  [Test]
  public async Task AFirstSightingIsReportedAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var key = _key();

    await Assert.That(await _tryBeginAsync(connection, key, "JobName", DateTimeOffset.UtcNow)).IsTrue()
      .Because("a finding nobody has reported has to be granted, or the report that creates the row "
             + "never happens and the ledger only ever refuses.");
  }

  [Test]
  public async Task TheSameAdviceWithinTheCooldownIsRefusedAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;
    await _tryBeginAsync(connection, key, "JobName", t0);

    var again = await _tryBeginAsync(connection, key, "JobName", t0.AddDays(6));

    await Assert.That(again).IsFalse()
      .Because("this is consulted from a cycle that runs every 30 seconds, so granting unchanged "
             + "advice again is the wall of identical warnings the table exists to stop.");
  }

  [Test]
  public async Task AdviceThatHasChangedIsReportedAtOnceAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;
    await _tryBeginAsync(connection, key, "JobName", t0);

    var changed = await _tryBeginAsync(connection, key, "JobName,Status", t0.AddMinutes(1));

    await Assert.That(changed).IsTrue()
      .Because("another field reaching an unindexed sort is different advice about the same table, "
             + "and waiting out the cooldown would leave the operator acting on the stale version.");
  }

  [Test]
  public async Task TheSameAdviceAfterTheCooldownIsReportedAgainAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;
    await _tryBeginAsync(connection, key, "JobName", t0);

    var again = await _tryBeginAsync(connection, key, "JobName", t0 + _week);

    await Assert.That(again).IsTrue()
      .Because("a finding suppressed forever cannot be told apart from one that was fixed, and the "
             + "table is still being scanned either way.");
  }

  [Test]
  public async Task ARefusedSightingStillRecordsThatItWasSeenAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;
    await _tryBeginAsync(connection, key, "JobName", t0);

    var seenAt = t0.AddDays(1);
    await _tryBeginAsync(connection, key, "JobName", seenAt);
    var row = await _rowAsync(connection, key);

    await Assert.That(row.LastTouched).IsGreaterThanOrEqualTo(seenAt.AddSeconds(-1))
      .Because("the gap between when a finding was last seen and last reported is how an operator "
             + "tells suppression from a finding that stopped being detected.");
    await Assert.That(row.LastReported).IsLessThan(seenAt.AddSeconds(-1))
      .Because("and the refusal must not move last_reported_at, or the cooldown would restart on "
             + "every cycle and the advice would never come back at all.");
    await Assert.That(row.Count).IsEqualTo(1)
      .Because("a refusal is not a report.");
  }

  [Test]
  public async Task FirstSeenAtSurvivesEveryLaterReportAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;
    await _tryBeginAsync(connection, key, "JobName", t0);
    var firstSeen = (await _rowAsync(connection, key)).FirstSeen;

    // Changed advice, then the cooldown elapsing: both paths through the upsert.
    await _tryBeginAsync(connection, key, "JobName,Status", t0.AddMinutes(1));
    await _tryBeginAsync(connection, key, "JobName,Status", t0 + _week + TimeSpan.FromMinutes(1));
    var row = await _rowAsync(connection, key);

    await Assert.That(row.FirstSeen).IsEqualTo(firstSeen)
      .Because("how long a finding has gone unaddressed is the question an operator acts on, and "
             + "adding first_seen_at to the columns the upsert moves would reset that clock on every "
             + "report while leaving the column populated and plausible.");
    await Assert.That(row.Count).IsEqualTo(3)
      .Because("and each granted report counts, so a finding that keeps coming back does not read "
             + "as a first sighting forever.");
  }

  /// <summary>Two findings are two rows, not one shared decision.</summary>
  [Test]
  public async Task ADifferentFindingIsTrackedSeparatelyAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var t0 = DateTimeOffset.UtcNow;
    await _tryBeginAsync(connection, _key(), "JobName", t0);

    await Assert.That(await _tryBeginAsync(connection, _key(), "JobName", t0)).IsTrue()
      .Because("the key is the identity of a finding, so sharing a decision between findings would "
             + "silence every table after the first one.");
  }
}
