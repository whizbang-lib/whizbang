// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 630 (#2596): the database's side of heavy startup work. Per-schema-per-build work is claimed by the first
/// instance of a build, skipped by the rest while a live claimer runs it and after it finished, released for the next
/// start when it did not finish, and taken over from a claimer that is no longer live. A different build claims it
/// again, so a rollback re-runs what the newer build changed. At most the cap's number of live instances hold a
/// heavy-start permit at once, and a permit held by an instance that is no longer live is free.
/// </summary>
/// <remarks>Liveness is the one rule (316): a dead instance is seeded with a heartbeat an hour old, never by waiting.</remarks>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/630_HeavyStartupWorkIsCappedAndSharedWorkRunsOncePerBuild.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PgStartupWorkLedger.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StartupWorkLedgerSqlTests : EFCoreTestBase {
  private const string WORK = FrameworkStartupWork.PERSPECTIVE_REGISTRY;

  [Test]
  [Timeout(60000)]
  public async Task OnAMigratedSchema_TheLedgerIsAvailable_AndOnOneWithoutTheFunctionsItIsNotAsync(CancellationToken cancellationToken) {
    var ledger = _ledger();
    await Assert.That(await ledger.IsAvailableAsync(cancellationToken)).IsTrue();

    // A schema an older release migrated has no such function: the session then runs every unit as before.
    await using var conn = await _openAsync(cancellationToken);
    await _execAsync(conn, "SELECT drop_all_overloads('wh_try_acquire_startup_permit')", cancellationToken);

    await Assert.That(await ledger.IsAvailableAsync(cancellationToken)).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task TheFirstStartOfABuild_ClaimsSharedWork_ThePeersSkipIt_AndOnceDoneEveryLaterStartSkipsItAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, live: true, cancellationToken);
    var b = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();

    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", a, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Claimed);
    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", b, false, cancellationToken)).IsEqualTo(StartupWorkClaim.RunningElsewhere);

    await ledger.CompleteAsync(WORK, "build-1", a, succeeded: true, cancellationToken);

    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", b, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Done);
    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", a, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Done);
    await Assert.That(await _scalarAsync<string>(conn, "SELECT state FROM wh_startup_work WHERE work_name = @w", cancellationToken, ("w", WORK)))
      .IsEqualTo("done");
  }

  [Test]
  [Timeout(60000)]
  public async Task AnotherBuild_ClaimsTheWorkAgain_SoARollbackRerunsWhatTheNewerBuildChangedAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, live: true, cancellationToken);
    var b = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();
    _ = await ledger.ClaimAsync(WORK, "build-1", a, false, cancellationToken);
    await ledger.CompleteAsync(WORK, "build-1", a, true, cancellationToken);

    await Assert.That(await ledger.ClaimAsync(WORK, "build-2", b, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Claimed);
    await ledger.CompleteAsync(WORK, "build-2", b, true, cancellationToken);

    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", a, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Claimed)
      .Because("the record says who changed the shared state last; build 1 did not, so it runs again");
  }

  [Test]
  [Timeout(60000)]
  public async Task ARunThatDidNotFinish_ReleasesItsClaim_AndTheNextStartClaimsItAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, live: true, cancellationToken);
    var b = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();
    _ = await ledger.ClaimAsync(WORK, "build-1", a, false, cancellationToken);

    await ledger.CompleteAsync(WORK, "build-1", a, succeeded: false, cancellationToken);

    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", b, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Claimed);
  }

  [Test]
  [Timeout(60000)]
  public async Task AClaimHeldByAnInstanceNoLongerLive_IsTakenOver_AndItsLateCompletionChangesNothingAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var dead = await _registerAsync(conn, live: false, cancellationToken);
    var b = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();
    _ = await ledger.ClaimAsync(WORK, "build-1", dead, false, cancellationToken);

    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", b, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Claimed);

    // The old claimer finishing after all records nothing: the claim is b's now.
    await ledger.CompleteAsync(WORK, "build-1", dead, succeeded: true, cancellationToken);
    await Assert.That(await _scalarAsync<string>(conn, "SELECT state FROM wh_startup_work WHERE work_name = @w", cancellationToken, ("w", WORK)))
      .IsEqualTo("running");
    await ledger.CompleteAsync(WORK, "build-1", dead, succeeded: false, cancellationToken);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_startup_work", cancellationToken)).IsEqualTo(1L)
      .Because("a release by an instance that no longer holds the claim does not release it");
  }

  [Test]
  [Timeout(60000)]
  public async Task AStartWhosePassAppliedSomething_ClaimsDoneWork_AndARetryOfItsOwnClaimSucceedsAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();
    _ = await ledger.ClaimAsync(WORK, "build-1", a, false, cancellationToken);
    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", a, false, cancellationToken)).IsEqualTo(StartupWorkClaim.Claimed)
      .Because("its own running claim is no peer's");
    await ledger.CompleteAsync(WORK, "build-1", a, true, cancellationToken);

    await Assert.That(await ledger.ClaimAsync(WORK, "build-1", a, true, cancellationToken)).IsEqualTo(StartupWorkClaim.Claimed);
  }

  [Test]
  [Timeout(60000)]
  public async Task TenStartsOfOneBuildAtOnce_ClaimTheWorkExactlyOnceAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var instances = new List<Guid>();
    for (var i = 0; i < 10; i++) {
      instances.Add(await _registerAsync(conn, live: true, cancellationToken));
    }
    var ledger = _ledger();

    var answers = await Task.WhenAll(instances.Select(id => ledger.ClaimAsync(WORK, "build-1", id, false, cancellationToken)));

    await Assert.That(answers.Count(a => a == StartupWorkClaim.Claimed)).IsEqualTo(1);
    await Assert.That(answers.Count(a => a == StartupWorkClaim.RunningElsewhere)).IsEqualTo(9);
  }

  [Test]
  [Timeout(60000)]
  public async Task NoMoreThanTheCap_HoldAPermit_AndAPermitGivenBackIsFreeAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, live: true, cancellationToken);
    var b = await _registerAsync(conn, live: true, cancellationToken);
    var c = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();

    await Assert.That(await ledger.TryAcquirePermitAsync(a, 2, cancellationToken)).IsEqualTo(new StartupPermitAnswer(true, 1));
    await Assert.That(await ledger.TryAcquirePermitAsync(b, 2, cancellationToken)).IsEqualTo(new StartupPermitAnswer(true, 2));
    await Assert.That(await ledger.TryAcquirePermitAsync(c, 2, cancellationToken)).IsEqualTo(new StartupPermitAnswer(false, 2));
    await Assert.That(await ledger.TryAcquirePermitAsync(a, 2, cancellationToken)).IsEqualTo(new StartupPermitAnswer(true, 2))
      .Because("asking again for a permit already held counts it once");

    await ledger.ReleasePermitAsync(a, cancellationToken);

    await Assert.That(await ledger.TryAcquirePermitAsync(c, 2, cancellationToken)).IsEqualTo(new StartupPermitAnswer(true, 2));
  }

  [Test]
  [Timeout(60000)]
  public async Task APermitHeldByAnInstanceNoLongerLive_IsFreeAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var dead = await _registerAsync(conn, live: false, cancellationToken);
    var c = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();
    _ = await ledger.TryAcquirePermitAsync(dead, 1, cancellationToken);

    await Assert.That(await ledger.TryAcquirePermitAsync(c, 1, cancellationToken)).IsEqualTo(new StartupPermitAnswer(true, 1));
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_startup_permits WHERE instance_id = @id", cancellationToken, ("id", dead)))
      .IsEqualTo(0L).Because("the dead holder's permit is removed, not only ignored");
  }

  [Test]
  [Timeout(60000)]
  public async Task TenStartsAskingAtOnce_AreGrantedNoMoreThanTheCapAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var instances = new List<Guid>();
    for (var i = 0; i < 10; i++) {
      instances.Add(await _registerAsync(conn, live: true, cancellationToken));
    }
    var ledger = _ledger();

    var answers = await Task.WhenAll(instances.Select(id => ledger.TryAcquirePermitAsync(id, 3, cancellationToken)));

    await Assert.That(answers.Count(a => a.Granted)).IsEqualTo(3);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_startup_permits", cancellationToken)).IsEqualTo(3L);
  }

  [Test]
  [Timeout(60000)]
  public async Task ACapBelowOne_IsReadAsOneAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, live: true, cancellationToken);
    var b = await _registerAsync(conn, live: true, cancellationToken);
    var ledger = _ledger();

    await Assert.That((await ledger.TryAcquirePermitAsync(a, 0, cancellationToken)).Granted).IsTrue();
    await Assert.That((await ledger.TryAcquirePermitAsync(b, 0, cancellationToken)).Granted).IsFalse();
  }

  private PgStartupWorkLedger _ledger() => new(() => new NpgsqlConnection(ConnectionString), "public");

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task<int> _execAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return (T)(await cmd.ExecuteScalarAsync(ct))!;
  }

  /// <summary>A pooled registration, live by its fresh heartbeat or dead by one an hour old (on the database clock).</summary>
  private static async Task<Guid> _registerAsync(NpgsqlConnection conn, bool live, CancellationToken ct) {
    var id = (Guid)TrackedGuid.New();
    _ = await _execAsync(conn, """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode,
         liveness_window_seconds, startup_stage, lifecycle_phase, metadata, library_version)
      VALUES (@id, 'work-svc', 'work-host', 1, CASE WHEN @live THEN NOW() ELSE NOW() - INTERVAL '1 hour' END,
              NOW() - INTERVAL '2 hours', 'pooled', 30, 40, 'Migrating', '{}'::jsonb, '1.2.3')
      """, ct, ("id", id), ("live", live));
    return id;
  }
}
