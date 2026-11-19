// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
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
/// Multi-instance end-to-end coverage for duty election (increment 7's testing bar): N instances
/// contend and exactly one holds; the recorded holdings match the assignment; the holder releases
/// (or dies) and another acquires; an evicted instance is refused at acquisition. Every duty is a
/// role assignment (#2044), a framework duty or a host's own. Each simulated instance is its own
/// elector over its own connections, exactly as separate pods would be.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/108_InstanceCapabilities.sql</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class DutyElectionE2ETests : EFCoreTestBase {

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
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

  private PgRoleElector _electorFor(Pod pod, TimeProvider? time = null) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    Options.Create(new RoleAssignmentOptions()),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    NullLogger<PgRoleElector>.Instance,
    libraryVersion: null,
    timeProvider: time);

  private async Task _joinFleetAsync(Pod pod, CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
  }

  private async Task<bool> _holdsAsync(Guid instanceId, string duty, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_instance_capabilities WHERE capability = @duty AND instance_id = @id)";
    cmd.Parameters.AddWithValue(nameof(duty), duty);
    cmd.Parameters.AddWithValue("id", instanceId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  [Test]
  [Timeout(120000)]
  public async Task Contention_ExactlyOneWins_AndTheRowReportsTheLockHolderAsync(CancellationToken cancellationToken) {
    var pods = new[] { new Pod(), new Pod(), new Pod(), new Pod(), new Pod() };
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
      .Because("the vote decides, the row reports, and they must agree once the dust settles");
    foreach (var pod in pods.Where(p => p.InstanceId != winner.InstanceId)) {
      await Assert.That(await _holdsAsync(pod.InstanceId, "migrator", cancellationToken)).IsFalse()
        .Because("a loser records nothing — it holds nothing");
    }
    await Assert.That(await grants[0]!.VerifyStillHeldAsync(cancellationToken)).IsTrue();

    // Clean release: the loser's next attempt wins, and the record follows the assignment.
    await grants[0]!.DisposeAsync();
    await Assert.That(await _holdsAsync(winner.InstanceId, "migrator", cancellationToken)).IsFalse()
      .Because("a clean release deletes the holding — tenure ended, the row says so");
    var loser = pods.First(p => p.InstanceId != winner.InstanceId);
    await using var takeover = (await _electorFor(loser).TryAcquireAsync("migrator", cancellationToken)).Grant;
    await Assert.That(takeover).IsNotNull()
      .Because("a released duty is always one somebody is about to take — no reaper needed");
    await Assert.That(await _holdsAsync(loser.InstanceId, "migrator", cancellationToken)).IsTrue();
  }

  private sealed class CountingStep : IStartupStep {
    private int _executions;
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>
    /// Awaited while the step runs, so the winner keeps the duty until the test says to let go. Without a
    /// hold the winner can acquire, execute and RELEASE before the other pipeline attempts anything, and
    /// the loser then wins the duty for real and runs the step too: two executions, no defect, a failing
    /// assertion. A released duty is one somebody else may take, which a sibling test asserts on purpose.
    /// </summary>
    public Task Hold { get; init; } = Task.CompletedTask;

    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "ExclusiveWork",
      RequiredCapability = "pipeline-duty",
      NonHolderBehavior = NonHolderBehavior.Skip,
    };
    public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _executions);
      await Hold.WaitAsync(cancellationToken);
      return new StartupStepReport(StartupStepOutcome.Completed);
    }
  }

  /// <summary>
  /// Reports the first refusal any instance gets, which is the signal that the two pipelines really did
  /// contend rather than merely both having run.
  /// </summary>
  private sealed class RefusalSignallingElector(IDutyElector inner, TaskCompletionSource refused) : IDutyElector {
    public async Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var attempt = await inner.TryAcquireAsync(duty, cancellationToken);
      if (attempt.Grant is null) {
        refused.TrySetResult();
      }
      return attempt;
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task TwoPipelines_RaceADutyStep_ExactlyOneInstanceRunsItAsync(CancellationToken cancellationToken) {
    var podA = new Pod();
    var podB = new Pod();
    await _joinFleetAsync(podA, cancellationToken);
    await _joinFleetAsync(podB, cancellationToken);

    // The winner holds the duty until the loser has been refused. Task.WhenAll starts both pipelines
    // but does not make them overlap: the winner could acquire, execute and release before the other
    // instance attempts anything, and the loser would then win the duty legitimately and run the step
    // too. That is correct behavior and a failing assertion, which is the shape of this test's flake.
    var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stepA = new CountingStep { Hold = refused.Task };
    var stepB = new CountingStep { Hold = refused.Task };
    var runnerA = new StartupPipelineRunner(
      steps: [stepA], observers: [], dutyElector: new RefusalSignallingElector(_electorFor(podA), refused));
    var runnerB = new StartupPipelineRunner(
      steps: [stepB], observers: [], dutyElector: new RefusalSignallingElector(_electorFor(podB), refused));

    // Both instances run their pipelines concurrently — the real race, through the real elector. Whichever
    // loses releases the winner, so the contention is observed rather than assumed; if neither wins, the
    // signal still fires and the counts below say so.
    var results = await Task.WhenAll(
      runnerA.RunAsync(cancellationToken), runnerB.RunAsync(cancellationToken));

    await Assert.That(stepA.Executions + stepB.Executions).IsEqualTo(1)
      .Because("a step requiring a duty runs on the one instance that wins it — the pipeline's "
             + "exclusivity is the capability's, not a mode it enters. "
             + $"A={stepA.Executions} B={stepB.Executions}: "
             + "two means the exclusivity leaked, zero means neither instance won and the fence refused both");
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
    var pod = new Pod();
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
      .Because("the fence reaches exclusive work: an evicted instance is refused by the vote and stands down");
    await Assert.That(evictedAttempt.Refusal).IsEqualTo(DutyRefusal.Refused)
      .Because("issue #494: not a race — retrying cannot help, and the caller must be able to tell");
    await Assert.That(await _holdsAsync(pod.InstanceId, "maintainer", cancellationToken)).IsFalse();

    // And the duty is genuinely free: a live instance takes it immediately.
    var live = new Pod();
    await _joinFleetAsync(live, cancellationToken);
    await using var liveGrant = (await _electorFor(live).TryAcquireAsync("maintainer", cancellationToken)).Grant;
    await Assert.That(liveGrant).IsNotNull();
  }

  [Test]
  [Timeout(120000)]
  public async Task DirtyDeath_JudgedByTheLivenessRule_HandsTheDutyOver_AndTheGrantKnowsItIsLostAsync(CancellationToken cancellationToken) {
    // The OOMKill shape: the holder stops without a clean release. Its instance stops beating, the one liveness rule
    // calls it dead, the reap removes its row, and the next vote takes the duty at once (#2044): no lock is left to
    // free, and no lease has to run out first.
    var victim = new Pod();
    var successor = new Pod();
    await _joinFleetAsync(victim, cancellationToken);
    await _joinFleetAsync(successor, cancellationToken);
    var time = new FakeTimeProvider();
    var grant = (await _electorFor(victim, time).TryAcquireAsync("migrator", cancellationToken)).Grant;
    await Assert.That(grant).IsNotNull();
    await Assert.That((await _electorFor(successor).TryAcquireAsync("migrator", cancellationToken)).Refusal)
      .IsEqualTo(DutyRefusal.Contended).Because("the victim is live and holds the duty");

    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var die = conn.CreateCommand();
      die.CommandText = """
        UPDATE wh_service_instances SET last_heartbeat_at = now() - INTERVAL '1 hour' WHERE instance_id = @id;
        SELECT count(*) FROM cleanup_stale_instances(now());
        """;
      die.Parameters.AddWithValue("id", victim.InstanceId);
      _ = await die.ExecuteScalarAsync(cancellationToken);
    }

    await using var takeover = (await _electorFor(successor).TryAcquireAsync("migrator", cancellationToken)).Grant;
    await Assert.That(takeover).IsNotNull()
      .Because("the rule called the holder dead and the reap removed it, so the vote voids its assignment");
    await Assert.That(takeover!.PreviousHolderInstanceId).IsEqualTo(victim.InstanceId);
    await Assert.That(await _holdsAsync(successor.InstanceId, "migrator", cancellationToken)).IsTrue();
    await Assert.That(await _holdsAsync(victim.InstanceId, "migrator", cancellationToken)).IsFalse()
      .Because("the victim's recorded holding went with its row");
    time.Advance(new RoleAssignmentOptions().RenewInterval);
    await Assert.That(await grant!.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("fencing: a grant whose assignment was taken over must learn it before its next unit of exclusive work");

    await grant.DisposeAsync();
  }
}
