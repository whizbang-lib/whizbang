using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Role assignment through the real elector (#966): each simulated instance is its own elector
/// over its own connections, exactly as separate pods would be. Database time is advanced by
/// shifting the stored instants back, and the grant's renew throttle runs on a fake clock, so
/// nothing here sleeps and nothing retries.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/173_RoleAssignments.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RoleAssignmentElectorE2ETests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;
  private static readonly RoleAssignmentOptions _defaults = new();

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "role-svc";
    public string HostName => "role-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private WhizbangNotificationOptions _notificationOptions() => new() { DirectConnectionString = ConnectionString };

  private PgDutyElector _legacyFor(Pod pod) => new(
    Options.Create(_notificationOptions()),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    NullLogger<PgDutyElector>.Instance);

  private PgRoleElector _electorFor(Pod pod, bool bridge = false, TimeProvider? time = null) => new(
    Options.Create(_notificationOptions()),
    Options.Create(new RoleAssignmentOptions { HoldLegacySessionLock = bridge }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    _legacyFor(pod),
    NullLogger<PgRoleElector>.Instance,
    libraryVersion: null,
    timeProvider: time);

  private async Task<Pod> _joinAsync(CancellationToken ct) {
    var pod = new Pod();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
    return pod;
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task _executeAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Moves the database clock forward for the role by moving its stored instants back.</summary>
  private Task _ageAsync(TimeSpan by, CancellationToken ct) => _executeAsync("""
    UPDATE wh_role_assignments
       SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
           lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
     WHERE role = @role
    """, ct, ("by", by), ("role", ROLE));

  private async Task<TimeSpan?> _leaseRemainingAsync(CancellationToken ct) {
    await using var conn = await _openAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT lease_remaining FROM wh_role_assignment_status() WHERE role = @role";
    cmd.Parameters.AddWithValue("role", ROLE);
    var value = await cmd.ExecuteScalarAsync(ct);
    return value is TimeSpan t ? t : null;
  }

  private async Task<bool> _fencedWriteAsync(Guid instanceId, long epoch, CancellationToken ct) {
    try {
      await _executeAsync("SELECT wh_assert_role_epoch(@role, @id, @epoch)", ct, ("role", ROLE), ("id", instanceId), ("epoch", epoch));
      return true;
    } catch (PostgresException ex) when (ex.SqlState == "WHF01") {
      return false;
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task TenInstancesRacing_ExactlyOneHolderAndOneEpochActAsync(CancellationToken cancellationToken) {
    var pods = new List<Pod>();
    for (var i = 0; i < 10; i++) {
      pods.Add(await _joinAsync(cancellationToken));
    }

    var attempts = await Task.WhenAll(pods.Select(pod => _electorFor(pod).TryAcquireAsync(ROLE, cancellationToken)));

    var granted = attempts.Where(a => a.Grant is not null).ToList();
    await Assert.That(granted.Count).IsEqualTo(1)
      .Because("ten contenders, one vote winner: the vote lock serializes the votes that could change the row");
    await Assert.That(attempts.Count(a => a.Refusal == DutyRefusal.Contended)).IsEqualTo(9);
    await Assert.That(granted[0].Grant!.Epoch).IsEqualTo(1L);

    var winner = pods[Array.FindIndex(attempts, a => a.Grant is not null)];
    var writers = 0;
    foreach (var instanceId in pods.Select(pod => pod.InstanceId)) {
      if (await _fencedWriteAsync(instanceId, 1L, cancellationToken)) {
        writers++;
        await Assert.That(instanceId).IsEqualTo(winner.InstanceId);
      }
    }
    await Assert.That(writers).IsEqualTo(1).Because("exactly one (holder, epoch) passes the fence");
    await granted[0].Grant!.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task CrashedHolder_VoidsAfterTheLapseBound_AndTheNextVoteTakesOverAsync(CancellationToken cancellationToken) {
    // Requirement 2: the crashed holder never calls anything again. Its grant is simply abandoned.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var crashed = (await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    var tooSoon = await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(tooSoon.Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(tooSoon.Detail).Contains(a.InstanceId.ToString());

    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var takeover = await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(takeover.Grant).IsNotNull();
    await Assert.That(takeover.Grant!.Epoch).IsEqualTo(crashed.Epoch + 1);
    await crashed.DisposeAsync();
    await Assert.That(await _fencedWriteAsync(b.InstanceId, takeover.Grant.Epoch!.Value, cancellationToken)).IsTrue()
      .Because("a superseded grant that never learned it was superseded still cannot release its successor");
    await takeover.Grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task StaleHolder_AfterLosingTheRole_FailsVerification_AndTheDatabaseRefusesItsWriteAsync(CancellationToken cancellationToken) {
    // Requirements 1 and 3: a holder that stopped renewing (stuck, paused, partitioned) wakes up
    // after another instance took the role. Its verify says so, and its write is refused in SQL.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var stale = (await _electorFor(a, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var current = (await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    time.Advance(_defaults.RenewInterval);

    await Assert.That(await stale.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await Assert.That(await stale.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("a lost grant stays lost; it never asks the database again");
    await Assert.That(await _fencedWriteAsync(a.InstanceId, stale.Epoch!.Value, cancellationToken)).IsFalse();
    await Assert.That(await _fencedWriteAsync(b.InstanceId, current.Epoch!.Value, cancellationToken)).IsTrue();

    await stale.DisposeAsync();
    await Assert.That(await _fencedWriteAsync(b.InstanceId, current.Epoch.Value, cancellationToken)).IsTrue()
      .Because("disposing a lost grant releases nothing: it cannot vacate its successor");
    await current.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task Verify_WithinTheRenewInterval_AnswersFromMemory_ThenRenewsTheLeaseAsync(CancellationToken cancellationToken) {
    // Requirement 3: the lease moves only when the holder's own loop verifies.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await _ageAsync(TimeSpan.FromSeconds(10), cancellationToken);

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await Assert.That((await _leaseRemainingAsync(cancellationToken))!.Value).IsLessThanOrEqualTo(TimeSpan.FromSeconds(5))
      .Because("inside one renew interval the grant answers from memory and the lease does not move");

    time.Advance(_defaults.RenewInterval);
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await Assert.That((await _leaseRemainingAsync(cancellationToken))!.Value).IsGreaterThan(TimeSpan.FromSeconds(10))
      .Because("a verify after the renew interval renews the lease from the database's now()");
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task Verify_WithACanceledToken_PropagatesCancellation_AndTheGrantStaysHeldAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    var a = await _joinAsync(cancellationToken);
    var bridged = (await _electorFor(a, bridge: true, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await Assert.That(bridged.Duty).IsEqualTo(ROLE);
    await Assert.That(bridged.AcquiredAt).IsEqualTo(time.GetUtcNow());

    await Assert.That(async () => await bridged.VerifyStillHeldAsync(canceled.Token)).Throws<OperationCanceledException>()
      .Because("shutdown is not a verdict on the role: the bridge check is abandoned, not failed");
    await Assert.That(await bridged.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await bridged.DisposeAsync();

    var plain = (await _electorFor(a, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    time.Advance(_defaults.RenewInterval);
    await Assert.That(async () => await plain.VerifyStillHeldAsync(canceled.Token)).Throws<OperationCanceledException>();
    await Assert.That(await plain.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await plain.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task GracefulRelease_HandsOffAtOnce_WithNoLapseWaitAsync(CancellationToken cancellationToken) {
    // Requirement 6.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var electorA = _electorFor(a);
    var electorB = _electorFor(b);
    var first = (await electorA.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await Assert.That((await electorB.TryAcquireAsync(ROLE, cancellationToken)).Refusal).IsEqualTo(DutyRefusal.Contended);

    await first.DisposeAsync();
    await first.DisposeAsync();
    var next = await electorB.TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(next.Grant).IsNotNull()
      .Because("the successor wins on its very next attempt, without waiting for a lease to lapse");
    await Assert.That(next.Grant!.Epoch).IsEqualTo(first.Epoch + 1);
    await Assert.That(await first.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await next.Grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task ReleaseAll_OnShutdown_ReleasesEveryOutstandingGrantAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var electorA = _electorFor(a);
    var held = (await electorA.TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await electorA.ReleaseAllAsync(cancellationToken);
    await electorA.ReleaseAllAsync(cancellationToken);

    var next = await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(next.Grant).IsNotNull();
    await Assert.That(await held.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await next.Grant!.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task Assignment_SurvivesEveryConnectionBeingTerminated_WithTheSameEpochAsync(CancellationToken cancellationToken) {
    // Requirement 8: the role is a row, so no session has to survive for the holder to keep it.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a, time: time);
    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await _executeAsync(
      "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()",
      cancellationToken);
    NpgsqlConnection.ClearAllPools();
    time.Advance(_defaults.RenewInterval);

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    var again = await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(again.Grant!.Epoch).IsEqualTo(grant.Epoch)
      .Because("the first vote after reconnect re-validates the row and finds the same live assignment");
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task Verify_WhenTheRenewCannotReachItsFunction_AnswersFalseWithoutLosingTheGrantAsync(CancellationToken cancellationToken) {
    // A transient failure is not a verdict: the lease may still be valid, and the next verify asks again.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    time.Advance(_defaults.RenewInterval);

    await _executeAsync("ALTER FUNCTION wh_renew_role_lease(text, uuid, bigint, bigint) RENAME TO wh_renew_role_lease_moved", cancellationToken);
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await _executeAsync("ALTER FUNCTION wh_renew_role_lease_moved(text, uuid, bigint, bigint) RENAME TO wh_renew_role_lease", cancellationToken);

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task Dispose_WhenTheReleaseCannotReachItsFunction_DoesNotThrow_AndTheLeaseLapsesInsteadAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a, bridge: true).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await _executeAsync("ALTER FUNCTION wh_release_role(text, uuid, bigint) RENAME TO wh_release_role_moved", cancellationToken);

    await Assert.That(async () => await grant.DisposeAsync()).ThrowsNothing();

    await Assert.That((await _legacyFor(await _joinAsync(cancellationToken)).TryAcquireAsync(ROLE, cancellationToken)).Grant).IsNotNull()
      .Because("the bridge session closes with the grant whatever the release managed");
  }

  [Test]
  [Timeout(120000)]
  public async Task MixedFleet_ALegacyHolder_BlocksABridgedInstance_AndNoAssignmentIsWrittenAsync(CancellationToken cancellationToken) {
    var old = await _joinAsync(cancellationToken);
    var @new = await _joinAsync(cancellationToken);
    await using var legacyGrant = (await _legacyFor(old).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    var attempt = await _electorFor(@new, bridge: true).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(attempt.Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(await _leaseRemainingAsync(cancellationToken)).IsNull()
      .Because("the new instance deferred before voting, so the old holder stays the only one acting");
  }

  [Test]
  [Timeout(120000)]
  public async Task MixedFleet_ABridgedHolder_BlocksALegacyInstanceAsync(CancellationToken cancellationToken) {
    var @new = await _joinAsync(cancellationToken);
    var old = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(@new, bridge: true).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    var legacyAttempt = await _legacyFor(old).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(legacyAttempt.Refusal).IsEqualTo(DutyRefusal.Contended)
      .Because("an old instance only sees the session lock, so the new holder keeps it while the bridge is on");
    await grant.DisposeAsync();
    await Assert.That((await _legacyFor(old).TryAcquireAsync(ROLE, cancellationToken)).Grant).IsNotNull();
  }

  [Test]
  [Timeout(120000)]
  public async Task MixedFleet_WithTheBridgeOff_ALegacyHolderIsStillSeenByTheVoteAsync(CancellationToken cancellationToken) {
    var old = await _joinAsync(cancellationToken);
    var @new = await _joinAsync(cancellationToken);
    await using var legacyGrant = (await _legacyFor(old).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    var attempt = await _electorFor(@new, bridge: false).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(attempt.Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(attempt.Detail).Contains("session-lock");
  }

  [Test]
  [Timeout(120000)]
  public async Task BridgedHolder_ThatLapses_GivesUpItsSessionLockOnItsNextVerifyAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a, bridge: true, time: time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await Assert.That((await _electorFor(b, bridge: true).TryAcquireAsync(ROLE, cancellationToken)).Refusal)
      .IsEqualTo(DutyRefusal.Contended);

    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    time.Advance(_defaults.RenewInterval);
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsFalse();

    var next = await _electorFor(b, bridge: true).TryAcquireAsync(ROLE, cancellationToken);
    await Assert.That(next.Grant).IsNotNull();
    await Assert.That(next.Grant!.Epoch).IsEqualTo(grant.Epoch + 1);
    await next.Grant.DisposeAsync();
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task BridgedHolder_WhoseSessionDies_IsLostAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a, bridge: true).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await _executeAsync("""
      SELECT pg_terminate_backend(l.pid) FROM pg_locks l
       WHERE l.locktype = 'advisory' AND l.granted AND l.pid <> pg_backend_pid()
      """, cancellationToken);

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("without its session lock an old instance could take the duty, so the new holder must stop");
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Vote_OnADatabaseWithoutTheRoleFunctions_Throws_AndLeavesNoSessionLockBehindAsync(
      bool bridge, CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    await _executeAsync("ALTER FUNCTION wh_vote_role(text, uuid, interval, interval, bigint, integer[], integer) RENAME TO wh_vote_role_moved", cancellationToken);

    await Assert.That(async () => await _electorFor(a, bridge).TryAcquireAsync(ROLE, cancellationToken))
      .Throws<PostgresException>();

    await Assert.That((await _legacyFor(await _joinAsync(cancellationToken)).TryAcquireAsync(ROLE, cancellationToken)).Grant).IsNotNull()
      .Because("a vote that could not run must not leave the session lock behind");
  }

  [Test]
  [Timeout(120000)]
  public async Task CoolingDown_TheLapsedInstance_IsContendedWithTheReasonAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    _ = (await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);

    // The same instance after a restart: a fresh elector with no tenure of its own.
    var attempt = await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(attempt.Refusal).IsEqualTo(DutyRefusal.Contended);
    await Assert.That(attempt.Detail).Contains("cool");
  }

  [Test]
  [Timeout(120000)]
  public async Task EvictedInstance_IsRefused_NotContendedAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    await _executeAsync("INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'test')", cancellationToken, ("id", a.InstanceId));

    var attempt = await _electorFor(a, bridge: true).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(attempt.Refusal).IsEqualTo(DutyRefusal.Refused);
    await Assert.That((await _legacyFor(await _joinAsync(cancellationToken)).TryAcquireAsync(ROLE, cancellationToken)).Grant).IsNotNull()
      .Because("a refused attempt gives the bridge lock back");
  }

  [Test]
  [Timeout(120000)]
  public async Task UnmanagedDuty_IsDelegatedToTheSessionLockElectorAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);

    var attempt = await _electorFor(a).TryAcquireAsync("host-duty", cancellationToken);

    await Assert.That(attempt.Grant).IsNotNull();
    await Assert.That(attempt.Grant!.Epoch).IsNull()
      .Because("a duty that is not a role stays on the session lock, which has no fencing token");
    await attempt.Grant.DisposeAsync();
  }

  [Test]
  [Timeout(60000)]
  public async Task NoConnection_IsUnavailable_NotContendedAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    var elector = new PgRoleElector(
      Options.Create(new WhizbangNotificationOptions()),
      Options.Create(new RoleAssignmentOptions()),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      pod,
      NullDutyElector.Instance,
      NullLogger<PgRoleElector>.Instance,
      libraryVersion: null);

    var attempt = await elector.TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(attempt.Refusal).IsEqualTo(DutyRefusal.Unavailable);
    await Assert.That(((IDutyElector)elector).IsConfigured).IsTrue();
  }

  [Test]
  [Timeout(120000)]
  public async Task TwoHoldersInOneProcess_ShareOneTenure_AndTheLastHandleReleasesAsync(CancellationToken cancellationToken) {
    // The holder loop and a startup step both hold the role on one instance: neither may release
    // it under the other.
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var elector = _electorFor(a);
    var loop = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var step = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await Assert.That(step.Epoch).IsEqualTo(loop.Epoch).Because("a second acquisition is a handle, not a second vote");

    await step.DisposeAsync();
    await Assert.That(await loop.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await Assert.That(await step.VerifyStillHeldAsync(cancellationToken)).IsFalse().Because("a closed handle holds nothing");
    await Assert.That(await _fencedWriteAsync(a.InstanceId, loop.Epoch!.Value, cancellationToken)).IsTrue();
    await Assert.That((await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken)).Refusal).IsEqualTo(DutyRefusal.Contended);

    await loop.DisposeAsync();
    await Assert.That((await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken)).Grant).IsNotNull()
      .Because("the last handle released the role");
  }

  [Test]
  [Timeout(120000)]
  public async Task ALostTenure_IsNotReused_TheNextAcquisitionVotesAgainAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a, time: time);
    var first = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await _executeAsync("UPDATE wh_role_assignments SET epoch = epoch + 10 WHERE role = @role", cancellationToken, ("role", ROLE));
    time.Advance(_defaults.RenewInterval);

    var again = await elector.TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(await first.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await Assert.That(again.Grant!.Epoch).IsEqualTo(first.Epoch + 10)
      .Because("the old tenure is gone; a fresh vote adopts whatever the row says this instance holds");
    await again.Grant.DisposeAsync();
  }

  private sealed class MeterReader : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, long Value, Dictionary<string, object?> Tags)> _seen = [];
    public MeterReader(Meter meter) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == meter) {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags) {
          dict[tag.Key] = tag.Value;
        }
        _seen.Add((instrument.Name, value, dict));
      });
      _listener.Start();
    }
    public long Total(string name, string? tag = null, object? value = null) {
      _seen.Clear();
      _listener.RecordObservableInstruments();
      return _seen.Where(s => s.Name == name && (tag is null || (s.Tags.TryGetValue(tag, out var v) && Equals(v, value))))
        .Sum(s => s.Value);
    }
    public void Dispose() => _listener.Dispose();
  }

  [Test]
  [Timeout(120000)]
  public async Task Metrics_CountElectionsHandoffsLossesReleasesAndHeldRolesAsync(CancellationToken cancellationToken) {
    // Requirement 10.
    await using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new RoleAssignmentMetrics(new WhizbangMetrics(provider.GetRequiredService<IMeterFactory>()));
    using var reader = new MeterReader(metrics.Elections.Meter);
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    PgRoleElector withMetrics(Pod pod) => new(
      Options.Create(_notificationOptions()), Options.Create(new RoleAssignmentOptions()),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(), pod, _legacyFor(pod),
      NullLogger<PgRoleElector>.Instance, libraryVersion: null, timeProvider: time, metrics: metrics);

    var first = (await withMetrics(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await Assert.That(reader.Total("whizbang.roles.held")).IsEqualTo(1);
    await first.DisposeAsync();
    var second = (await withMetrics(b).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    time.Advance(_defaults.RenewInterval);
    await Assert.That(await second.VerifyStillHeldAsync(cancellationToken)).IsFalse();

    await Assert.That(reader.Total("whizbang.roles.elections")).IsEqualTo(2);
    await Assert.That(reader.Total("whizbang.roles.handoffs", RoleAssignmentMetrics.REASON_TAG, "released")).IsEqualTo(1);
    await Assert.That(reader.Total("whizbang.roles.released")).IsEqualTo(1);
    await Assert.That(reader.Total("whizbang.roles.lost")).IsEqualTo(1);
    await Assert.That(reader.Total("whizbang.roles.held")).IsEqualTo(0);
  }

  [Test]
  [Timeout(120000)]
  public async Task ReadAssignments_ReportsHolderEpochStateAndOwedWorkAsync(CancellationToken cancellationToken) {
    // Requirements 9 and 10 through the C# reader.
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a);
    await Assert.That(await elector.ReadAssignmentsAsync(cancellationToken)).IsEmpty();

    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    await _executeAsync("SELECT wh_owe_role_work(@role, 'Rewrite')", cancellationToken, ("role", ROLE));
    var held = (await elector.ReadAssignmentsAsync(cancellationToken)).Single();
    await Assert.That(held.State).IsEqualTo(RoleAssignmentState.Held);
    await Assert.That(held.HolderInstanceId).IsEqualTo(a.InstanceId);
    await Assert.That(held.Epoch).IsEqualTo(grant.Epoch!.Value);
    await Assert.That(held.AssignedAt).IsNotNull();
    await Assert.That(held.RenewedAt).IsNotNull();
    await Assert.That(held.LeaseRemaining).IsNotNull();
    await Assert.That(held.ElectionCount).IsEqualTo(1L);
    await Assert.That(held.PendingWork).IsEqualTo(1L);

    await _ageAsync(_defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var lapsed = (await elector.ReadAssignmentsAsync(cancellationToken)).Single();
    await Assert.That(lapsed.State).IsEqualTo(RoleAssignmentState.Lapsed);
    await Assert.That(lapsed.VoidReason).IsEqualTo("lapsed");

    await _ageAsync(-(_defaults.Lease + TimeSpan.FromSeconds(1)), cancellationToken);
    await grant.DisposeAsync();
    var vacant = (await elector.ReadAssignmentsAsync(cancellationToken)).Single(s => s.Role == ROLE);
    await Assert.That(vacant.State).IsEqualTo(RoleAssignmentState.Vacant);
    await Assert.That(vacant.LastHolderInstanceId).IsEqualTo(a.InstanceId);
    await Assert.That(vacant.LastVacatedAt).IsNotNull();
    await Assert.That(vacant.LastVacatedReason).IsEqualTo("released");
  }

  [Test]
  [Timeout(60000)]
  public async Task Constructor_ValidatesItsOptions_AndItsArgumentsAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var notification = Options.Create(new WhizbangNotificationOptions());
    var roles = Options.Create(new RoleAssignmentOptions());
    var logger = NullLogger<PgRoleElector>.Instance;
    var bad = new RoleAssignmentOptions { MissedRenewalsBeforeLapse = 1 };

    await Assert.That(() => new PgRoleElector(notification, Options.Create(bad), config, pod, NullDutyElector.Instance, logger, null))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new PgRoleElector(null!, roles, config, pod, NullDutyElector.Instance, logger, null)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgRoleElector(notification, null!, config, pod, NullDutyElector.Instance, logger, null)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgRoleElector(notification, roles, null!, pod, NullDutyElector.Instance, logger, null)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgRoleElector(notification, roles, config, null!, NullDutyElector.Instance, logger, null)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgRoleElector(notification, roles, config, pod, null!, logger, null)).Throws<ArgumentNullException>();
    await Assert.That(() => new PgRoleElector(notification, roles, config, pod, NullDutyElector.Instance, null!, null)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new PgRoleElector(notification, roles, config, pod, NullDutyElector.Instance, logger, null)
      .TryAcquireAsync("", cancellationToken)).Throws<ArgumentException>();
  }
}
