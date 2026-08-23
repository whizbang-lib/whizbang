using Npgsql;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the repair that makes schema-qualifying the four framework tables invisible to operators:
/// state already sitting in <c>public</c> is carried into the service schema on the next startup.
/// </summary>
/// <remarks>
/// <para>
/// Before qualification those tables resolved through <c>search_path</c> and landed in
/// <c>public</c>. Qualifying them without a repair would hand a non-public deployment a brand-new
/// EMPTY table — settings silently reverting to defaults and the DLQ appearing drained — which is
/// a worse failure than the sharing it fixes, because it looks like success.
/// </para>
/// <para>
/// The copy is additive and conflict-skipping in both directions that matter: re-running is a
/// no-op, and a value already set service-locally outranks the inherited one. The public copies are
/// left in place on purpose — another service on a shared database may still be reading them.
/// </para>
/// </remarks>
/// <docs>contributors/data-engines/writing-migrations</docs>
[NotInParallel("PublicFrameworkTableRepair")]
[Category("Shard1")]
public class PublicFrameworkTableRepairSqlTests : EFCoreTestBase {
  private const string SCHEMA = "svc_repair_probe";
  private const string PROBE_KEY = "repair_probe_setting";

  private static string _repairSqlFor(string schema) =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, schema)
      .GetMigration("105_RepairPublicFrameworkTables")!.Sql;

  /// <summary>
  /// Stands up the shape a pre-qualification deployment is actually in: real rows in
  /// <c>public</c>, an empty service schema whose tables the qualified migrations just created.
  /// </summary>
  private async Task _arrangeAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($@"
      DROP SCHEMA IF EXISTS {SCHEMA} CASCADE;
      CREATE SCHEMA {SCHEMA};
      CREATE TABLE {SCHEMA}.wh_settings     (LIKE public.wh_settings     INCLUDING ALL);
      CREATE TABLE {SCHEMA}.wh_dead_letters (LIKE public.wh_dead_letters INCLUDING ALL);

      DELETE FROM public.wh_settings WHERE setting_key = '{PROBE_KEY}';
      INSERT INTO public.wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('{PROBE_KEY}', 'from-public', 'string', 'pre-qualification value');", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _cleanupAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($@"
      DROP SCHEMA IF EXISTS {SCHEMA} CASCADE;
      DELETE FROM public.wh_settings WHERE setting_key = '{PROBE_KEY}';", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<string?> _settingInAsync(NpgsqlConnection conn, string schema) {
    await using var cmd = new NpgsqlCommand(
      $"SELECT setting_value FROM {schema}.wh_settings WHERE setting_key = @k", conn);
    cmd.Parameters.AddWithValue("k", PROBE_KEY);
    return await cmd.ExecuteScalarAsync() as string;
  }

  [Test]
  public async Task Repair_CarriesPublicSettingsIntoTheServiceSchemaAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    try {
      await Assert.That(await _settingInAsync(conn, SCHEMA)).IsNull()
        .Because("the qualified migration creates the service-schema table EMPTY — this is the "
          + "state the repair exists to fix");

      await using (var repair = new NpgsqlCommand(_repairSqlFor(SCHEMA), conn)) {
        await repair.ExecuteNonQueryAsync();
      }

      await Assert.That(await _settingInAsync(conn, SCHEMA)).IsEqualTo("from-public")
        .Because("configuration must follow the service across the qualification, or debug_mode and "
          + "every retention knob silently revert to their defaults on the next deploy");
    } finally {
      await _cleanupAsync(conn);
    }
  }

  [Test]
  public async Task Repair_OverwritesTheSeededDefaultOnFirstRunAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    try {
      // Stand in for what migrations 028/032/073/076/089/092/093 do: they seed defaults into the
      // freshly-created service table, and every one of them runs BEFORE this repair.
      await using (var seeded = new NpgsqlCommand($@"
        INSERT INTO {SCHEMA}.wh_settings (setting_key, setting_value, value_type)
        VALUES ('{PROBE_KEY}', 'seeded-default', 'string')", conn)) {
        await seeded.ExecuteNonQueryAsync();
      }

      await using (var repair = new NpgsqlCommand(_repairSqlFor(SCHEMA), conn)) {
        await repair.ExecuteNonQueryAsync();
      }

      await Assert.That(await _settingInAsync(conn, SCHEMA)).IsEqualTo("from-public")
        .Because("on first run the service table holds nothing but defaults this same migration run "
          + "just seeded, so the operator's value in public is the one that must survive — a "
          + "conflict-skipping copy would quietly reset debug_mode and every retention knob");
    } finally {
      await _cleanupAsync(conn);
    }
  }

  [Test]
  public async Task Repair_NeverTouchesTheSchemaAgainOnceTheBoundaryIsClosedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    try {
      await using (var repair = new NpgsqlCommand(_repairSqlFor(SCHEMA), conn)) {
        await repair.ExecuteNonQueryAsync();
      }

      // After adoption the service owns its configuration; public is stale by definition.
      await using (var local = new NpgsqlCommand($@"
        UPDATE {SCHEMA}.wh_settings SET setting_value = 'set-after-adoption'
         WHERE setting_key = '{PROBE_KEY}'", conn)) {
        await local.ExecuteNonQueryAsync();
      }

      await using (var again = new NpgsqlCommand(_repairSqlFor(SCHEMA), conn)) {
        await again.ExecuteNonQueryAsync();
      }

      await Assert.That(await _settingInAsync(conn, SCHEMA)).IsEqualTo("set-after-adoption")
        .Because("the marker row is a one-time boundary: 'public wins' holds only while the service "
          + "table is still nothing but seeded defaults, and 'service wins' from then on. Without "
          + "the gate the two rules would contradict each other on every later run");
    } finally {
      await _cleanupAsync(conn);
    }
  }

  [Test]
  public async Task Repair_IsIdempotentAndLeavesThePublicCopyInPlaceAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    try {
      for (var i = 0; i < 3; i++) {
        await using var repair = new NpgsqlCommand(_repairSqlFor(SCHEMA), conn);
        await repair.ExecuteNonQueryAsync();
      }

      await using (var count = new NpgsqlCommand(
        $"SELECT COUNT(*) FROM {SCHEMA}.wh_settings WHERE setting_key = @k", conn)) {
        count.Parameters.AddWithValue("k", PROBE_KEY);
        var rows = Convert.ToInt64(
          await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        await Assert.That(rows).IsEqualTo(1)
          .Because("migrations re-run on every startup whose hash changed, so the copy has to be a "
            + "no-op the second time");
      }

      await Assert.That(await _settingInAsync(conn, "public")).IsEqualTo("from-public")
        .Because("another service on a shared database may still be reading the public copy, so a "
          + "startup migration must not drop it — pruning is an operator decision");
    } finally {
      await _cleanupAsync(conn);
    }
  }

  [Test]
  public async Task Repair_IsANoOpWhenTheServiceSchemaIsPublicAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    try {
      // The overwhelmingly common deployment: __SCHEMA__ IS public, so source and target are one
      // table and the copy would be a self-insert.
      await using (var repair = new NpgsqlCommand(_repairSqlFor("public"), conn)) {
        await repair.ExecuteNonQueryAsync();
      }

      await Assert.That(await _settingInAsync(conn, "public")).IsEqualTo("from-public");
      await Assert.That(await _settingInAsync(conn, SCHEMA)).IsNull()
        .Because("a single-schema deployment has nothing to carry, and the repair must not reach "
          + "into a schema that is not its own");
    } finally {
      await _cleanupAsync(conn);
    }
  }
}
