using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Multi-instance end-to-end coverage for duty election (increment 7's testing bar): N instances
/// contend and exactly one holds; the recorded holdings match who actually holds the lock; the
/// holder releases (or dies) and another acquires; an evicted instance is refused at acquisition
/// even when it wins the primitive. Each simulated instance is its own elector over its own
/// connection, exactly as separate pods would be.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDutyElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/108_InstanceCapabilities.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class DutyElectionE2ETests : EFCoreTestBase {

  private sealed class _pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "duty-svc";
    public string HostName => "duty-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private PgDutyElector _electorFor(_pod pod) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    NullLogger<PgDutyElector>.Instance);

  private async Task _joinFleetAsync(_pod pod, CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
  }

  private async Task<bool> _holdsAsync(Guid instanceId, string duty, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_instance_capabilities WHERE capability = @duty AND instance_id = @id)";
    cmd.Parameters.AddWithValue("duty", duty);
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  [Test]
  [Timeout(120000)]
  public async Task Contention_ExactlyOneWins_AndTheRowReportsTheLockHolderAsync(CancellationToken cancellationToken) {
    var pods = new[] { new _pod(), new _pod(), new _pod(), new _pod(), new _pod() };
    foreach (var pod in pods) {
      await _joinFleetAsync(pod, cancellationToken);
    }

    // Five instances contend concurrently — the real race, not a sequential pretence of one.
    var attempts = await Task.WhenAll(pods.Select(pod =>
      _electorFor(pod).TryAcquireAsync("migrator", cancellationToken)));

    var grants = attempts.Where(g => g is not null).ToList();
    await Assert.That(grants.Count).IsEqualTo(1)
      .Because("a duty is exclusive: five contenders, one holder — that is what election means");

    var winner = pods[Array.FindIndex(attempts, g => g is not null)];
    await Assert.That(await _holdsAsync(winner.InstanceId, "migrator", cancellationToken)).IsTrue()
      .Because("the lock decides, the row reports — and they must agree once the dust settles");
    foreach (var pod in pods.Where(p => p.InstanceId != winner.InstanceId)) {
      await Assert.That(await _holdsAsync(pod.InstanceId, "migrator", cancellationToken)).IsFalse()
        .Because("a loser records nothing — it holds nothing");
    }
    await Assert.That(await grants[0]!.VerifyStillHeldAsync(cancellationToken)).IsTrue();

    // Clean release: the loser's next attempt wins, and the record follows the lock.
    await grants[0]!.DisposeAsync();
    await Assert.That(await _holdsAsync(winner.InstanceId, "migrator", cancellationToken)).IsFalse()
      .Because("a clean release deletes the holding — tenure ended, the row says so");
    var loser = pods.First(p => p.InstanceId != winner.InstanceId);
    await using var takeover = await _electorFor(loser).TryAcquireAsync("migrator", cancellationToken);
    await Assert.That(takeover).IsNotNull()
      .Because("a released duty is always one somebody is about to take — no reaper needed");
    await Assert.That(await _holdsAsync(loser.InstanceId, "migrator", cancellationToken)).IsTrue();
  }

  [Test]
  [Timeout(120000)]
  public async Task EvictedInstance_IsRefusedAtAcquisition_EvenWithTheLockFreeAsync(CancellationToken cancellationToken) {
    var pod = new _pod();
    await _joinFleetAsync(pod, cancellationToken);

    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "INSERT INTO wh_instance_evictions (instance_id, reason) VALUES (@id, 'e2e-eviction')";
      cmd.Parameters.AddWithValue("id", pod.InstanceId);
      await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var grant = await _electorFor(pod).TryAcquireAsync("maintainer", cancellationToken);

    await Assert.That(grant).IsNull()
      .Because("the fence reaches exclusive work: an evicted instance that wins the primitive is "
             + "refused at recording, releases what it won, and stands down");
    await Assert.That(await _holdsAsync(pod.InstanceId, "maintainer", cancellationToken)).IsFalse();

    // And the released lock is genuinely free — a live instance takes it immediately.
    var live = new _pod();
    await _joinFleetAsync(live, cancellationToken);
    await using var liveGrant = await _electorFor(live).TryAcquireAsync("maintainer", cancellationToken);
    await Assert.That(liveGrant).IsNotNull();
  }

  [Test]
  [Timeout(120000)]
  public async Task DirtyDeath_TheGrantKnowsItIsLost_AndAnotherInstanceAcquiresAsync(CancellationToken cancellationToken) {
    var victim = new _pod();
    var successor = new _pod();
    await _joinFleetAsync(victim, cancellationToken);
    await _joinFleetAsync(successor, cancellationToken);

    var grant = await _electorFor(victim).TryAcquireAsync("migrator", cancellationToken);
    await Assert.That(grant).IsNotNull();

    // The OOMKill shape: terminate the backend session holding the duty lock, without any clean
    // release. Postgres frees the session lock as the backend dies.
    var key = DutyLockKey.Compute("public", "migrator");
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var kill = conn.CreateCommand();
      kill.CommandText = @"
        SELECT pg_terminate_backend(l.pid)
        FROM pg_locks l
        WHERE l.locktype = 'advisory'
          AND ((l.classid::bigint << 32) | (l.objid::bigint & 4294967295)) = @key
          AND l.pid <> pg_backend_pid()";
      kill.Parameters.AddWithValue("key", key);
      _ = await kill.ExecuteScalarAsync(cancellationToken);
    }

    await Assert.That(await grant!.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("fencing: a grant whose session died is a grant another instance may already hold — "
             + "the holder must learn this before its next unit of exclusive work");

    await using var takeover = await _electorFor(successor).TryAcquireAsync("migrator", cancellationToken);
    await Assert.That(takeover).IsNotNull()
      .Because("a clean OR dirty death releases the session lock server-side; the next attempt wins");
    await Assert.That(await _holdsAsync(successor.InstanceId, "migrator", cancellationToken)).IsTrue();
    // The victim's stale row is EXPECTED to linger until its instance row reaps — the record is
    // inconsistent only inside the lease window the system already has, and the lock (not the
    // row) is what decides. Asserting it were already gone would assert a design that needs a
    // reaper this table exists to avoid.
    await Assert.That(await _holdsAsync(victim.InstanceId, "migrator", cancellationToken)).IsTrue();

    await grant.DisposeAsync();
  }
}
