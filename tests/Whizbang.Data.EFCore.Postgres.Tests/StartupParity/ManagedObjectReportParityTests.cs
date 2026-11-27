// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests.StartupParity;

/// <summary>
/// #2608 on both drivers alike: every start reports what its instance declares, and the managed-object reconcile
/// (create, alter and drop) runs once per schema per build. A second start of the same build reports and does not
/// reconcile; a drop the fleet gate held back lands once every live instance has reported, through the periodic re-run;
/// and an older release that still reconciles at every start runs beside this one without either dropping what the
/// other declares.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#rolling-deploys</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaReconciler.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaFleet.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StartupWorkSessions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresSchemaInitializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/ManagedSchemaReconcile.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
public partial class StartupParityTests {
  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task ASecondStartOfTheBuild_ReportsWithoutReconciling_AndTheDropLandsOnceEveryLiveInstanceHasReportedAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    var first = await _settledStartAsync(driver);
    var firstId = _instanceId(first);
    await _execAsync($"CREATE INDEX {driver.Retired} ON {driver.Table} USING gin (data)");
    await _reconcileStepInANewWindowAsync(first, firstId);
    await Assert.That(await _ledgerStatusAsync(driver.Table, driver.Retired)).IsEqualTo(ManagedStatuses.PENDING_RETIREMENT)
      .Because("the re-run records the index it first sees, and drops nothing on that sighting");

    // The second instance of this build is running and has not reported yet, as a starting instance is before its
    // Converge stage: the gate cannot know what it relies on, so the re-run keeps everything.
    var second = _compose(driver);
    var secondId = _instanceId(second);
    await _registerLiveAsync(secondId);
    var claim = await _reconcileClaimAsync();
    await Assert.That(claim?.State).IsEqualTo("done");
    await _reconcileStepInANewWindowAsync(first, firstId, secondId);
    await Assert.That(await _existsAsync(driver.Retired)).IsTrue()
      .Because("a live instance that has not reported holds every drop back");

    await _initializer(second).StartAsync(cancellationToken);

    await Assert.That(await _existsAsync(driver.Retired)).IsTrue()
      .Because("the second start of the build reports without reconciling; a reconcile would have dropped it, since no "
        + "live instance declares it");
    await Assert.That(await _declaredByAsync(secondId)).IsNotNull()
      .Because("the second start reported what it declares");
    await Assert.That(await _declaredByAsync(secondId)).DoesNotContain($"{driver.Table}:{driver.Retired}");
    await Assert.That(await _reconcileClaimAsync()).IsEqualTo(claim)
      .Because("the reconcile ran once for this build, on its first start, and this start left the record as it was");

    await _reconcileStepInANewWindowAsync(first, firstId, secondId);

    await Assert.That(await _existsAsync(driver.Retired)).IsFalse()
      .Because("every live instance has reported and none declares it, so the drop lands");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task AnOlderReleaseThatReconcilesAtEveryStart_AndThisReleaseThatReportsOnly_DropNothingTheOtherDeclaresAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    var first = await _settledStartAsync(driver);
    var firstId = _instanceId(first);
    var ours = await _activeWhizbangObjectsAsync();
    await Assert.That(ours).IsNotEmpty().Because("a check over no objects proves nothing");

    // The older release declares an index this release retired, and none of this release's objects.
    await _execAsync($"CREATE INDEX {driver.Retired} ON {driver.Table} USING gin (data)");
    var older = Guid.CreateVersion7();
    await _registerLiveAsync(older);
    await _olderReleaseStartsAsync(driver, older, firstId);
    await _olderReleaseStartsAsync(driver, older, firstId);
    await Assert.That(await _missingAsync(ours)).IsEmpty()
      .Because("this release's first instance reported, so the older release keeps what it declares");

