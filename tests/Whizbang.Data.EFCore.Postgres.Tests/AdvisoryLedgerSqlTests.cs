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

  // ============================================================
  // Through the wrapper, not just the function
  // ============================================================

  /// <summary>
  /// The C# that calls the function agrees with it about parameters and about the answer.
  /// </summary>
  /// <remarks>
  /// Every test above drives the SQL directly, which says nothing about the type that reaches it in
  /// production. A wrapper can look correct and still bind the wrong parameter shape or mistranslate
  /// the result, and this one turns anything unexpected into a fallback, which reads exactly like a
  /// legitimate refusal.
  /// </remarks>
  [Test]
  public async Task TheLedgerTypeAgreesWithTheFunctionAboutBothAnswersAsync() {
    await using var ctx = CreateDbContext();
    await _openAsync(ctx);
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var ledger = new PostgresAdvisoryLedger(dataSource);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;

    var first = await ledger.TryBeginReportAsync(key, "JobName", t0, _week);
    var repeat = await ledger.TryBeginReportAsync(key, "JobName", t0.AddDays(1), _week);
    var changed = await ledger.TryBeginReportAsync(key, "JobName,Status", t0.AddDays(1), _week);

    await Assert.That(first).IsTrue();
    await Assert.That(repeat).IsFalse()
      .Because("a refusal has to survive the round trip as a refusal; the fallback this type applies "
             + "on any trouble would also answer true here, so a bound-parameter mistake would look "
             + "like the feature working.");
    await Assert.That(changed).IsTrue();
  }

  /// <summary>
  /// A database it cannot reach degrades to per-process suppression, and does not throw.
  /// </summary>
  /// <remarks>
  /// This is the whole reason the fallback is a real ledger rather than a hardcoded answer. Failing
  /// open restores the flood the durable ledger exists to stop; failing closed silences a genuine
  /// finding for as long as the fault lasts. Degrading to what the framework did before this existed
  /// is the only behavior that is wrong in neither direction, and it has to hold for the LIFETIME of
  /// the instance -- a fallback constructed per call would suppress nothing at all.
  /// </remarks>
  [Test]
  public async Task AnUnreachableDatabaseDegradesToPerProcessSuppressionAsync() {
    // Port 1 refuses immediately, so this is a connection failure and not a hang.
    await using var unreachable = NpgsqlDataSource.Create(
      "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1;Command Timeout=1");
    var ledger = new PostgresAdvisoryLedger(unreachable);
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;

    var first = await ledger.TryBeginReportAsync(key, "JobName", t0, _week);
    var repeat = await ledger.TryBeginReportAsync(key, "JobName", t0.AddDays(1), _week);

    await Assert.That(first).IsTrue()
      .Because("a fault must not silence a real finding, so the first sighting still goes out.");
    await Assert.That(repeat).IsFalse()
      .Because("and it must not restore the flood either: the fallback is held for the lifetime of "
             + "this instance, so a persistent fault is once per process rather than once per cycle.");
  }

  /// <summary>
  /// A schema whose function answers something other than yes or no degrades, rather than reading
  /// the non-answer as "already reported".
  /// </summary>
  /// <remarks>
  /// <para>
  /// Unreachable against the shipped schema, where the function is declared RETURNS BOOLEAN over a
  /// body that always returns. Reachable against a schema carrying something else under that name,
  /// which is what this builds: the ledger is schema-qualified so that co-located services consult
  /// their own, and that qualification is exactly what could land it somewhere unexpected.
  /// </para>
  /// <para>
  /// The direction matters more than the likelihood. Reading a non-answer as a refusal would
  /// suppress a real finding for a whole cooldown on the strength of an answer nobody gave, and it
  /// would look identical to the suppression working.
  /// </para>
  /// </remarks>
  [Test]
  public async Task ASchemaThatAnswersNeitherYesNorNoDegradesRatherThanSuppressingAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    await using (var shadow = connection.CreateCommand()) {
      shadow.CommandText = @"
        CREATE SCHEMA IF NOT EXISTS probe_nonanswer;
        CREATE OR REPLACE FUNCTION probe_nonanswer.wh_advisory_try_begin_report(
          p_finding_key TEXT, p_signature TEXT, p_now TIMESTAMPTZ, p_cooldown INTERVAL)
        RETURNS BOOLEAN LANGUAGE sql AS 'SELECT NULL::BOOLEAN';";
      await shadow.ExecuteNonQueryAsync();
    }

    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var ledger = new PostgresAdvisoryLedger(dataSource, "probe_nonanswer");
    var key = _key();
    var t0 = DateTimeOffset.UtcNow;

    var first = await ledger.TryBeginReportAsync(key, "JobName", t0, _week);
    var repeat = await ledger.TryBeginReportAsync(key, "JobName", t0.AddDays(1), _week);

    await Assert.That(first).IsTrue()
      .Because("a ledger that cannot answer must not silence a real finding.");
    await Assert.That(repeat).IsFalse()
      .Because("and it must not report it every cycle either: the non-answer degrades to the "
             + "process-local ledger, exactly as an unreachable database does.");
  }

  /// <summary>
  /// Cancellation is passed through, not swallowed into a decision.
  /// </summary>
  /// <remarks>
  /// Every other kind of trouble here degrades to the process-local ledger, and cancellation must
  /// not: a stopping service would otherwise record a report it never made, so the finding would be
  /// suppressed for a week by a cycle that was cut short.
  /// </remarks>
  [Test]
  public async Task ACanceledConsultIsPassedThroughRatherThanDegradedAsync() {
    await using var ctx = CreateDbContext();
    await _openAsync(ctx);
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var ledger = new PostgresAdvisoryLedger(dataSource);
    var key = _key();
    using var cancelled = new CancellationTokenSource();
    await cancelled.CancelAsync();

    await Assert.That(async () =>
        await ledger.TryBeginReportAsync(key, "JobName", DateTimeOffset.UtcNow, _week, cancelled.Token))
      .Throws<OperationCanceledException>();

    await using var check = NpgsqlDataSource.Create(ConnectionString);
    var afterwards = new PostgresAdvisoryLedger(check);
    await Assert.That(await afterwards.TryBeginReportAsync(key, "JobName", DateTimeOffset.UtcNow, _week))
      .IsTrue()
      .Because("the canceled consult must leave no record, or a cycle cut short would suppress the "
             + "finding for a week without ever having reported it.");
  }

  [Test]
  public async Task ANullKeyOrSignatureIsRejectedByTheLedgerTypeAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var ledger = new PostgresAdvisoryLedger(dataSource);
    var t0 = DateTimeOffset.UtcNow;

    await Assert.That(async () => await ledger.TryBeginReportAsync(null!, "sig", t0, _week))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await ledger.TryBeginReportAsync("k", null!, t0, _week))
      .Throws<ArgumentNullException>();
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
