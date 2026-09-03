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
[Category("Shard3")]
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

    var grants = attempts.Select(a => a.Grant).Where(g => g is not null).ToList();
    await Assert.That(attempts.Count(a => a.Refusal == DutyRefusal.Contended)).IsEqualTo(4)
      .Because("the losers lost the RACE — a retryable refusal, and now the elector says so");
    await Assert.That(grants.Count).IsEqualTo(1)
      .Because("a duty is exclusive: five contenders, one holder — that is what election means");

    var winner = pods[Array.FindIndex(attempts, a => a.Grant is not null)];
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
    await using var takeover = (await _electorFor(loser).TryAcquireAsync("migrator", cancellationToken)).Grant;
    await Assert.That(takeover).IsNotNull()
      .Because("a released duty is always one somebody is about to take — no reaper needed");
    await Assert.That(await _holdsAsync(loser.InstanceId, "migrator", cancellationToken)).IsTrue();
  }

  private sealed class _countingStep : IStartupStep {
    private int _executions;
    public int Executions => Volatile.Read(ref _executions);
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "ExclusiveWork",
      RequiredCapability = "pipeline-duty",
      NonHolderBehavior = NonHolderBehavior.Skip,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _executions);
      return new(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task TwoPipelines_RaceADutyStep_ExactlyOneInstanceRunsItAsync(CancellationToken cancellationToken) {
    var podA = new _pod();
    var podB = new _pod();
    await _joinFleetAsync(podA, cancellationToken);
    await _joinFleetAsync(podB, cancellationToken);

    var stepA = new _countingStep();
    var stepB = new _countingStep();
    var runnerA = new StartupPipelineRunner([stepA], dutyElector: _electorFor(podA));
    var runnerB = new StartupPipelineRunner([stepB], dutyElector: _electorFor(podB));

    // Both instances run their pipelines concurrently — the real race, through the real elector.
    var results = await Task.WhenAll(
      runnerA.RunAsync(cancellationToken), runnerB.RunAsync(cancellationToken));

    await Assert.That(stepA.Executions + stepB.Executions).IsEqualTo(1)
      .Because("a step requiring a duty runs on the one instance that wins it — the pipeline's "
             + "exclusivity is the capability's, not a mode it enters");
    var outcomes = new[] { results[0][0].Outcome, results[1][0].Outcome };
    await Assert.That(outcomes.Count(o => o == StartupStepOutcome.Completed)).IsEqualTo(1);
    await Assert.That(outcomes.Count(o => o == StartupStepOutcome.Skipped)).IsEqualTo(1)
      .Because("the loser reports 'capability not held' — a different fact from 'found nothing "
             + "to do', and an operator needs to tell them apart");
    // Tenure ended with the step: the grant released, so nothing lingers held.
    await Assert.That(await _holdsAsync(podA.InstanceId, "pipeline-duty", cancellationToken)).IsFalse();
    await Assert.That(await _holdsAsync(podB.InstanceId, "pipeline-duty", cancellationToken)).IsFalse();
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

    var evictedAttempt = await _electorFor(pod).TryAcquireAsync("maintainer", cancellationToken);

    await Assert.That(evictedAttempt.Grant).IsNull()
      .Because("the fence reaches exclusive work: an evicted instance that wins the primitive is "
             + "refused at recording, releases what it won, and stands down");
    await Assert.That(evictedAttempt.Refusal).IsEqualTo(DutyRefusal.Refused)
      .Because("issue #494: not a race — retrying cannot help, and the caller must be able to tell");
    await Assert.That(await _holdsAsync(pod.InstanceId, "maintainer", cancellationToken)).IsFalse();

    // And the released lock is genuinely free — a live instance takes it immediately.
    var live = new _pod();
    await _joinFleetAsync(live, cancellationToken);
    await using var liveGrant = (await _electorFor(live).TryAcquireAsync("maintainer", cancellationToken)).Grant;
    await Assert.That(liveGrant).IsNotNull();
  }

  [Test]
  [Timeout(120000)]
  public async Task DirtyDeath_TheGrantKnowsItIsLost_AndAnotherInstanceAcquiresAsync(CancellationToken cancellationToken) {
    var victim = new _pod();
    var successor = new _pod();
    await _joinFleetAsync(victim, cancellationToken);
    await _joinFleetAsync(successor, cancellationToken);

    var grant = (await _electorFor(victim).TryAcquireAsync("migrator", cancellationToken)).Grant;
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

    await using var takeover = (await _electorFor(successor).TryAcquireAsync("migrator", cancellationToken)).Grant;
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

  [Test]
  [Timeout(60000)]
  public async Task AVerifyCancelledByShutdown_DoesNotMarkTheDutyLostAsync(CancellationToken cancellationToken) {
    // VerifyStillHeldAsync pings the session that holds the advisory lock. A ping that FAILS means
    // the session is gone and so is the lock, so the grant latches _lost and the caller stops its
    // exclusive work — correct, and deliberately sticky, because a lock cannot come back.
    //
    // A ping cancelled by shutdown proves nothing about the lock. Latching there would leave a
    // grant that reports "not held" for the rest of its life while still owning the lock, and the
    // stickiness that makes the real case safe is exactly what makes this one unrecoverable.
    var pod = new _pod();
    await _joinFleetAsync(pod, cancellationToken);
    await using var grant = (await _electorFor(pod).TryAcquireAsync("migrator", cancellationToken)).Grant;

    await Assert.That(grant).IsNotNull();
    await Assert.That(await grant!.VerifyStillHeldAsync(cancellationToken)).IsTrue();

    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();
    await Assert.That(async () => await grant.VerifyStillHeldAsync(stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("a cancelled ping says nothing about whether the lock is still held");

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsTrue()
      .Because("the grant still owns the lock — had the shutdown latched _lost, this would report "
             + "not-held forever and the duty would go unclaimed while nobody else can take it");
  }
}
