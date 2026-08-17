using Npgsql;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the retention-enrollment columns on <c>wh_perspective_registry</c>: the registry is how a
/// C#-side declaration reaches SQL, so the reaper can scan only enrolled perspectives instead of
/// every table that happens to carry an <c>expires_at</c> column.
/// </summary>
/// <remarks>
/// <para>
/// Enrollment and duration are separate. <c>row_ttl_seconds</c> NULL on an enrolled perspective
/// means "swept, but no default rule" — rows expire only by an explicitly assigned
/// <c>expires_at</c>. A perspective absent from enrolment is not scanned at all.
/// </para>
/// <para>
/// The registry is the natural home because it already exists, already carries the schema hash, and
/// is already reconciled at startup — so the sync comes free rather than needing its own table and
/// its own lifecycle.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[NotInParallel("PerspectiveRetentionEnrollment")]
public class PerspectiveRetentionEnrollmentSqlTests : EFCoreTestBase {
  [Test]
  public async Task Registry_CarriesRetentionEnrollmentColumnsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(@"
      SELECT column_name, data_type, is_nullable
      FROM information_schema.columns
      WHERE table_name = 'wh_perspective_registry'
        AND column_name IN ('row_retention_enrolled', 'row_ttl_seconds', 'row_max_age_seconds')
      ORDER BY column_name", conn);

    var found = new Dictionary<string, (string Type, string Nullable)>(StringComparer.Ordinal);
    await using (var reader = await cmd.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        found[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
      }
    }

    await Assert.That(found).ContainsKey("row_retention_enrolled")
      .Because("enrolment is what tells the reaper where to look — without it the sweep must "
        + "enumerate every perspective table in the database");
    await Assert.That(found["row_retention_enrolled"].Type).IsEqualTo("boolean");

    await Assert.That(found).ContainsKey("row_ttl_seconds")
      .Because("the sliding window has to reach SQL somehow; it lives only in the C# registry today");
    await Assert.That(found["row_ttl_seconds"].Nullable).IsEqualTo("YES")
      .Because("NULL means enrolled with no default rule — rows expire only by an explicit expires_at");

    await Assert.That(found).ContainsKey("row_max_age_seconds")
      .Because("the absolute cap is a second, independent anchor measured from created_at");
    await Assert.That(found["row_max_age_seconds"].Nullable).IsEqualTo("YES");
  }

  [Test]
  public async Task SyncPerspectiveRetention_UpsertsEnrollmentWithoutTouchingSchemaHashAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var clrType = $"TestApp.RetentionModel_{Guid.CreateVersion7():N}";
    await using (var seed = new NpgsqlCommand(@"
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES (@t, 'wh_per_retention_probe', '{}'::jsonb, 'hash-before', 'svc')", conn)) {
      seed.Parameters.AddWithValue("t", clrType);
      await seed.ExecuteNonQueryAsync();
    }

    await using (var sync = new NpgsqlCommand(
      "SELECT sync_perspective_retention(@t, TRUE, 5184000, NULL, NULL, NULL)", conn)) {
      sync.Parameters.AddWithValue("t", clrType);
      await sync.ExecuteNonQueryAsync();
    }

    await using var read = new NpgsqlCommand(
      "SELECT row_retention_enrolled, row_ttl_seconds, row_max_age_seconds, schema_hash " +
      "FROM wh_perspective_registry WHERE clr_type_name = @t", conn);
    read.Parameters.AddWithValue("t", clrType);
    await using var reader = await read.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();

    await Assert.That(reader.GetBoolean(0)).IsTrue();
    await Assert.That(reader.GetInt32(1)).IsEqualTo(5_184_000)
      .Because("the sliding window reaches SQL through the registry rather than being threaded into "
        + "the maintenance call on every cycle");
    await Assert.That(reader.IsDBNull(2)).IsTrue()
      .Because("no absolute cap was declared, and absent must stay distinct from zero");
    await Assert.That(reader.GetString(3)).IsEqualTo("hash-before")
      .Because("retention is not part of the table's SHAPE, so syncing it must not disturb the schema "
        + "hash and trigger a spurious drift report");
  }

  [Test]
  public async Task SyncPerspectiveRetention_IsIdempotentAndCanUnenrollAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var clrType = $"TestApp.RetentionModel_{Guid.CreateVersion7():N}";
    await using (var seed = new NpgsqlCommand(@"
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES (@t, 'wh_per_retention_probe2', '{}'::jsonb, 'h', 'svc')", conn)) {
      seed.Parameters.AddWithValue("t", clrType);
      await seed.ExecuteNonQueryAsync();
    }

    for (var i = 0; i < 2; i++) {
      await using var sync = new NpgsqlCommand(
        "SELECT sync_perspective_retention(@t, TRUE, 60, 3600, NULL, NULL)", conn);
      sync.Parameters.AddWithValue("t", clrType);
      await sync.ExecuteNonQueryAsync();
    }

    // Removing the attribute must un-enrol, or the reaper keeps sweeping a perspective whose
    // declaration is gone.
    await using (var unenroll = new NpgsqlCommand(
      "SELECT sync_perspective_retention(@t, FALSE, NULL, NULL, NULL, NULL)", conn)) {
      unenroll.Parameters.AddWithValue("t", clrType);
      await unenroll.ExecuteNonQueryAsync();
    }

    await using var read = new NpgsqlCommand(
      "SELECT row_retention_enrolled, row_ttl_seconds FROM wh_perspective_registry WHERE clr_type_name = @t", conn);
    read.Parameters.AddWithValue("t", clrType);
    await using var reader = await read.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    await Assert.That(reader.GetBoolean(0)).IsFalse();
    await Assert.That(reader.IsDBNull(1)).IsTrue()
      .Because("un-enrolling clears the window too, so a later re-enrolment cannot inherit a stale one");
  }
}
