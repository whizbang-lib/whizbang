using Npgsql;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the reaper half of the effective-expiry ladder: the sweep is driven by ENROLMENT in
/// <c>wh_perspective_registry</c>, and resolves override → sliding rule → absolute cap → never.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the sweep enumerated every <c>wh_per_*</c> table carrying an <c>expires_at</c>
/// column — which, since the column is part of the standard perspective DDL, meant every
/// perspective table in the database — and could only act on a stamped value. Reading enrolment and
/// windows from the registry lets it skip perspectives that declared nothing and derive expiry for
/// those that did.
/// </para>
/// <para>
/// The load-bearing case is the cap: its disjunct deliberately carries NO <c>expires_at IS NULL</c>
/// guard, unlike the sliding one. That asymmetry is what makes an absolute ceiling unbreachable by
/// a per-row override, and it reads like an oversight beside its neighbours — so it is named here
/// to stop someone "fixing" it into a bug.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[NotInParallel("EnrolledRowReaper")]
public class EnrolledRowReaperSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_per_enrolled_reap";
  private const string CLR_TYPE = "TestApp.EnrolledReapModel";

  private async Task _resetAsync(NpgsqlConnection conn, bool enrolled, int? ttlSeconds, int? maxAgeSeconds) {
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
      VALUES ('{CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc');", conn)) {
      await ddl.ExecuteNonQueryAsync();
    }

    await using var sync = new NpgsqlCommand(
      "SELECT sync_perspective_retention(@t, @e, @ttl, @max)", conn);
    sync.Parameters.AddWithValue("t", CLR_TYPE);
    sync.Parameters.AddWithValue("e", enrolled);
    sync.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = (object?)ttlSeconds ?? DBNull.Value
    });
    sync.Parameters.Add(new NpgsqlParameter("max", NpgsqlTypes.NpgsqlDbType.Integer) {
      Value = (object?)maxAgeSeconds ?? DBNull.Value
    });
    await sync.ExecuteNonQueryAsync();
  }

  private async Task _seedAsync(
      NpgsqlConnection conn, Guid id, int createdDaysAgo, int updatedDaysAgo, DateTime? expiresAt) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version, expires_at)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb,
              NOW() - make_interval(days => @c), NOW() - make_interval(days => @u), 1, @e)", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("c", createdDaysAgo);
    cmd.Parameters.AddWithValue("u", updatedDaysAgo);
    cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.TimestampTz) {
      Value = (object?)expiresAt ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _reapAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand("SELECT reap_enrolled_perspective_rows()", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _survivesAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
  }

  [Test]
  public async Task SlidingRule_ReapsIdleRowsWithNoStampedExpiryAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, enrolled: true, ttlSeconds: 60 * 60 * 24 * 60, maxAgeSeconds: null);

    var stale = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    await _seedAsync(conn, stale, createdDaysAgo: 200, updatedDaysAgo: 90, expiresAt: null);
    await _seedAsync(conn, fresh, createdDaysAgo: 200, updatedDaysAgo: 1, expiresAt: null);

    await _reapAsync(conn);

    await Assert.That(await _survivesAsync(conn, stale)).IsFalse()
      .Because("a row idle past the sliding window is reaped from its business time alone — no stamped "
        + "expiry is required, which is what governs rows predating the declaration");
    await Assert.That(await _survivesAsync(conn, fresh)).IsTrue();
  }

  [Test]
  public async Task ExplicitExpiry_OverridesTheSlidingRuleAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, enrolled: true, ttlSeconds: 60 * 60 * 24 * 60, maxAgeSeconds: null);

    var pinned = Guid.CreateVersion7();
    await _seedAsync(conn, pinned, createdDaysAgo: 200, updatedDaysAgo: 90,
      expiresAt: DateTime.UtcNow.AddDays(30));

    await _reapAsync(conn);

    await Assert.That(await _survivesAsync(conn, pinned)).IsTrue()
      .Because("an explicit expiry replaces the sliding term, so a deliberately pinned row survives "
        + "being idle past the window");
  }

  [Test]
  public async Task AbsoluteCap_BindsEvenAgainstAnExplicitOverrideAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, enrolled: true, ttlSeconds: 60 * 60 * 24 * 60, maxAgeSeconds: 60 * 60 * 24 * 365);

    // Recently active AND pinned far into the future — but older than the ceiling.
    var overCap = Guid.CreateVersion7();
    await _seedAsync(conn, overCap, createdDaysAgo: 400, updatedDaysAgo: 1,
      expiresAt: DateTime.UtcNow.AddYears(5));

    await _reapAsync(conn);

    await Assert.That(await _survivesAsync(conn, overCap)).IsFalse()
      .Because("the cap is a ceiling, not a competing term: a per-row write must not defeat a retention "
        + "limit declared in code. Its disjunct deliberately carries no expires_at IS NULL guard — that "
        + "asymmetry IS the guarantee");
  }

  [Test]
  public async Task NotEnrolled_IsNeverSweptAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _resetAsync(conn, enrolled: false, ttlSeconds: null, maxAgeSeconds: null);

    // Ancient, idle, and even carrying a long-past stamped expiry.
    var ancient = Guid.CreateVersion7();
    await _seedAsync(conn, ancient, createdDaysAgo: 3000, updatedDaysAgo: 3000,
      expiresAt: DateTime.UtcNow.AddYears(-2));

    await _reapAsync(conn);

    await Assert.That(await _survivesAsync(conn, ancient)).IsTrue()
      .Because("enrolment is what tells the reaper where to look; a perspective that declared nothing "
        + "is not swept at all, however stale its rows look");
  }
}
