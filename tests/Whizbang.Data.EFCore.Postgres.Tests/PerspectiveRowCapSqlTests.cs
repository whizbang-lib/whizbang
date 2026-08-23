using Npgsql;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the count-cap sweep: keep the newest N rows per scope, ranked by business time.
/// </summary>
/// <remarks>
/// <para>
/// Time-based retention bounds AGE but never CARDINALITY — a heavy scope can hold thousands of rows
/// all created inside the window. A cap is a rank rather than an instant, so it cannot fold into the
/// effective-expiry ladder; it is a second rule unioned with it, on a slower cadence because ranking
/// needs a window function no index avoids.
/// </para>
/// <para>
/// Ranking by <c>updated_at</c> — business time — is what makes eviction reproducible under replay.
/// On a wall-clock column a rebuild would rewrite every row's timestamp to the rebuild moment in
/// write order, so the cap would evict essentially arbitrary rows.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[NotInParallel("PerspectiveRowCap")]
[Category("Shard3")]
public class PerspectiveRowCapSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_per_rowcap";
  private const string CLR_TYPE = "TestApp.RowCapModel";

  private async Task _resetAsync(NpgsqlConnection conn, int? cap, string? scopeKey, bool acknowledged = true) {
    await using (var ddl = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      DELETE FROM wh_perspective_registry WHERE clr_type_name = '{CLR_TYPE}';
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES ('{CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc');
      UPDATE wh_perspective_registry SET row_retention_enrolled = TRUE, retention_enforcement_acknowledged = {(acknowledged ? "TRUE" : "FALSE")} WHERE clr_type_name = '{CLR_TYPE}';", conn)) {
      await ddl.ExecuteNonQueryAsync();
    }

    await using var set = new NpgsqlCommand(
      "UPDATE wh_perspective_registry SET row_cap_per_scope = @cap, row_cap_scope_key = @key " +
      "WHERE clr_type_name = @t", conn);
    set.Parameters.AddWithValue("t", CLR_TYPE);
    set.Parameters.Add(new NpgsqlParameter("cap", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = (object?)cap ?? DBNull.Value
    });
    set.Parameters.Add(new NpgsqlParameter("key", NpgsqlTypes.NpgsqlDbType.Text) {
      Value = (object?)scopeKey ?? DBNull.Value
    });
    await set.ExecuteNonQueryAsync();
  }

  private async Task _seedAsync(NpgsqlConnection conn, Guid id, string user, int updatedDaysAgo) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, jsonb_build_object('u', @u),
              NOW() - make_interval(days => @d), NOW() - make_interval(days => @d), 1)", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("u", user);
    cmd.Parameters.AddWithValue("d", updatedDaysAgo);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _sweepAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT reap_perspective_row_caps()", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<(int Rows, string Status)> _sweepBatchedAsync(NpgsqlConnection conn, int batchSize) {
    await using var cmd = new NpgsqlCommand(
      "SELECT rows_affected, status FROM reap_perspective_row_caps(@b)", conn);
    cmd.Parameters.AddWithValue("b", batchSize);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.GetString(1));
  }

  private static async Task<bool> _survivesAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
  }

  [Test]
  public async Task Cap_KeepsNewestPerScope_AndEvictsTheRestAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, cap: 2, scopeKey: "u");

    var newest = Guid.CreateVersion7();
    var middle = Guid.CreateVersion7();
    var oldest = Guid.CreateVersion7();
    await _seedAsync(conn, newest, "alice", updatedDaysAgo: 1);
    await _seedAsync(conn, middle, "alice", updatedDaysAgo: 5);
    await _seedAsync(conn, oldest, "alice", updatedDaysAgo: 50);

    await _sweepAsync(conn);

    await Assert.That(await _survivesAsync(conn, newest)).IsTrue();
    await Assert.That(await _survivesAsync(conn, middle)).IsTrue();
    await Assert.That(await _survivesAsync(conn, oldest)).IsFalse()
      .Because("the cap keeps the N most recently ACTIVE rows and evicts the coldest — bounding "
        + "cardinality, which a time window alone never does");
  }

  [Test]
  public async Task Cap_IsPerScope_NotWholeTableAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, cap: 1, scopeKey: "u");

    var alice = Guid.CreateVersion7();
    var bob = Guid.CreateVersion7();
    await _seedAsync(conn, alice, "alice", updatedDaysAgo: 10);
    await _seedAsync(conn, bob, "bob", updatedDaysAgo: 20);

    await _sweepAsync(conn);

    await Assert.That(await _survivesAsync(conn, alice)).IsTrue();
    await Assert.That(await _survivesAsync(conn, bob)).IsTrue()
      .Because("each scope gets its own allowance — bob's only row is not evicted because alice also "
        + "has one; ranking is partitioned, not global");
  }

  [Test]
  public async Task NoCapDeclared_EvictsNothingAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, cap: null, scopeKey: null);

    var a = Guid.CreateVersion7();
    var b = Guid.CreateVersion7();
    await _seedAsync(conn, a, "alice", updatedDaysAgo: 100);
    await _seedAsync(conn, b, "alice", updatedDaysAgo: 200);

    await _sweepAsync(conn);

    await Assert.That(await _survivesAsync(conn, a)).IsTrue();
    await Assert.That(await _survivesAsync(conn, b)).IsTrue()
      .Because("an enrolled perspective with no declared cap is bounded by age alone; absent must "
        + "stay distinct from a cap of zero, which would evict everything");
  }

  [Test]
  public async Task Cap_Unacknowledged_ReportsButRemovesNothingAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, cap: 1, scopeKey: "u", acknowledged: false);

    var newest = Guid.CreateVersion7();
    var overflow = Guid.CreateVersion7();
    await _seedAsync(conn, newest, "alice", updatedDaysAgo: 1);
    await _seedAsync(conn, overflow, "alice", updatedDaysAgo: 50);

    await _sweepAsync(conn);

    await Assert.That(await _survivesAsync(conn, overflow)).IsTrue()
      .Because("a declared cap REPORTS before it removes — until the operator acknowledges "
        + "enforcement, the first sweep after an upgrade must not mass-evict a backlog that "
        + "accumulated while no cap existed");

    await using (var ack = new NpgsqlCommand(
      $"UPDATE wh_perspective_registry SET retention_enforcement_acknowledged = TRUE WHERE clr_type_name = '{CLR_TYPE}'", conn)) {
      await ack.ExecuteNonQueryAsync();
    }
    await _sweepAsync(conn);

    await Assert.That(await _survivesAsync(conn, overflow)).IsFalse()
      .Because("acknowledgement un-gates the cap exactly like the expiry ladder");
    await Assert.That(await _survivesAsync(conn, newest)).IsTrue();
  }

  [Test]
  public async Task Cap_BatchBound_DrainsAcrossSweeps_AndSaysSoAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, cap: 1, scopeKey: "u");

    var keeper = Guid.CreateVersion7();
    await _seedAsync(conn, keeper, "alice", updatedDaysAgo: 1);
    var overflow = new List<Guid>();
    for (var i = 0; i < 4; i++) {
      var id = Guid.CreateVersion7();
      overflow.Add(id);
      await _seedAsync(conn, id, "alice", updatedDaysAgo: 10 + i);
    }

    var first = await _sweepBatchedAsync(conn, batchSize: 2);
    await Assert.That(first.Rows).IsEqualTo(2)
      .Because("the sweep takes at most the batch bound per cycle — a first sweep over a large "
        + "backlog must not evict everything in one statement");
    await Assert.That(first.Status).Contains("draining")
      .Because("hitting the bound is reported, so an operator watching the first enforcement "
        + "cycle can see the backlog draining rather than wondering why rows remain");

    var second = await _sweepBatchedAsync(conn, batchSize: 5000);
    await Assert.That(second.Rows).IsEqualTo(2);
    await Assert.That(second.Status).IsEqualTo("ok")
      .Because("under the bound means the backlog is drained");

    await Assert.That(await _survivesAsync(conn, keeper)).IsTrue();
    foreach (var id in overflow) {
      await Assert.That(await _survivesAsync(conn, id)).IsFalse()
        .Because("the batch bound paces eviction; it never changes WHO is evicted");
    }
  }
}
