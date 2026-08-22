using Npgsql;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The end-to-end lock for row retention: a DECLARATION reaches ENFORCEMENT. Walks the whole chain —
/// registry → sync → <c>wh_perspective_registry</c> → sweep — with nothing hand-seeded in between.
/// </summary>
/// <remarks>
/// <para>
/// This is the test whose absence let <c>[RowCap]</c> ship completely inert. Coverage was
/// barbell-shaped: generator tests asserted the emitted TEXT contained a Register call, and the SQL
/// tests (<c>EnrolledRowReaperSqlTests</c>, <c>PerspectiveRowCapSqlTests</c>,
/// <c>RetentionAdoptionSafetyTests</c>) each INSERT their own <c>wh_perspective_registry</c> row and
/// exercise the sweep from there. Both ends were right. Nobody ran the join, so four missing links in
/// the middle — no emitted registration, no cap parameters on the sync function, a coordinator binding
/// arguments it never passed, and a reconciler that ignored cap-only perspectives — were all invisible.
/// </para>
/// <para>
/// The governing rule here is therefore: <b>this file must never seed the registry's retention
/// columns.</b> It may create the registry ROW (that is the perspective existing at all), but every
/// retention value has to arrive through <c>SyncPerspectiveRetentionAsync</c> from a declaration. The
/// moment a test writes <c>row_cap_per_scope</c> itself, it stops testing the thing that broke.
/// </para>
/// <para>
/// The registries are populated directly rather than via a generated runner, because a source
/// generator cannot run inside this assembly. That seam is covered on the other side by
/// <c>PerspectiveRunnerGeneratorTests</c>, which pins the emitted Register calls — the two together
/// span attribute → enforcement.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[Category("Integration")]
[NotInParallel("RowRetentionDeclarationToEnforcement")]
[Category("Shard3")]
public class RowRetentionDeclarationToEnforcementTests : EFCoreTestBase {
  private const string TABLE = "wh_per_decl_e2e";
  private const string CLR_TYPE = "TestApp.DeclarationToEnforcementModel";

  /// <summary>A stand-in for a generated model type; only its FullName reaches the database.</summary>
  private sealed class DeclarationToEnforcementModel;

  /// <summary>
  /// Creates the perspective's table and its registry ROW — the perspective merely existing. No
  /// retention column is touched: those must arrive through the sync, which is the point.
  /// </summary>
  private async Task _arrangeAsync(NpgsqlConnection conn) {
    await using var ddl = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      DELETE FROM wh_perspective_registry WHERE clr_type_name = '{CLR_TYPE}';
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES ('{CLR_TYPE}', '{TABLE}', '{{}}'::jsonb, 'h', 'svc');", conn);
    await ddl.ExecuteNonQueryAsync();
  }

