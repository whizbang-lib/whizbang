// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// What a waiter watches while another instance migrates (#966 phase 4): the migrator's assignment
/// row, held live while its lease runs or its marked backend works, and, for a migrator on an older
/// release that holds the duty by its own session lock, the holding it records, judged by the one
/// liveness rule (#2044). No lock is read. Over a real, migrated database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorWatch.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class MigratorWatchTests : EFCoreTestBase {
  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task<Guid> _joinAsync(CancellationToken ct) {
    var instanceId = (Guid)TrackedGuid.New();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "watch-svc", "watch-host", 1), ct);
    return instanceId;
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return await cmd.ExecuteScalarAsync(ct);
  }

  [Test]
  [Timeout(60000)]
  public async Task NothingHeld_IsNotMigratingAsync(CancellationToken cancellationToken) {
    await using var waiter = await _openAsync(cancellationToken);

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "public", cancellationToken)).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task AMigratorOnAnOlderRelease_IsSeenByItsRecordedHolding_WhileTheRuleCallsItLiveAsync(CancellationToken cancellationToken) {
    var older = await _joinAsync(cancellationToken);
    await using var waiter = await _openAsync(cancellationToken);
    _ = await _scalarAsync(waiter, "SELECT record_capability(@id, @role)", cancellationToken,
      ("id", older), ("role", StartupDuties.MIGRATOR));

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "\"public\"", cancellationToken)).IsTrue()
      .Because("either spelling of the schema names the same holding");

    _ = await _scalarAsync(waiter, "UPDATE wh_service_instances SET last_heartbeat_at = now() - INTERVAL '1 hour' WHERE instance_id = @id",
      cancellationToken, ("id", older));
    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "public", cancellationToken)).IsFalse()
      .Because("the one liveness rule calls the older migrator dead, so its holding no longer keeps anyone waiting");
  }

  [Test]
  [Timeout(60000)]
  public async Task AMigratorHoldingTheRole_IsMigrating_UntilItReleasesItAsync(CancellationToken cancellationToken) {
    var migrator = await _joinAsync(cancellationToken);
    await using var waiter = await _openAsync(cancellationToken);
    await using var cmd = waiter.CreateCommand();
    cmd.CommandText = "SELECT epoch FROM wh_vote_role(@role, @id, @lease, @cooldown, NULL, NULL, NULL)";
    cmd.Parameters.AddWithValue("role", StartupDuties.MIGRATOR);
    cmd.Parameters.AddWithValue("id", migrator);
    cmd.Parameters.AddWithValue("lease", TimeSpan.FromSeconds(30));
    cmd.Parameters.AddWithValue("cooldown", TimeSpan.FromSeconds(15));
    var epoch = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "public", cancellationToken)).IsTrue();

    _ = await _scalarAsync(waiter, "SELECT wh_release_role(@role, @id, @epoch)", cancellationToken,
      ("role", StartupDuties.MIGRATOR), ("id", migrator), ("epoch", epoch));
    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "public", cancellationToken)).IsFalse()
      .Because("a released role means the migrator finished; the caller reads the schema to see how");
  }

  /// <summary>
  /// A waiter whose open transaction began before the migrator's role function was committed still sees the migrator's
  /// assignment, and stops seeing it once the function is dropped (#2038).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The watch asks <c>to_regprocedure</c>, a name lookup answered from the session's catalog cache. Inside a
  /// transaction that cache keeps a "no such function" cached before another session committed it, until a statement
  /// opens a relation. The watch's first statement on every call is the session-lock probe, which reads
  /// <c>pg_locks</c> and <c>pg_database</c>, so by the time it looks the function up the cache is current. Measured on
  /// PostgreSQL 17: this test passes on the lookup as written, and fails if the lookup runs before the probe.
  /// </para>
  /// <para>
  /// It locks that ordering. The role table exists before the transaction on purpose: a lookup whose argument type does
  /// not resolve refreshes the cache by itself, and would hide a regression. Documented at
  /// contributors/data-engines/writing-migrations#catalog-existence-checks.
  /// </para>
  /// </remarks>
  [Test]
  [Timeout(60000)]
  public async Task InsideATransactionThatBeganBeforeTheRoleObjectsWereCommitted_SeesTheMigratorAsync(
      CancellationToken cancellationToken) {
    var schema = "watch_" + ((Guid)TrackedGuid.New()).ToString("N");
    var quoted = "\"" + schema + "\"";
    await using var other = await _openAsync(cancellationToken);
    // The table exists first: a lookup that fails on a missing argument type refreshes the cache and hides the defect.
    _ = await _scalarAsync(other,
      $"CREATE SCHEMA {quoted}; CREATE TABLE {quoted}.wh_role_assignments (role text NOT NULL, holder_instance_id uuid)",
      cancellationToken);
    await using var waiter = await _openAsync(cancellationToken);
    await using var transaction = await waiter.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, schema, cancellationToken)).IsFalse()
      .Because("precondition: the role function does not exist yet, and the waiter's session has now looked for them");

    _ = await _scalarAsync(other, $"""
      CREATE FUNCTION {quoted}._role_lapse_reason(r {quoted}.wh_role_assignments) RETURNS text
        LANGUAGE sql AS 'SELECT NULL::text';
      INSERT INTO {quoted}.wh_role_assignments (role, holder_instance_id) VALUES (@role, @holder);
      """, cancellationToken, ("role", StartupDuties.MIGRATOR), ("holder", (Guid)TrackedGuid.New()));

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, schema, cancellationToken)).IsTrue()
      .Because("the migrator's live assignment is committed, whatever the waiter's session cached before");

    _ = await _scalarAsync(other, $"DROP FUNCTION {quoted}._role_lapse_reason({quoted}.wh_role_assignments)", cancellationToken);

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, schema, cancellationToken)).IsFalse()
      .Because("without the role function the watch answers from the session lock alone, and nothing holds it");
  }

  [Test]
  [Timeout(60000)]
  public async Task ASchemaWithoutTheRoleFunctions_IsNotMigratingAsync(CancellationToken cancellationToken) {
    await using var waiter = await _openAsync(cancellationToken);

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "schema_never_bootstrapped", cancellationToken)).IsFalse();
    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "", cancellationToken)).IsFalse()
      .Because("an unset schema is public, and nothing is held there");
    await Assert.That(async () => await MigratorWatch.IsMigratingElsewhereAsync(null!, "public", cancellationToken))
      .Throws<ArgumentNullException>();
  }
}
