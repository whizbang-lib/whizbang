// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 615 (#2593, phase 1 of #1328): a role lapses only when another live instance can take it. A live sole holder
/// past its lease and its progress window keeps its role and its epoch, and the fence keeps accepting its writes; the
/// moment a live peer votes, that peer takes the role over at the next epoch, as before. A holder the one liveness rule
/// calls dead still lapses at its lease, a tombstoned holder is still evicted, and the cool-down is answered only while a
/// peer that could take the role exists.
/// </summary>
/// <remarks>
/// Database time is advanced by shifting stored instants back. Nothing sleeps, polls or races.
/// </remarks>
/// <docs>operations/startup/capabilities-and-duties#a-lapse-needs-a-successor</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/615_ALapseNeedsASuccessor.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class RoleLapseNeedsASuccessorSqlTests : EFCoreTestBase {
  private const string ROLE = "commit-stamper";
  private static readonly TimeSpan _lease = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan _cooldown = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan _window = TimeSpan.FromMinutes(2);
  private static readonly TimeSpan _pastTheWindow = _window + TimeSpan.FromSeconds(1);

  // --- no successor, no lapse -------------------------------------------------------------------

  [Test]
  [Timeout(60000)]
  public async Task Lapse_ALiveSoleHolderPastItsLeaseAndWindow_KeepsTheRoleAtItsEpochAsync(CancellationToken cancellationToken) {
    // #1327: one instance draining a backlog, its loop stalled past its window, and nobody else to take the role.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    await Assert.That(await _declareAsync(conn, a, epoch, cancellationToken)).IsTrue();
    await _ageRoleAsync(conn, _pastTheWindow, cancellationToken);

    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsTrue()
      .Because("with no other live candidate the lapse verdict is not given, so the holder keeps its assignment");
    await Assert.That(await _fencedAsync(conn, a, epoch, cancellationToken)).IsTrue()
      .Because("the fence keeps accepting the sole holder's writes: there is no other writer");
    var (outcome, votedEpoch) = await _voteWithEpochAsync(conn, a, cancellationToken);
    await Assert.That(outcome).IsEqualTo("held");
    await Assert.That(votedEpoch).IsEqualTo(epoch).Because("the role is reaffirmed, not lost and re-won at a new epoch");
  }

  [Test]
  [Timeout(60000)]
  public async Task Lapse_APeerThatVotes_TakesOverAStuckHolder_AtTheNextEpochAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, cancellationToken);
    await _ageRoleAsync(conn, _pastTheWindow, cancellationToken);
    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsTrue()
      .Because("b is registered but has not voted for the role, so it is not yet a candidate");

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome, epoch, previous_holder_instance_id, void_reason FROM wh_vote_role(@role, @id, @lease, @cooldown, NULL, NULL, NULL)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("id", b);
    cmd.Parameters.AddWithValue("lease", _lease);
    cmd.Parameters.AddWithValue("cooldown", _cooldown);
    await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken)) {
      _ = await reader.ReadAsync(cancellationToken);
      await Assert.That(reader.GetString(0)).IsEqualTo("granted")
        .Because("the peer's vote writes its candidacy before it reads the assignment, so it sees a successor: itself");
      await Assert.That(reader.GetInt64(1)).IsEqualTo(epoch + 1);
      await Assert.That(reader.GetGuid(2)).IsEqualTo(a);
      await Assert.That(reader.GetString(3)).IsEqualTo("lapsed");
    }
    await Assert.That(await _fencedAsync(conn, a, epoch, cancellationToken)).IsFalse()
      .Because("the old holder's fenced write is refused once the role moved");
  }

  [Test]
  [Timeout(60000)]
  public async Task Successor_APeerTheRuleCallsDead_ATombstonedPeer_AndAStaleCandidacy_DoNotCountAsync(
      CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var dead = await _registerAsync(conn, TimeSpan.FromMinutes(10), cancellationToken);
    var tombstoned = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var stale = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, cancellationToken);
    await _candidateAsync(conn, dead, TimeSpan.Zero, cancellationToken);
    await _candidateAsync(conn, tombstoned, TimeSpan.Zero, cancellationToken);
    _ = await _scalarAsync(conn, "INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test')", cancellationToken,
      ("id", tombstoned));
    await _candidateAsync(conn, stale, _lease + TimeSpan.FromSeconds(1), cancellationToken);
    await _ageRoleAsync(conn, _pastTheWindow, cancellationToken);

    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsTrue()
      .Because("a dead peer, a tombstoned peer and a peer that stopped voting cannot take the role");

    // The positive control: the same stale peer voting again within the lease is a successor.
    await _candidateAsync(conn, stale, TimeSpan.Zero, cancellationToken);
    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsFalse()
      .Because("a live peer with a fresh candidacy can take the role, so the stuck holder has lapsed");
  }

  // --- what still lapses ------------------------------------------------------------------------

  [Test]
  [Timeout(60000)]
  public async Task Lapse_ASoleHolderTheRuleCallsDead_LapsesAtItsLease_AndANewInstanceWinsWithNoCoolDownAsync(
      CancellationToken cancellationToken) {
    // A crashed sole holder must lapse, so its restart (a new instance id) wins at once.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    await _ageHeartbeatAsync(conn, a, TimeSpan.FromSeconds(45), cancellationToken);
    await _ageRoleAsync(conn, _lease + TimeSpan.FromSeconds(1), cancellationToken);

    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsFalse();
    await Assert.That(await _fencedAsync(conn, a, epoch, cancellationToken)).IsFalse();
    var restarted = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    await Assert.That(await _voteAsync(conn, restarted, cancellationToken)).IsEqualTo("granted");
  }

  [Test]
  [Timeout(60000)]
  public async Task Lapse_ATombstonedSoleHolder_IsStillEvictedAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _scalarAsync(conn, "INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test')", cancellationToken, ("id", a));

    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsFalse();
    var reason = await _scalarAsync(conn, "SELECT void_reason FROM wh_role_assignment_status() WHERE role = @role", cancellationToken,
      ("role", ROLE));
    await Assert.That(reason).IsEqualTo("evicted").Because("a tombstoned holder is gone whether or not a peer exists");
  }

  // --- the cool-down needs a peer ---------------------------------------------------------------

  [Test]
  [Timeout(60000)]
  public async Task CoolDown_AHolderWhosePeerLeft_WinsTheRoleStraightBackAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    await _voidedWhileAPeerExistedAsync(conn, a, b, cancellationToken);
    // The peer leaves: it deregisters, so its candidacy is no longer live.
    _ = await _scalarAsync(conn, "DELETE FROM wh_service_instances WHERE instance_id = @id", cancellationToken, ("id", b));

    await Assert.That(await _voteAsync(conn, a, cancellationToken)).IsEqualTo("granted")
      .Because("the cool-down keeps the role free for a peer, and there is none");
  }

  [Test]
  [Timeout(60000)]
  public async Task CoolDown_WithALivePeer_IsAnsweredAsBeforeAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    await _voidedWhileAPeerExistedAsync(conn, a, b, cancellationToken);

    await Assert.That(await _voteAsync(conn, a, cancellationToken)).IsEqualTo("cooling_down");
  }

  // --- the status names a stalled sole holder ---------------------------------------------------

  [Test]
  [Timeout(60000)]
  public async Task Status_ReportsAStalledSoleHolder_AsHeld_AndStalledWithoutSuccessorAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, cancellationToken);
    await _ageRoleAsync(conn, _pastTheWindow, cancellationToken);

    var (state, stalled) = await _statusAsync(conn, cancellationToken);
    await Assert.That(state).IsEqualTo("held");
    await Assert.That(stalled).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Status_AHealthySoleHolder_IsNotStalledAsync(CancellationToken cancellationToken) {
    // Having no peer is not a fault: a single-instance service that renews reads healthy, and so does one inside its
    // progress window.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, cancellationToken);
    await Assert.That((await _statusAsync(conn, cancellationToken)).Stalled).IsFalse();

    await _ageRoleAsync(conn, TimeSpan.FromSeconds(40), cancellationToken);
    await Assert.That((await _statusAsync(conn, cancellationToken)).Stalled).IsFalse()
      .Because("past its lease but inside its window the holder is kept by its progress, not by the missing peer");
  }

  // --- plan shape: the successor probe stays on the candidates' primary key ---------------------

  [Test]
  [Timeout(60000)]
  public async Task Successor_ReadsTheCandidatesByTheirPrimaryKeyAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    // Enough candidates across roles that a sequential scan would not be the cheaper plan.
    _ = await _scalarAsync(conn, """
      INSERT INTO wh_role_candidates (role, instance_id, version_key, last_voted_at)
      SELECT 'plan-role-' || (g % 200), gen_random_uuid(), '{}', now() FROM generate_series(1, 4000) g
      ON CONFLICT DO NOTHING
      """, cancellationToken);
    _ = await _scalarAsync(conn, "ANALYZE wh_role_candidates", cancellationToken);

    var plan = await _planAsync(conn, """
      SELECT 1 FROM wh_role_candidates c
       WHERE c.role = 'plan-role-7'
         AND c.instance_id IS DISTINCT FROM '00000000-0000-0000-0000-000000000001'::uuid
         AND c.last_voted_at + INTERVAL '15 seconds' > now()
      """, cancellationToken);

    await Assert.That(plan).Contains("wh_role_candidates_pkey");
    await Assert.That(plan).DoesNotContain("Seq Scan on wh_role_candidates");
  }

  // --- helpers ----------------------------------------------------------------------------------

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync(ct);
    return result is DBNull ? null : result;
  }

  private static async Task<string> _planAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "EXPLAIN " + sql;
    var lines = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) {
      lines.Add(reader.GetString(0));
    }
    return string.Join('\n', lines);
  }

  /// <summary>A pooled registration whose heartbeat is <paramref name="age"/> old by the database clock, with a 30 second window.</summary>
  private static async Task<Guid> _registerAsync(NpgsqlConnection conn, TimeSpan age, CancellationToken ct) {
    var id = (Guid)TrackedGuid.New();
    _ = await _scalarAsync(conn, """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, liveness_window_seconds)
      VALUES (@id, 'successor-svc', 'successor-host', 1, NOW() - @age, NOW() - INTERVAL '1 hour', @mode, 30)
      """, ct, ("id", id), ("age", age), ("mode", InstanceConnectionModes.POOLED));
    return id;
  }

  private static async Task _ageHeartbeatAsync(NpgsqlConnection conn, Guid id, TimeSpan age, CancellationToken ct) =>
    _ = await _scalarAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - @age WHERE instance_id = @id",
      ct, ("age", age), ("id", id));

  /// <summary>A candidacy for the role whose last vote is <paramref name="age"/> old, as a peer's own vote writes it.</summary>
  private static async Task _candidateAsync(NpgsqlConnection conn, Guid id, TimeSpan age, CancellationToken ct) =>
    _ = await _scalarAsync(conn, """
      INSERT INTO wh_role_candidates (role, instance_id, version_key, last_voted_at) VALUES (@role, @id, '{}', NOW() - @age)
      ON CONFLICT (role, instance_id) DO UPDATE SET last_voted_at = EXCLUDED.last_voted_at
      """, ct, ("role", ROLE), ("id", id), ("age", age));

  private static async Task _ageRoleAsync(NpgsqlConnection conn, TimeSpan by, CancellationToken ct) =>
    _ = await _scalarAsync(conn, """
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
             lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
       WHERE role = @role
      """, ct, ("by", by), ("role", ROLE));

  /// <summary>
  /// The state a peer's vote leaves when it voids <paramref name="holder"/> as lapsed: the role vacant, the holder its
  /// last holder, lapsed just now and not fleet-wide; <paramref name="peer"/> is a live candidate.
  /// </summary>
  private static async Task _voidedWhileAPeerExistedAsync(NpgsqlConnection conn, Guid holder, Guid peer, CancellationToken ct) {
    _ = await _grantAsync(conn, holder, ct);
    await _candidateAsync(conn, peer, TimeSpan.Zero, ct);
    _ = await _scalarAsync(conn, """
      UPDATE wh_role_assignments
         SET holder_instance_id = NULL, lease_expires_at = NULL, last_holder_instance_id = @holder,
             last_vacated_at = NOW(), last_vacated_reason = 'lapsed', last_vacated_fleet_wide = FALSE
       WHERE role = @role
      """, ct, ("holder", holder), ("role", ROLE));
  }

  private static async Task<string> _voteAsync(NpgsqlConnection conn, Guid id, CancellationToken ct) =>
    (await _voteWithEpochAsync(conn, id, ct)).Outcome;

  private static async Task<(string Outcome, long Epoch)> _voteWithEpochAsync(NpgsqlConnection conn, Guid id, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome, epoch FROM wh_vote_role(@role, @id, @lease, @cooldown, NULL, NULL, NULL)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue("lease", _lease);
    cmd.Parameters.AddWithValue("cooldown", _cooldown);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    _ = await reader.ReadAsync(ct);
    return (reader.GetString(0), reader.GetInt64(1));
  }

  private static async Task<long> _grantAsync(NpgsqlConnection conn, Guid id, CancellationToken ct) {
    var (outcome, epoch) = await _voteWithEpochAsync(conn, id, ct);
    if (outcome != "granted") {
      throw new InvalidOperationException($"expected a grant for {id}, got {outcome}");
    }
    return epoch;
  }

  private static async Task<bool> _declareAsync(NpgsqlConnection conn, Guid id, long epoch, CancellationToken ct) =>
    await _scalarAsync(conn, "SELECT wh_declare_role_progress_window(@role, @id, @epoch, @window)", ct,
      ("role", ROLE), ("id", id), ("epoch", epoch), ("window", _window)) is true;

  private static async Task<bool> _heldAsync(NpgsqlConnection conn, Guid id, long epoch, CancellationToken ct) =>
    await _scalarAsync(conn, "SELECT wh_role_is_held(@role, @id, @epoch)", ct, ("role", ROLE), ("id", id), ("epoch", epoch)) is true;

  private static async Task<bool> _fencedAsync(NpgsqlConnection conn, Guid id, long epoch, CancellationToken ct) {
    try {
      _ = await _scalarAsync(conn, "SELECT wh_assert_role_epoch(@role, @id, @epoch)", ct, ("role", ROLE), ("id", id), ("epoch", epoch));
      return true;
    } catch (PostgresException ex) when (ex.SqlState == "WHF01") {
      return false;
    }
  }

  private static async Task<(string State, bool Stalled)> _statusAsync(NpgsqlConnection conn, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT state, stalled_without_successor FROM wh_role_assignment_status() WHERE role = @role";
    cmd.Parameters.AddWithValue("role", ROLE);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    _ = await reader.ReadAsync(ct);
    return (reader.GetString(0), reader.GetBoolean(1));
  }
}
