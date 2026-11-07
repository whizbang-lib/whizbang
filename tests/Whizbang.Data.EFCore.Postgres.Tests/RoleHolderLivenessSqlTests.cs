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
/// Migration 355 (#2140): a role holder's liveness is judged by the one liveness rule (<c>wh_instance_is_live</c>), the
/// same rule the claim, the orphan claims and the partition candidates read. Past its lease, a holder keeps the role
/// while the rule calls it live and it made progress (renewed from its work loop) inside the progress window it declared
/// for its epoch; a holder the rule calls dead lapses at its lease, and a connected but stuck holder lapses at its window.
/// </summary>
/// <remarks>
/// Database time is advanced by shifting stored instants back; a held alive-lock is a session of its own, as an
/// instance's notification connection is. Nothing sleeps, polls or races.
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/355_RoleHoldersAreJudgedByTheLivenessRule.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class RoleHolderLivenessSqlTests : EFCoreTestBase {
  private const string ROLE = "maintainer";
  private static readonly TimeSpan _lease = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan _cooldown = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan _window = TimeSpan.FromMinutes(2);

  // --- the holder is judged by the rule ---------------------------------------------------------

  [Test]
  [Timeout(60000)]
  public async Task Vote_AHolderPastItsLease_ThatTheRuleCallsLive_AndThatMadeProgressInsideItsWindow_KeepsTheRoleAsync(
      CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    await Assert.That(await _declareAsync(conn, a, epoch, _window, cancellationToken)).IsTrue();

    // A busy startup: 40 seconds without a renewal is past the 15 second lease, inside the 2 minute window.
    await _ageRoleAsync(conn, TimeSpan.FromSeconds(40), cancellationToken);

    await Assert.That(await _voteAsync(conn, b, cancellationToken)).IsEqualTo("contended")
      .Because("the rule calls the holder live and it made progress inside its window, so its role has not lapsed");
    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsTrue();
    await Assert.That(await _fencedAsync(conn, a, epoch, cancellationToken)).IsTrue()
      .Because("the fence treats the holder as live exactly when the vote does");
    await Assert.That(await _renewAsync(conn, a, epoch, cancellationToken)).IsEqualTo("renewed")
      .Because("the holder's late verify renews the role it never lost");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_ADirectHolderHoldingItsAliveLock_DoesNotLapse_WhileItsHeartbeatIsPastOneBeatAsync(
      CancellationToken cancellationToken) {
    // A holder busy with a slow startup: its heartbeat row is past one beat, but it holds its alive-lock on its
    // notification connection, which the rule counts as live. Inside its window it keeps the role.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.DIRECT, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, _window, cancellationToken);
    await using var aliveLock = await _holdAliveLockAsync(a, cancellationToken);
    await _ageHeartbeatAsync(conn, a, TimeSpan.FromSeconds(60), cancellationToken);
    await _ageRoleAsync(conn, TimeSpan.FromSeconds(40), cancellationToken);

    await Assert.That(await _voteAsync(conn, b, cancellationToken)).IsEqualTo("contended");
    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_AHolderTheRuleCallsDead_LapsesAtItsLease_InsideItsWindowAsync(CancellationToken cancellationToken) {
    // A crashed holder: no alive-lock, and its heartbeat is past its window. Its declared window does not keep it.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.DIRECT, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, _window, cancellationToken);
    await _ageHeartbeatAsync(conn, a, TimeSpan.FromSeconds(45), cancellationToken);
    await _ageRoleAsync(conn, TimeSpan.FromSeconds(40), cancellationToken);

    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsFalse();
    await Assert.That(await _fencedAsync(conn, a, epoch, cancellationToken)).IsFalse();
    await Assert.That(await _voteAsync(conn, b, cancellationToken)).IsEqualTo("granted");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_AConnectedButStuckHolder_LapsesAtItsProgressWindow_ThoughTheRuleCallsItLiveAsync(
      CancellationToken cancellationToken) {
    // The role-specific half: the instance is connected and heartbeating, but its work loop has not verified for longer
    // than its window, so the role goes to another instance.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.DIRECT, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, _window, cancellationToken);
    await using var aliveLock = await _holdAliveLockAsync(a, cancellationToken);
    await _ageRoleAsync(conn, _window + TimeSpan.FromSeconds(1), cancellationToken);

    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsFalse();
    await Assert.That(await _renewAsync(conn, a, epoch, cancellationToken)).IsEqualTo("lost")
      .Because("a stuck holder that wakes past its window does not get its role back by renewing");
    await Assert.That(await _voteAsync(conn, b, cancellationToken)).IsEqualTo("granted");
  }

  [Test]
  [Timeout(60000)]
  public async Task Vote_AHolderThatDeclaredNoWindow_LapsesAtItsLease_AsBeforeAsync(CancellationToken cancellationToken) {
    // An instance on an older release never declares a window: it is judged by its lease alone, as it always was.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    _ = await _grantAsync(conn, a, cancellationToken);
    await _ageRoleAsync(conn, TimeSpan.FromSeconds(16), cancellationToken);

    await Assert.That(await _voteAsync(conn, b, cancellationToken)).IsEqualTo("granted");
  }

  [Test]
  [Timeout(60000)]
  public async Task ProgressWindow_CountsOnlyForTheEpochThatDeclaredItAsync(CancellationToken cancellationToken) {
    // A releases a role whose window it declared; B wins the next epoch on an older release and declares none. The
    // window left on the row is A's, and does not keep B.
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var c = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var first = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, first, _window, cancellationToken);
    _ = await _scalarAsync(conn, "SELECT wh_release_role(@role, @id, @epoch)", cancellationToken,
      ("role", ROLE), ("id", a), ("epoch", first));
    _ = await _grantAsync(conn, b, cancellationToken);
    await _ageRoleAsync(conn, TimeSpan.FromSeconds(16), cancellationToken);

    await Assert.That(await _voteAsync(conn, c, cancellationToken)).IsEqualTo("granted");
  }

  [Test]
  [Timeout(60000)]
  public async Task DeclareProgressWindow_IsRefusedToAHolderThatDoesNotHoldTheRole_AndAZeroWindowTurnsItOffAsync(
      CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);

    await Assert.That(await _declareAsync(conn, b, epoch, _window, cancellationToken)).IsFalse();
    await Assert.That(await _declareAsync(conn, a, epoch + 1, _window, cancellationToken)).IsFalse();
    await Assert.That(await _declareAsync(conn, a, epoch, TimeSpan.Zero, cancellationToken)).IsTrue();
    await _ageRoleAsync(conn, TimeSpan.FromSeconds(16), cancellationToken);
    await Assert.That(await _voteAsync(conn, b, cancellationToken)).IsEqualTo("granted")
      .Because("a zero window declares no extension: the lease alone decides");
  }

  [Test]
  [Timeout(60000)]
  public async Task RoleHeld_AnswersForTheCurrentHolderAndEpochOnlyAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var b = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);

    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken)).IsTrue();
    await Assert.That(await _heldAsync(conn, b, epoch, cancellationToken)).IsFalse();
    await Assert.That(await _heldAsync(conn, a, epoch - 1, cancellationToken)).IsFalse();
    await Assert.That(await _heldAsync(conn, a, epoch, cancellationToken, role: "no-such-role")).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task Status_ReportsAHolderKeptByTheRule_AsHeldAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    var a = await _registerAsync(conn, InstanceConnectionModes.POOLED, TimeSpan.Zero, cancellationToken);
    var epoch = await _grantAsync(conn, a, cancellationToken);
    _ = await _declareAsync(conn, a, epoch, _window, cancellationToken);
    await _ageRoleAsync(conn, TimeSpan.FromSeconds(40), cancellationToken);

    var state = await _scalarAsync(conn, "SELECT state FROM wh_role_assignment_status() WHERE role = @role", cancellationToken,
      ("role", ROLE));
    await Assert.That(state).IsEqualTo("held");
  }

  // --- plan shape: the new reads stay on the role's primary key -----------------------------------

  [Test]
  [Timeout(60000)]
  public async Task RoleHeld_ReadsTheAssignmentByItsPrimaryKeyAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync(cancellationToken);
    // Enough roles that a sequential scan would not be the cheaper plan.
    _ = await _scalarAsync(conn, """
      INSERT INTO wh_role_assignments (role) SELECT 'plan-role-' || g FROM generate_series(1, 2000) g ON CONFLICT DO NOTHING
      """, cancellationToken);
    _ = await _scalarAsync(conn, "ANALYZE wh_role_assignments", cancellationToken);

    var plan = await _planAsync(conn, """
      SELECT 1 FROM wh_role_assignments r
       WHERE r.role = 'maintainer' AND r.holder_instance_id = '00000000-0000-0000-0000-000000000001'::uuid AND r.epoch = 1
         AND _role_lapse_reason(r) IS NULL
      """, cancellationToken);

    await Assert.That(plan).Contains("wh_role_assignments_pkey");
    await Assert.That(plan).DoesNotContain("Seq Scan on wh_role_assignments");
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

  /// <summary>A registration row with a connection mode, a heartbeat age by the database clock, and a 30 second window.</summary>
  private static async Task<Guid> _registerAsync(NpgsqlConnection conn, string mode, TimeSpan age, CancellationToken ct) {
    var id = (Guid)TrackedGuid.New();
    _ = await _scalarAsync(conn, """
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode, liveness_window_seconds)
      VALUES (@id, 'role-svc', 'role-host', 1, NOW() - @age, NOW() - INTERVAL '1 hour', @mode, 30)
      """, ct, ("id", id), ("age", age), ("mode", mode));
    return id;
  }

  private static async Task _ageHeartbeatAsync(NpgsqlConnection conn, Guid id, TimeSpan age, CancellationToken ct) =>
    _ = await _scalarAsync(conn, "UPDATE wh_service_instances SET last_heartbeat_at = NOW() - @age WHERE instance_id = @id",
      ct, ("age", age), ("id", id));

  private static async Task _ageRoleAsync(NpgsqlConnection conn, TimeSpan by, CancellationToken ct) =>
    _ = await _scalarAsync(conn, """
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
             lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
       WHERE role = @role
      """, ct, ("by", by), ("role", ROLE));

  private async Task<NpgsqlConnection> _holdAliveLockAsync(Guid id, CancellationToken ct) {
    var holder = await _openAsync(ct);
    if (await _scalarAsync(holder, "SELECT claim_instance_alive_lock(@id)", ct, ("id", id)) is not true) {
      throw new InvalidOperationException($"the alive-lock for {id} was already held");
    }
    return holder;
  }

  private static async Task<string> _voteAsync(NpgsqlConnection conn, Guid id, CancellationToken ct) =>
    (string)(await _scalarAsync(conn, "SELECT outcome FROM wh_vote_role(@role, @id, @lease, @cooldown, NULL, NULL, NULL)", ct,
      ("role", ROLE), ("id", id), ("lease", _lease), ("cooldown", _cooldown)))!;

  private static async Task<long> _grantAsync(NpgsqlConnection conn, Guid id, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT outcome, epoch FROM wh_vote_role(@role, @id, @lease, @cooldown, NULL, NULL, NULL)";
    cmd.Parameters.AddWithValue("role", ROLE);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("lease", _lease);
    cmd.Parameters.AddWithValue("cooldown", _cooldown);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    _ = await reader.ReadAsync(ct);
    if (reader.GetString(0) != "granted") {
      throw new InvalidOperationException($"expected a grant for {id}, got {reader.GetString(0)}");
    }
    return reader.GetInt64(1);
  }

  private static async Task<bool> _declareAsync(NpgsqlConnection conn, Guid id, long epoch, TimeSpan window, CancellationToken ct) =>
    await _scalarAsync(conn, "SELECT wh_declare_role_progress_window(@role, @id, @epoch, @window)", ct,
      ("role", ROLE), ("id", id), ("epoch", epoch), ("window", window)) is true;

  private static async Task<bool> _heldAsync(NpgsqlConnection conn, Guid id, long epoch, CancellationToken ct, string role = ROLE) =>
    await _scalarAsync(conn, "SELECT wh_role_is_held(@role, @id, @epoch)", ct, ("role", role), ("id", id), ("epoch", epoch)) is true;

  private static async Task<string> _renewAsync(NpgsqlConnection conn, Guid id, long epoch, CancellationToken ct) =>
    (string)(await _scalarAsync(conn, "SELECT wh_renew_role_lease(@role, @id, @epoch)", ct,
      ("role", ROLE), ("id", id), ("epoch", epoch)))!;

  private static async Task<bool> _fencedAsync(NpgsqlConnection conn, Guid id, long epoch, CancellationToken ct) {
    try {
      _ = await _scalarAsync(conn, "SELECT wh_assert_role_epoch(@role, @id, @epoch)", ct, ("role", ROLE), ("id", id), ("epoch", epoch));
      return true;
    } catch (PostgresException ex) when (ex.SqlState == "WHF01") {
      return false;
    }
  }
}