    var second = _compose(driver);
    var secondId = _instanceId(second);
    await _initializer(second).StartAsync(cancellationToken);
    await Assert.That(await _existsAsync(driver.Retired)).IsTrue().Because("the older release still runs and declares it");
    await Assert.That(await _declaredByAsync(secondId)).IsNotNull().Because("the start that did not reconcile still reported");

    await _olderReleaseStartsAsync(driver, older, firstId, secondId);
    await Assert.That(await _missingAsync(ours)).IsEmpty()
      .Because("the older release reads the report of the instance that only reported, and keeps what it declares");

    // The older release stops; the re-run on the instance that only reported drops what nothing live declares.
    await _execAsync($"UPDATE wh_service_instances SET last_heartbeat_at = now() - interval '1 hour' WHERE instance_id = '{older}'");
    await _reconcileStepInANewWindowAsync(second, firstId, secondId);

    await Assert.That(await _existsAsync(driver.Retired)).IsFalse()
      .Because("once the older release is gone, every live instance has reported and none declares it");
    await Assert.That(await _missingAsync(ours)).IsEmpty();
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task AReconcileThatCannotTakeTheSchemaLock_ReleasesItsClaim_SoTheNextStartRunsItAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    _ = await _settledStartAsync(driver);
    await _asIfTheNextBuildStartsAsync();

    await using (var holder = new NpgsqlConnection(_database.ConnectionString)) {
      await holder.OpenAsync(cancellationToken);
      await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock($1)", holder)) {
        take.Parameters.AddWithValue(SchemaInitializationLockKey.Compute("public"));
        await take.ExecuteNonQueryAsync(cancellationToken);
      }
      var blocked = _compose(driver);
      await _initializer(blocked).StartAsync(cancellationToken);

