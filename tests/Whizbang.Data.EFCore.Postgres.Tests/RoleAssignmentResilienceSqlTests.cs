using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 184: the role-assignment decisions of #968 in SQL. Per-duty leases, the duty-backend
/// backstop for one long statement, the cooperative drain, the newest-version preference for a
/// vacant role, ending a lapsed bridged holder's lock session, the fleet-wide lapse that skips the
/// cool-down, and the multi-role vote. Database time is advanced by shifting stored instants back;
/// a backend counts as running a statement when the check itself runs on it, so nothing here
/// sleeps, polls or races.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/184_RoleAssignmentResilience.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RoleAssignmentResilienceSqlTests : EFCoreTestBase {
  private const string ROLE = "maintainer";
  private const string OTHER_ROLE = "commit-stamper";
  private static readonly TimeSpan _lease = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan _cooldown = TimeSpan.FromSeconds(15);
  private static readonly int[] _old = [0, 2606, 0, 1];
  private static readonly int[] _new = [0, 2607, 0, 1];
  private static readonly int[] _newest = [0, 2608, 0, 1];

  private sealed record Vote(string Outcome, Guid? Holder, long Epoch, TimeSpan? LeaseRemaining);

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task<Guid> _joinAsync(CancellationToken ct) {
    var instanceId = (Guid)TrackedGuid.New();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, "role-svc", "role-host", 1), ct);
    return instanceId;
  }

  private static async Task<Vote> _voteOnAsync(
      NpgsqlConnection conn, Guid instanceId, CancellationToken ct,
      string role = ROLE, int[]? version = null, TimeSpan? lease = null, long? legacyKey = null, int? bridgePid = null) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome, holder_instance_id, epoch, lease_remaining "
      + "FROM wh_vote_role(@role, @id, @lease, @cooldown, @legacy, @version, @bridge)";
    cmd.Parameters.AddWithValue("role", role);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("lease", lease ?? _lease);
    cmd.Parameters.AddWithValue("cooldown", _cooldown);
    cmd.Parameters.Add(new NpgsqlParameter("legacy", NpgsqlDbType.Bigint) { Value = (object?)legacyKey ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = (object?)version ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("bridge", NpgsqlDbType.Integer) { Value = (object?)bridgePid ?? DBNull.Value });
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    _ = await reader.ReadAsync(ct);
    return new Vote(
      reader.GetString(0),
      await reader.IsDBNullAsync(1, ct) ? null : reader.GetGuid(1),
      reader.GetInt64(2),
      await reader.IsDBNullAsync(3, ct) ? null : reader.GetTimeSpan(3));
  }

  private async Task<Vote> _voteAsync(
      Guid instanceId, CancellationToken ct,
      string role = ROLE, int[]? version = null, TimeSpan? lease = null, long? legacyKey = null, int? bridgePid = null) {
    await using var conn = await _openAsync(ct);
    return await _voteOnAsync(conn, instanceId, ct, role, version, lease, legacyKey, bridgePid);
  }

  private static async Task<object?> _scalarOnAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object? Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is DBNull ? null : result;
  }

  private async Task<object?> _scalarAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] args) {
    await using var conn = await _openAsync(ct);
    return await _scalarOnAsync(conn, sql, ct, args);
  }

  private async Task<string> _renewLeaseAsync(Guid instanceId, long epoch, CancellationToken ct, long? legacyKey = null) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT wh_renew_role_lease(@role, @id, @epoch, @legacy)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("epoch", epoch);
    cmd.Parameters.Add(new NpgsqlParameter("legacy", NpgsqlDbType.Bigint) { Value = (object?)legacyKey ?? DBNull.Value });
    return (string)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<bool> _fencedOnAsync(NpgsqlConnection conn, Guid instanceId, long epoch, CancellationToken ct) {
    try {
      _ = await _scalarOnAsync(conn, "SELECT wh_assert_role_epoch(@role, @id, @epoch)", ct, ("role", ROLE), ("id", instanceId), ("epoch", epoch));
      return true;
    } catch (PostgresException ex) when (ex.SqlState == "WHF01") {
      return false;
    }
  }

  /// <summary>Moves the database clock forward for one role (and its candidates) by moving their stored instants back.</summary>
  private async Task _ageAsync(string role, TimeSpan by, CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    _ = await _scalarOnAsync(conn, """
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
             lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
       WHERE role = @role
      """, ct, ("by", by), ("role", role));
    _ = await _scalarOnAsync(conn, "UPDATE wh_role_candidates SET last_voted_at = last_voted_at - @by WHERE role = @role",
      ct, ("by", by), ("role", role));
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_GrantsEachRoleItsOwnDeclaredLeaseAsync(CancellationToken cancellationToken) {
    // Decision 1: each duty declares its own lease, and renewal extends by the lease it was granted.
    var a = await _joinAsync(cancellationToken);
    var shortLease = await _voteAsync(a, cancellationToken, ROLE, lease: TimeSpan.FromSeconds(15));
    var longLease = await _voteAsync(a, cancellationToken, OTHER_ROLE, lease: TimeSpan.FromMinutes(10));

    await Assert.That(shortLease.LeaseRemaining).IsEqualTo(TimeSpan.FromSeconds(15));
    await Assert.That(longLease.LeaseRemaining).IsEqualTo(TimeSpan.FromMinutes(10));
    await _ageAsync(OTHER_ROLE, TimeSpan.FromMinutes(5), cancellationToken);
    var remaining = (TimeSpan)(await _scalarAsync(
      "SELECT lease_remaining FROM wh_role_assignment_status() WHERE role = @role", cancellationToken, ("role", OTHER_ROLE)))!;
    await Assert.That(remaining).IsGreaterThan(TimeSpan.FromMinutes(4))
      .Because("five minutes into a ten-minute lease the role is still held, where a fifteen-second lease lapsed long ago");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_TreatsAnExpiredHolderAsLive_WhileItsMarkedDutyBackendRunsAStatementAsync(CancellationToken cancellationToken) {
    // Decision 1's backstop: the holder marks the backend running its duty's long statement. While that
    // backend is running a statement, the lease's expiry does not lapse the assignment. Any statement run
    // ON the marked backend is, by definition, that backend running a statement, so the checks below run
    // there to observe "active" without racing a second session.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var grant = await _voteAsync(a, cancellationToken);
    await using var duty = await _openAsync(cancellationToken);
    var marked = (bool)(await _scalarOnAsync(duty, "SELECT wh_mark_role_duty_backend(@role, @id, @epoch, TRUE)", cancellationToken,
      ("role", ROLE), ("id", a), ("epoch", grant.Epoch)))!;
    await Assert.That(marked).IsTrue();

    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);

    var whileActive = await _voteOnAsync(duty, b, cancellationToken);
    await Assert.That(whileActive.Outcome).IsEqualTo("contended")
      .Because("the holder's duty backend is running a statement, so its expired lease does not lapse it");
    await Assert.That(await _fencedOnAsync(duty, a, grant.Epoch, cancellationToken)).IsTrue()
      .Because("the fence treats the holder as live exactly when the vote does");
    await Assert.That((bool)(await _scalarOnAsync(duty,
      "SELECT duty_backend_active FROM wh_role_assignment_status() WHERE role = @role", cancellationToken, ("role", ROLE)))!).IsTrue();

    await using var elsewhere = await _openAsync(cancellationToken);
    await Assert.That(await _fencedOnAsync(elsewhere, a, grant.Epoch, cancellationToken)).IsFalse()
      .Because("with the duty backend idle, the expired lease is all there is");
    var whileIdle = await _voteOnAsync(elsewhere, b, cancellationToken);
    await Assert.That(whileIdle.Outcome).IsEqualTo("granted");
    await Assert.That(whileIdle.Epoch).IsEqualTo(grant.Epoch + 1);
  }

  [Test]
  [Timeout(60000)]
  public async Task MarkDutyBackend_IsRefusedToAStaleHolder_AndClearingItEndsTheBackstopAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var grant = await _voteAsync(a, cancellationToken);
    await using var duty = await _openAsync(cancellationToken);

    var stale = (bool)(await _scalarOnAsync(duty, "SELECT wh_mark_role_duty_backend(@role, @id, @epoch, TRUE)", cancellationToken,
      ("role", ROLE), ("id", a), ("epoch", grant.Epoch + 1)))!;
    await Assert.That(stale).IsFalse().Because("only the current (holder, epoch) may mark a backend");

    _ = await _scalarOnAsync(duty, "SELECT wh_mark_role_duty_backend(@role, @id, @epoch, TRUE)", cancellationToken,
      ("role", ROLE), ("id", a), ("epoch", grant.Epoch));
    var cleared = (bool)(await _scalarOnAsync(duty, "SELECT wh_mark_role_duty_backend(@role, @id, @epoch, FALSE)", cancellationToken,
      ("role", ROLE), ("id", a), ("epoch", grant.Epoch)))!;
    await Assert.That(cleared).IsTrue();
    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);

    var vote = await _voteOnAsync(duty, b, cancellationToken);
    await Assert.That(vote.Outcome).IsEqualTo("granted")
      .Because("a cleared mark counts for nothing, even on the backend it once named");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_ByANewerCaller_AsksTheOlderHolderToDrain_OnceAsync(CancellationToken cancellationToken) {
    // Decision 2: a caller on a newer library version asks a live older holder to finish its current
    // step and release. Asked once; later callers see the role as draining.
    var holder = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var newest = await _joinAsync(cancellationToken);
    var peer = await _joinAsync(cancellationToken);
    _ = await _voteAsync(holder, cancellationToken, version: _old);

    var samePeer = await _voteAsync(peer, cancellationToken, version: _old);
    await Assert.That(samePeer.Outcome).IsEqualTo("contended").Because("an equal version never asks for a drain");
    await Assert.That(await _scalarAsync("SELECT drain_requested_at FROM wh_role_assignment_status() WHERE role = @role",
      cancellationToken, ("role", ROLE))).IsNull();

    var asked = await _voteAsync(newer, cancellationToken, version: _new);
    await Assert.That(asked.Outcome).IsEqualTo("draining");
    await Assert.That(asked.Holder).IsEqualTo(holder);
    var later = await _voteAsync(newest, cancellationToken, version: _newest);
    await Assert.That(later.Outcome).IsEqualTo("draining");
    await Assert.That((Guid)(await _scalarAsync("SELECT drain_requested_by FROM wh_role_assignments WHERE role = @role",
      cancellationToken, ("role", ROLE)))!).IsEqualTo(newer).Because("the first newer caller asked; nobody asks twice");
    await Assert.That(await _scalarAsync("SELECT drain_requested_at FROM wh_role_assignment_status() WHERE role = @role",
      cancellationToken, ("role", ROLE))).IsNotNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task RenewLease_AnswersDrain_AfterANewerCallerAskedAsync(CancellationToken cancellationToken) {
    var holder = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var grant = await _voteAsync(holder, cancellationToken, version: _old);
    await Assert.That(await _renewLeaseAsync(holder, grant.Epoch, cancellationToken)).IsEqualTo("renewed");

    _ = await _voteAsync(newer, cancellationToken, version: _new);

    await Assert.That(await _renewLeaseAsync(holder, grant.Epoch, cancellationToken)).IsEqualTo("drain")
      .Because("the holder learns of the request on its next renewal, which still renews: it finishes its step first");
    await Assert.That(await _renewLeaseAsync(holder, grant.Epoch + 7, cancellationToken)).IsEqualTo("lost");
    _ = await _scalarAsync("SELECT wh_release_role(@role, @id, @epoch)", cancellationToken, ("role", ROLE), ("id", holder), ("epoch", grant.Epoch));
    var handOff = await _voteAsync(newer, cancellationToken, version: _new);
    await Assert.That(handOff.Outcome).IsEqualTo("granted");
    await Assert.That(await _scalarAsync("SELECT drain_requested_at FROM wh_role_assignments WHERE role = @role",
      cancellationToken, ("role", ROLE))).IsNull().Because("a new grant starts with no drain pending");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_ForAVacantRole_DefersToALiveCandidateOnANewerVersionAsync(CancellationToken cancellationToken) {
    // Decision 3: a vacant role prefers the newest library version; among equals the first valid caller.
    var holder = await _joinAsync(cancellationToken);
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var grant = await _voteAsync(holder, cancellationToken, version: _old);
    _ = await _voteAsync(newer, cancellationToken, version: _new);   // a live candidate, asking for a drain
    _ = await _scalarAsync("SELECT wh_release_role(@role, @id, @epoch)", cancellationToken, ("role", ROLE), ("id", holder), ("epoch", grant.Epoch));

    var deferred = await _voteAsync(older, cancellationToken, version: _old);
    await Assert.That(deferred.Outcome).IsEqualTo("deferred")
      .Because("a live candidate on a newer version is voting for this role");
    var unversioned = await _voteAsync(older, cancellationToken);
    await Assert.That(unversioned.Outcome).IsEqualTo("deferred").Because("no version sorts below every version");

    var won = await _voteAsync(newer, cancellationToken, version: _new);
    await Assert.That(won.Outcome).IsEqualTo("granted");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_ForAVacantRole_StopsDeferring_OnceTheNewerCandidateStopsVotingAsync(CancellationToken cancellationToken) {
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var grant = await _voteAsync(newer, cancellationToken, version: _new);
    _ = await _scalarAsync("SELECT wh_release_role(@role, @id, @epoch)", cancellationToken, ("role", ROLE), ("id", newer), ("epoch", grant.Epoch));
    await Assert.That((await _voteAsync(older, cancellationToken, version: _old)).Outcome).IsEqualTo("deferred");

    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);

    var vote = await _voteAsync(older, cancellationToken, version: _old);
    await Assert.That(vote.Outcome).IsEqualTo("granted")
      .Because("a candidate that has not voted within a lease is not live, so it cannot hold the role up");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_ForAVacantRole_StopsDeferring_ToACandidateThatLeftTheRegistryAsync(CancellationToken cancellationToken) {
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    var grant = await _voteAsync(newer, cancellationToken, version: _new);
    _ = await _scalarAsync("SELECT wh_release_role(@role, @id, @epoch)", cancellationToken, ("role", ROLE), ("id", newer), ("epoch", grant.Epoch));
    _ = await _scalarAsync("DELETE FROM wh_service_instances WHERE instance_id = @id", cancellationToken, ("id", newer));

    var vote = await _voteAsync(older, cancellationToken, version: _old);

    await Assert.That(vote.Outcome).IsEqualTo("granted");
    await Assert.That((long)(await _scalarAsync("SELECT count(*) FROM wh_role_candidates WHERE role = @role AND instance_id = @id",
      cancellationToken, ("role", ROLE), ("id", newer)))!).IsEqualTo(0L).Because("a candidate that left is forgotten by the next vote");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_ForAVacantRole_DoesNotDeferToANewerCandidateThatIsCoolingDownAsync(CancellationToken cancellationToken) {
    var older = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    _ = await _voteAsync(newer, cancellationToken, version: _new);
    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);
    // The newer holder lapsed (no other role bears witness, so it is its own lapse) and is cooling down.
    await Assert.That((await _voteAsync(newer, cancellationToken, version: _new)).Outcome).IsEqualTo("cooling_down");

    var vote = await _voteAsync(older, cancellationToken, version: _old);
    await Assert.That(vote.Outcome).IsEqualTo("granted")
      .Because("deferring to a candidate that may not win would leave the role vacant for the whole cool-down");
  }

  [Test]
  [Timeout(60000)]
  public async Task RenewLease_StepsAside_WhenALegacyHolderTookTheLockAfterTheVoteAsync(CancellationToken cancellationToken) {
    const long legacyKey = 0x5EED_0184_0001;
    var a = await _joinAsync(cancellationToken);
    var grant = await _voteAsync(a, cancellationToken, legacyKey: legacyKey);
    await Assert.That(await _renewLeaseAsync(a, grant.Epoch, cancellationToken, legacyKey)).IsEqualTo("renewed");

    await using var legacy = await _openAsync(cancellationToken);
    _ = await _scalarOnAsync(legacy, "SELECT pg_advisory_lock(@k)", cancellationToken, ("k", legacyKey));

    await Assert.That(await _renewLeaseAsync(a, grant.Epoch, cancellationToken, legacyKey)).IsEqualTo("lost")
      .Because("an instance on the session-lock elector took the duty; the assignment steps aside within one renewal");
    await Assert.That((string)(await _scalarAsync("SELECT last_vacated_reason FROM wh_role_assignments WHERE role = @role",
      cancellationToken, ("role", ROLE)))!).IsEqualTo("legacy_holder");
    await using var check = await _openAsync(cancellationToken);
    await Assert.That(await _fencedOnAsync(check, a, grant.Epoch, cancellationToken)).IsFalse();
    await Assert.That(await _renewLeaseAsync(a, grant.Epoch, cancellationToken, legacyKey)).IsEqualTo("lost")
      .Because("stepping aside again changes nothing");
  }

  [Test]
  [Timeout(60000)]
  public async Task EndLapsedBridge_EndsOnlyTheLapsedHoldersLockSession_AndOnlyOnceAsync(CancellationToken cancellationToken) {
    // Decision 4: once the row says the bridged holder lapsed, a would-be winner may end that holder's
    // legacy-lock session, and only that session.
    const long legacyKey = 0x5EED_0184_0002;
    var a = await _joinAsync(cancellationToken);
    var bridge = await _openAsync(cancellationToken);
    await using var bystander = await _openAsync(cancellationToken);
    _ = await _scalarOnAsync(bridge, "SELECT pg_advisory_lock(@k)", cancellationToken, ("k", legacyKey));
    _ = await _scalarOnAsync(bystander, "SELECT pg_advisory_lock(@k)", cancellationToken, ("k", legacyKey + 1));
    _ = await _voteAsync(a, cancellationToken, bridgePid: bridge.ProcessID);

    var whileLive = await _scalarAsync("SELECT wh_end_lapsed_bridge(@role, @k)", cancellationToken, ("role", ROLE), ("k", legacyKey));
    await Assert.That(whileLive).IsNull().Because("a live holder's session is never ended");

    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);
    var unrelatedKey = await _scalarAsync("SELECT wh_end_lapsed_bridge(@role, @k)", cancellationToken, ("role", ROLE), ("k", legacyKey + 1));
    await Assert.That(unrelatedKey).IsNull().Because("only the session holding THIS role's legacy lock may be ended");

    var ended = await _scalarAsync("SELECT wh_end_lapsed_bridge(@role, @k)", cancellationToken, ("role", ROLE), ("k", legacyKey));
    await Assert.That(ended).IsEqualTo(bridge.ProcessID);
    await Assert.That(await _scalarAsync("SELECT wh_end_lapsed_bridge(@role, @k)", cancellationToken, ("role", ROLE), ("k", legacyKey))).IsNull()
      .Because("ended once; a fleet asking together ends it once");
    await Assert.That((bool)(await _scalarOnAsync(bystander, "SELECT pg_try_advisory_lock(@k)", cancellationToken, ("k", legacyKey)))!).IsTrue()
      .Because("the lapsed holder's lock went with its session, and the bystander's session is untouched");
    await Assert.That(async () => await _scalarOnAsync(bridge, "SELECT 1", cancellationToken)).Throws<Exception>();
    await bridge.DisposeAsync();
  }

  [Test]
  [Timeout(60000)]
  public async Task EndLapsedBridge_LeavesASessionThatNoLongerHoldsTheLockAsync(CancellationToken cancellationToken) {
    const long legacyKey = 0x5EED_0184_0003;
    var a = await _joinAsync(cancellationToken);
    await using var bridge = await _openAsync(cancellationToken);
    _ = await _voteAsync(a, cancellationToken, bridgePid: bridge.ProcessID);
    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);

    var ended = await _scalarAsync("SELECT wh_end_lapsed_bridge(@role, @k)", cancellationToken, ("role", ROLE), ("k", legacyKey));

    await Assert.That(ended).IsNull();
    await Assert.That(await _scalarOnAsync(bridge, "SELECT 1", cancellationToken)).IsEqualTo(1);
    await Assert.That(await _scalarAsync("SELECT wh_end_lapsed_bridge(@role, @k)", cancellationToken, ("role", "never-held"), ("k", legacyKey))).IsNull();
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_AfterEveryHolderLapsedTogether_SkipsTheCooldownAsync(CancellationToken cancellationToken) {
    // Decision 5: every holder lapsed together (the database was out), so no cool-down.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var first = await _voteAsync(a, cancellationToken, ROLE);
    var second = await _voteAsync(b, cancellationToken, OTHER_ROLE);
    // Both holders renewing: each was renewed after the other was assigned, as a healthy fleet's are.
    _ = await _voteAsync(a, cancellationToken, ROLE);
    _ = await _voteAsync(b, cancellationToken, OTHER_ROLE);
    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);
    await _ageAsync(OTHER_ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);

    var again = await _voteAsync(a, cancellationToken, ROLE);
    var againOther = await _voteAsync(b, cancellationToken, OTHER_ROLE);

    await Assert.That(again.Outcome).IsEqualTo("granted").Because("the other role bears witness that nobody reached the database");
    await Assert.That(again.Epoch).IsEqualTo(first.Epoch + 1);
    await Assert.That(againOther.Outcome).IsEqualTo("granted")
      .Because("a role re-granted after the outage is no witness to the outage window");
    await Assert.That(againOther.Epoch).IsEqualTo(second.Epoch + 1);
    await Assert.That((bool)(await _scalarAsync("SELECT last_vacated_fleet_wide FROM wh_role_assignments WHERE role = @role",
      cancellationToken, ("role", ROLE)))!).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_AfterOneHolderLapsedWhileAnotherKeptRenewing_KeepsTheCooldownAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    _ = await _voteAsync(a, cancellationToken, ROLE);
    _ = await _voteAsync(b, cancellationToken, OTHER_ROLE);
    // The other role has been held for an hour and is live now, so it was renewed through the window.
    _ = await _scalarAsync("UPDATE wh_role_assignments SET assigned_at = assigned_at - interval '1 hour' WHERE role = @role",
      cancellationToken, ("role", OTHER_ROLE));
    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);

    var again = await _voteAsync(a, cancellationToken, ROLE);

    await Assert.That(again.Outcome).IsEqualTo("cooling_down")
      .Because("another holder kept renewing through the window, so the database was reachable: this lapse was the holder's own");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_AfterALapse_WhileAnotherRoleWasVacatedInTheWindow_KeepsTheCooldownAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    _ = await _voteAsync(a, cancellationToken, ROLE);
    _ = await _voteAsync(b, cancellationToken, OTHER_ROLE);
    await _ageAsync(ROLE, _lease + TimeSpan.FromSeconds(1), cancellationToken);
    // The other role was held from the start and has lapsed too, which alone would read as fleet-wide.
    // An instance vacated it inside this holder's final lease, though, so the database was reachable then.
    _ = await _scalarAsync("""
      UPDATE wh_role_assignments o
         SET assigned_at = o.assigned_at - interval '1 hour',
             lease_expires_at = now() - interval '1 second',
             last_vacated_at = r.renewed_at + interval '1 second',
             last_vacated_reason = 'released'
        FROM wh_role_assignments r
       WHERE o.role = @other AND r.role = @role
      """, cancellationToken, ("other", OTHER_ROLE), ("role", ROLE));

    var again = await _voteAsync(a, cancellationToken, ROLE);

    await Assert.That(again.Outcome).IsEqualTo("cooling_down")
      .Because("a vacancy inside the window means some instance reached the database then");
  }

  [Test]
  [Timeout(60000)]
  public async Task VoteRoles_VotesForEveryRoleInOneStatement_InRoleOrderAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT role, outcome, epoch, lease_remaining FROM wh_vote_roles(@roles, @leases, @id, @cooldown, @legacy, @version)";
    cmd.Parameters.AddWithValue("roles", new[] { OTHER_ROLE, ROLE });
    cmd.Parameters.AddWithValue("leases", new[] { TimeSpan.FromMinutes(1), _lease });
    cmd.Parameters.AddWithValue("id", a);
    cmd.Parameters.AddWithValue("cooldown", _cooldown);
    cmd.Parameters.Add(new NpgsqlParameter("legacy", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = new long?[] { null, null } });
    cmd.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = _new });
    var rows = new List<(string Role, string Outcome, long Epoch, TimeSpan Lease)>();
    await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken)) {
      while (await reader.ReadAsync(cancellationToken)) {
        rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetTimeSpan(3)));
      }
    }

    await Assert.That(rows.Select(r => r.Role)).IsEquivalentTo(new[] { OTHER_ROLE, ROLE }.Order(StringComparer.Ordinal));
    await Assert.That(rows.Select(r => r.Role).First()).IsEqualTo(OTHER_ROLE).Because("votes run in role order");
    await Assert.That(rows.All(r => r.Outcome == "granted" && r.Epoch == 1L)).IsTrue();
    await Assert.That(rows.Single(r => r.Role == OTHER_ROLE).Lease).IsEqualTo(TimeSpan.FromMinutes(1));

    await using var bad = conn.CreateCommand();
    bad.CommandText = "SELECT * FROM wh_vote_roles(ARRAY['a','b'], ARRAY[interval '1 minute'], @id, @cooldown, ARRAY[NULL::bigint, NULL], NULL)";
    bad.Parameters.AddWithValue("id", a);
    bad.Parameters.AddWithValue("cooldown", _cooldown);
    var error = await Assert.That(async () => await bad.ExecuteNonQueryAsync(cancellationToken)).Throws<PostgresException>();
    await Assert.That(error!.SqlState).IsEqualTo("22023");
  }

  [Test]
  [Timeout(60000)]
  public async Task PhaseOneEntryPoints_StillWork_AndReadNewOutcomesAsContendedAsync(CancellationToken cancellationToken) {
    // An instance on the release before this one keeps calling wh_elect_role and wh_renew_role during a
    // rolling deploy. It counts as the oldest version and cannot drain.
    var old = await _joinAsync(cancellationToken);
    var oldPeer = await _joinAsync(cancellationToken);
    var newer = await _joinAsync(cancellationToken);
    await using var conn = await _openAsync(cancellationToken);

    var elected = await _electPhaseOneAsync(conn, old, cancellationToken);
    await Assert.That(elected.Outcome).IsEqualTo("granted");
    await Assert.That((await _voteAsync(newer, cancellationToken, version: _new)).Outcome).IsEqualTo("draining");
    await Assert.That((await _electPhaseOneAsync(conn, oldPeer, cancellationToken)).Outcome).IsEqualTo("contended")
      .Because("'draining' means 'wait' to a caller that can only wait");
    await Assert.That((bool)(await _scalarOnAsync(conn, "SELECT wh_renew_role(@role, @id, @epoch)", cancellationToken,
      ("role", ROLE), ("id", old), ("epoch", elected.Epoch)))!).IsTrue()
      .Because("a drain request renews: the phase 1 holder cannot drain, so it keeps the role until it stops");

    _ = await _scalarOnAsync(conn, "SELECT wh_release_role(@role, @id, @epoch)", cancellationToken, ("role", ROLE), ("id", old), ("epoch", elected.Epoch));
    await Assert.That((await _electPhaseOneAsync(conn, oldPeer, cancellationToken)).Outcome).IsEqualTo("contended")
      .Because("'deferred' reads as contended too: the newer candidate is preferred");
  }

  private static async Task<Vote> _electPhaseOneAsync(NpgsqlConnection conn, Guid instanceId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome, holder_instance_id, epoch, lease_remaining FROM wh_elect_role(@role, @id, @lease, @cooldown)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("id", instanceId);
    cmd.Parameters.AddWithValue("lease", _lease);
    cmd.Parameters.AddWithValue("cooldown", _cooldown);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    _ = await reader.ReadAsync(ct);
    return new Vote(reader.GetString(0), await reader.IsDBNullAsync(1, ct) ? null : reader.GetGuid(1), reader.GetInt64(2),
      await reader.IsDBNullAsync(3, ct) ? null : reader.GetTimeSpan(3));
  }
}
