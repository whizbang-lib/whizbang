using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the REPLAY path for migration 090's <c>first_seen_at</c> column.
///
/// <para>
/// 090 shipped without this column, so deployments already running it have the table but not the
/// column. Editing a migration in place changes its content hash, which makes the runner re-apply
/// it — and re-application has to work against BOTH shapes: the fresh install where the CREATE
/// TABLE already includes the column, and the existing install where only the idempotent ALTER can
/// add it. Getting that wrong is not theoretical: an earlier ledger migration shipped a replay that
/// referenced a column the target database did not have yet, and every pod crash-looped on
/// <c>42703 column does not exist</c>.
/// </para>
///
/// <para>
/// The fresh-install shape is covered by every other test in this project. This covers the one the
/// harness cannot reach on its own: a database that predates the column.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
[Category("Shard4")]
public class IntegrityLedgerMigrationRerunSqlTests : EFCoreTestBase {

  /// <summary>The parts of 090 that must survive re-application, verbatim in shape.</summary>
  private const string REPLAY_DDL = """
    CREATE TABLE IF NOT EXISTS wh_integrity_ledger (
      origin_service_id  UUID        NOT NULL,
      tenant_scope       TEXT        NOT NULL DEFAULT '',
      event_type         TEXT        NOT NULL,
      stream_id          UUID        NOT NULL,
      origin_lo          BIGINT      NOT NULL,
      origin_hi          BIGINT      NOT NULL,
      local_lo           BIGINT      NOT NULL,
      local_hi           BIGINT      NOT NULL,
      last_reported_at   TIMESTAMPTZ,
      last_repair_at     TIMESTAMPTZ,
      repair_attempts    INTEGER     NOT NULL DEFAULT 0,
      last_touched       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      first_seen_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      PRIMARY KEY (origin_service_id, tenant_scope, event_type, stream_id)
    );

    ALTER TABLE wh_integrity_ledger
      ADD COLUMN IF NOT EXISTS first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
    """;

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task<bool> _columnExistsAsync(NpgsqlConnection conn, string column) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'wh_integrity_ledger' AND column_name = @c)
      """;
    cmd.Parameters.AddWithValue("c", column);
    return (bool)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Migration090_ReAppliedToALedgerPredatingTheColumn_AddsItAsync() {
    await using var conn = await _openAsync();

    // Simulate the deployed shape: 090 applied before first_seen_at existed.
    await _execAsync(conn, "ALTER TABLE wh_integrity_ledger DROP COLUMN IF EXISTS first_seen_at;");
    await Assert.That(await _columnExistsAsync(conn, "first_seen_at")).IsFalse()
      .Because("precondition: this is the shape every already-deployed database is in");

    await _execAsync(conn, REPLAY_DDL);

    await Assert.That(await _columnExistsAsync(conn, "first_seen_at")).IsTrue()
      .Because("the in-place edit re-applies against an existing table, so the ALTER — not the "
               + "CREATE — is what has to add the column");
  }

  [Test]
  public async Task Migration090_ReAppliedToACurrentLedger_IsANoOpAsync() {
    await using var conn = await _openAsync();

    await Assert.That(await _columnExistsAsync(conn, "first_seen_at")).IsTrue()
      .Because("precondition: the fresh fixture ran the whole chain");

    // Must not throw. A replay that errored here would crash-loop every pod on the deploy after
    // the first, which is precisely how the last ledger migration failure presented.
    await _execAsync(conn, REPLAY_DDL);

    await Assert.That(await _columnExistsAsync(conn, "first_seen_at")).IsTrue()
      .Because("idempotent: replaying leaves the column intact");
  }

  [Test]
  public async Task LedgerSummary_WorksOnALedgerThatJustGainedTheColumnAsync() {
    // The column is NOT NULL DEFAULT NOW(), so rows that predate it are backfilled with the
    // moment of the ALTER rather than left null. That is the honest reading available — their real
    // first-seen time was never recorded — and it must not break the age calculation.
    await using var conn = await _openAsync();
    await _execAsync(conn, "ALTER TABLE wh_integrity_ledger DROP COLUMN IF EXISTS first_seen_at;");
    await _execAsync(conn, """
      INSERT INTO wh_integrity_ledger (origin_service_id, tenant_scope, event_type, stream_id,
        origin_lo, origin_hi, local_lo, local_hi, repair_attempts)
      VALUES ('cccc3333-0000-0000-0000-00000000cccc', 'tenant-x', 'T, A',
              'dddd4444-0000-0000-0000-00000000dddd', 1, 2, 3, 4, 0)
      ON CONFLICT DO NOTHING;
      """);
    await _execAsync(conn, REPLAY_DDL);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT unhealed_buckets, oldest_unhealed_secs FROM wh_integrity_ledger_summary(8)";
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();

    await Assert.That(reader.GetInt64(0)).IsGreaterThanOrEqualTo(1)
      .Because("a pre-existing row still counts as unhealed after the column is added");
    await Assert.That(reader.GetDouble(1)).IsGreaterThanOrEqualTo(0)
      .Because("backfilled rows get the ALTER's timestamp, so age stays computable rather than null");
  }
}
