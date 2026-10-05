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
/// release, the duty's session lock. Over a real, migrated database.
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
  public async Task AMigratorOnAnOlderRelease_HoldingTheDutysSessionLock_IsMigratingAsync(CancellationToken cancellationToken) {
    await using var waiter = await _openAsync(cancellationToken);
    await using var older = await _openAsync(cancellationToken);
    _ = await _scalarAsync(older, "SELECT pg_advisory_lock(@k)", cancellationToken,
      ("k", DutyLockKey.Compute("public", StartupDuties.MIGRATOR)));

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "\"public\"", cancellationToken)).IsTrue()
      .Because("either spelling of the schema names the same lock");
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

  [Test]
  [Timeout(60000)]
  public async Task ASchemaWithoutTheRoleFunctions_AnswersFromTheSessionLockAloneAsync(CancellationToken cancellationToken) {
    await using var waiter = await _openAsync(cancellationToken);

    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "schema_never_bootstrapped", cancellationToken)).IsFalse();
    await Assert.That(await MigratorWatch.IsMigratingElsewhereAsync(waiter, "", cancellationToken)).IsFalse()
      .Because("an unset schema is public, and nothing is held there");
    await Assert.That(async () => await MigratorWatch.IsMigratingElsewhereAsync(null!, "public", cancellationToken))
      .Throws<ArgumentNullException>();
  }
}
