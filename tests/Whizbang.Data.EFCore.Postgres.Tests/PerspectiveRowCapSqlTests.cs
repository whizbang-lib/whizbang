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
public class PerspectiveRowCapSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_per_rowcap";
  private const string CLR_TYPE = "TestApp.RowCapModel";

  private async Task _resetAsync(NpgsqlConnection conn, int? cap, string? scopeKey) {
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
      UPDATE wh_perspective_registry SET row_retention_enrolled = TRUE WHERE clr_type_name = '{CLR_TYPE}';", conn)) {
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
}