  private async Task _seedRowAsync(NpgsqlConnection conn, Guid id, string user, int idleDays) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, jsonb_build_object('u', @u),
              NOW() - make_interval(days => @d), NOW() - make_interval(days => @d), 1)", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("u", user);
    cmd.Parameters.AddWithValue("d", idleDays);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<bool> _survivesAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    return Convert.ToInt64(
      await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
  }

  /// <summary>Acknowledges enforcement — migration 104 withholds every sweep until this is set.</summary>
  private static async Task _acknowledgeAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand(
      "UPDATE wh_perspective_registry SET retention_enforcement_acknowledged = TRUE " +
      "WHERE clr_type_name = @t", conn);
    cmd.Parameters.AddWithValue("t", CLR_TYPE);
    await cmd.ExecuteNonQueryAsync();
  }

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task _syncAsync(int? ttlSeconds, int? capPerScope, string? capScopeKey) {
    await using var ctx = CreateDbContext();
    await _coordinator(ctx).SyncPerspectiveRetentionAsync([
      new PerspectiveRetentionDeclaration(
        CLR_TYPE, Enrolled: true, TtlSeconds: ttlSeconds, MaxAgeSeconds: null,
        CapPerScope: capPerScope, CapScopeKey: capScopeKey)
    ]);
  }

  [Test]
  public async Task DeclaredCap_ReachesTheRegistryAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    await _syncAsync(ttlSeconds: null, capPerScope: 2, capScopeKey: "u");

    await using var read = new NpgsqlCommand(
      "SELECT row_cap_per_scope, row_cap_scope_key FROM wh_perspective_registry " +
      "WHERE clr_type_name = @t", conn);
    read.Parameters.AddWithValue("t", CLR_TYPE);
    await using var reader = await read.ExecuteReaderAsync();
    await reader.ReadAsync();

    await Assert.That(reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0)).IsEqualTo(2)
      .Because("the declared cap must survive the whole sync path — this is the link that was missing, "
        + "where sync_perspective_retention had no cap parameter and the coordinator called it with "
        + "four arguments while binding six");
    await Assert.That(reader.IsDBNull(1) ? null : reader.GetString(1)).IsEqualTo("u")
      .Because("the scope key partitions the sweep's ranking, so losing it silently changes who evicts "
        + "whom rather than failing");
  }

  [Test]
  public async Task DeclaredCap_EvictsTheColdestRowsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    var newest = Guid.CreateVersion7();
    var middle = Guid.CreateVersion7();
    var coldest = Guid.CreateVersion7();
    await _seedRowAsync(conn, newest, "alice", idleDays: 1);
    await _seedRowAsync(conn, middle, "alice", idleDays: 5);
    await _seedRowAsync(conn, coldest, "alice", idleDays: 50);

    await _syncAsync(ttlSeconds: null, capPerScope: 2, capScopeKey: "u");
    await _acknowledgeAsync(conn);

    await using (var sweep = new NpgsqlCommand("SELECT reap_perspective_row_caps()", conn)) {
      await sweep.ExecuteNonQueryAsync();
    }

    await Assert.That(await _survivesAsync(conn, newest)).IsTrue();
    await Assert.That(await _survivesAsync(conn, middle)).IsTrue();
    await Assert.That(await _survivesAsync(conn, coldest)).IsFalse()
      .Because("declaration to eviction, end to end, with nothing about the cap hand-written into the "
        + "registry — the chain this feature shipped without");
  }

  [Test]
  public async Task CapWithoutTtl_StillEnrolsAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    // A cap is a complete policy on its own: bound cardinality, leave age alone. The reconciler used to
    // iterate the TTL registry only, so this perspective would never have been synced at all.
    await _syncAsync(ttlSeconds: null, capPerScope: 5, capScopeKey: "t");

    await using var read = new NpgsqlCommand(
      "SELECT row_retention_enrolled, row_ttl_seconds, row_cap_per_scope " +
      "FROM wh_perspective_registry WHERE clr_type_name = @t", conn);
    read.Parameters.AddWithValue("t", CLR_TYPE);
    await using var reader = await read.ExecuteReaderAsync();
    await reader.ReadAsync();

    await Assert.That(reader.GetBoolean(0)).IsTrue();
    await Assert.That(reader.IsDBNull(1)).IsTrue()
      .Because("no sliding rule was declared, which must stay distinct from a rule of zero");
    await Assert.That(reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2)).IsEqualTo(5);
  }

  [Test]
  public async Task DeclaredTtlAndCap_BothReachTheRegistryAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    // The companion case, and the one JDX ships: age AND cardinality on one perspective.
    await _syncAsync(ttlSeconds: 60 * 60 * 24 * 60, capPerScope: 200, capScopeKey: "u");

    await using var read = new NpgsqlCommand(
      "SELECT row_ttl_seconds, row_cap_per_scope FROM wh_perspective_registry " +
      "WHERE clr_type_name = @t", conn);
    read.Parameters.AddWithValue("t", CLR_TYPE);
    await using var reader = await read.ExecuteReaderAsync();
    await reader.ReadAsync();

    await Assert.That(reader.GetInt32(0)).IsEqualTo(5184000);
    await Assert.That(reader.GetInt32(1)).IsEqualTo(200)
      .Because("the two are companions, not alternatives — a window bounds how old rows get and a cap "
        + "bounds how many, and the sync has to carry both together");
  }

  [Test]
  public async Task UnEnrolling_ClearsTheCapTooAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _arrangeAsync(conn);

    await _syncAsync(ttlSeconds: 3600, capPerScope: 10, capScopeKey: "u");

    await using var ctx = CreateDbContext();
    await _coordinator(ctx).SyncPerspectiveRetentionAsync([
      new PerspectiveRetentionDeclaration(CLR_TYPE, Enrolled: false, null, null, null, null)
    ]);

    await using var read = new NpgsqlCommand(
      "SELECT row_cap_per_scope, row_cap_scope_key FROM wh_perspective_registry " +
      "WHERE clr_type_name = @t", conn);
    read.Parameters.AddWithValue("t", CLR_TYPE);
    await using var reader = await read.ExecuteReaderAsync();
    await reader.ReadAsync();

    await Assert.That(reader.IsDBNull(0)).IsTrue();
    await Assert.That(reader.IsDBNull(1)).IsTrue()
      .Because("removing a declaration must clear the cap as well as the windows, or a later "
        + "re-enrolment silently inherits a stale bound nobody declared");
  }

  [Test]
  public async Task RegistryUnion_CoversCapOnlyModelsAsync() {
    // Unit-level guard on the reconciler's registry union — the fourth break. A model registering only
    // a cap must still be enumerable as a declaring perspective.
    PerspectiveRowCapRegistry.Register(typeof(DeclarationToEnforcementModel), 42, "u");

    var capModels = PerspectiveRowCapRegistry.RegisteredModels();

    await Assert.That(capModels.Any(m => m.Key == typeof(DeclarationToEnforcementModel))).IsTrue()
      .Because("the reconciler unions this registry with the TTL one; if cap-only models were not "
        + "enumerable here they would register in memory and never reach the database");
    await Assert.That(PerspectiveRowCapRegistry.Resolve(typeof(DeclarationToEnforcementModel))!.Value.Cap)
      .IsEqualTo(42);
  }
}