      await Assert.That(await _reconcileClaimAsync()).IsNull()
        .Because("the reconcile found the schema lock held and did nothing, so it is not recorded done for the build");
      await Assert.That(await _declaredByAsync(_instanceId(blocked))).IsNotNull()
        .Because("the report takes no lock, so it is made whoever holds it");
    }

    var next = _compose(driver);
    await _initializer(next).StartAsync(cancellationToken);

    await Assert.That(await _reconcileClaimAsync()).IsEqualTo(("done", _instanceId(next)))
      .Because("the next start claimed the released reconcile and ran it");
  }

  [Test]
  [Arguments("efcore")]
  [Arguments("dapper")]
  [Timeout(300000)]
  public async Task WithTheReconcileOff_AStartNeitherReportsNorClaimsTheReconcileAsync(
      string driverName, CancellationToken cancellationToken) {
    var driver = StartupDriver.For(driverName);
    _ = await _settledStartAsync(driver);
    await _asIfTheNextBuildStartsAsync();

    var off = await _startAsync(driver, ("Whizbang:Schema:Reconcile:Mode", "Off"));

    await Assert.That(await _declaredByAsync(_instanceId(off))).IsNull().Because("Off reads and writes nothing");
    await Assert.That(await _reconcileFingerprintAsync()).IsEqualTo("an-earlier-build")
      .Because("Off claims nothing, so it takes no heavy-start permit for it");
  }

  /// <summary>
  /// A start on a schema an earlier start already settled: the first start migrates and the second settles what the
  /// first converted, so the instance returned starts on a current schema, as every later start of the build does.
  /// </summary>
  private async Task<ServiceProvider> _settledStartAsync(StartupDriver driver) {
    _ = await _startAsync(driver);
    return await _startAsync(driver);
  }

  /// <summary>
  /// The record the next build finds: the reconcile last claimed under another fingerprint, so a start claims it again.
  /// </summary>
  private Task _asIfTheNextBuildStartsAsync() => _execAsync(
    $"UPDATE wh_startup_work SET fingerprint = 'an-earlier-build' WHERE work_name = '{FrameworkStartupWork.MANAGED_OBJECT_RECONCILE}'");

  /// <summary>Registers <paramref name="instance"/> as a live instance that has not reported what it declares.</summary>
  private Task _registerLiveAsync(Guid instance) => _execAsync($"""
    SELECT register_instance_heartbeat('{instance}', 'parity', 'parity-host', 1, NULL::jsonb, now(), now() + interval '60 seconds');
    """);

  /// <summary>
  /// The periodic re-run, on <paramref name="instance"/>, in a window no run has claimed yet, with each of
  /// <paramref name="live"/> beating first so the liveness rule calls it live however long the run took to get here.
  /// </summary>
  private async Task _reconcileStepInANewWindowAsync(IServiceProvider instance, params Guid[] live) {
    await _execAsync("DELETE FROM wh_unique_emission_claims WHERE claim_key LIKE 'whizbang:managed-schema-objects:%'");
    await _beatAsync(live);
    await _runReconcileStepAsync(instance);
  }

  private async Task _beatAsync(Guid[] live) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var beat = new NpgsqlCommand(
      "UPDATE wh_service_instances SET last_heartbeat_at = now() WHERE instance_id = ANY($1)", db);
    beat.Parameters.AddWithValue(live);
    await beat.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// What a release before #2608 runs at every start: the whole reconcile, with its own declarations, reporting them
  /// as it plans. Each of <paramref name="live"/> beats first.
  /// </summary>
  private async Task _olderReleaseStartsAsync(StartupDriver driver, Guid older, params Guid[] live) {
    await _beatAsync([older, .. live]);
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    _ = await ManagedSchemaReconciler.RunAsync(
      db, "public", new ManagedSchemaObjectSet().Index(driver.Table, driver.Retired, "OlderReleaseModel [Indexed]"),
      new ManagedSchemaSettings(ReconcileMode.Apply, [], [], DropAfterFleetConverged: true), older, null,
      CancellationToken.None);
  }

  private async Task<string[]?> _declaredByAsync(Guid instance) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT objects FROM wh_managed_object_declarations WHERE instance_id = $1", db);
    command.Parameters.AddWithValue(instance);
    return await command.ExecuteScalarAsync() as string[];
  }

  private async Task<(string State, Guid Instance)?> _reconcileClaimAsync() {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT state, instance_id FROM wh_startup_work WHERE work_name = $1", db);
    command.Parameters.AddWithValue(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE);
    await using var reader = await command.ExecuteReaderAsync();
    return await reader.ReadAsync() ? (reader.GetString(0), reader.GetGuid(1)) : null;
  }

  private Task<string?> _reconcileFingerprintAsync() => _scalarAsync<string?>(
    "SELECT fingerprint FROM wh_startup_work WHERE work_name = $1", FrameworkStartupWork.MANAGED_OBJECT_RECONCILE);

  private Task<string?> _ledgerStatusAsync(string table, string name) => _scalarAsync<string?>(
    "SELECT status FROM wh_managed_objects WHERE table_name = $1 AND object_name = $2", table, name);

  /// <summary>Every object this release's start recorded as its own and declared.</summary>
  private async Task<List<string>> _activeWhizbangObjectsAsync() {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      $"SELECT object_name FROM wh_managed_objects WHERE owner = '{ManagedOwners.WHIZBANG}' AND status = '{ManagedStatuses.ACTIVE}'", db);
    var names = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }

  /// <summary>The objects of <paramref name="names"/> the catalog no longer has, as an index, a constraint or a trigger.</summary>
  private async Task<List<string>> _missingAsync(List<string> names) {
    await using var db = new NpgsqlConnection(_database.ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand("""
      SELECT n FROM unnest($1::text[]) n
      WHERE NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.relname = n)
        AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conname = n)
        AND NOT EXISTS (SELECT 1 FROM pg_trigger g WHERE g.tgname = n)
        AND NOT EXISTS (SELECT 1 FROM pg_statistic_ext s WHERE s.stxname = n)
      """, db);
    command.Parameters.AddWithValue(names.ToArray());
    var missing = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      missing.Add(reader.GetString(0));
    }
    return missing;
  }
}
