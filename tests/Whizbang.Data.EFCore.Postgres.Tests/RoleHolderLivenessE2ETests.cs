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
/// #2140 through the real elector: a role holder is judged by the one liveness rule, so a freshly elected holder busy
/// with a slow startup keeps its role; a connected but stuck holder loses it at its progress window; and the old holder
/// learns of a loss from its grant's watch within one renew interval, with no verify of its own, so its exclusive work
/// stops before it could overlap another holder's.
/// </summary>
/// <remarks>
/// Each simulated instance is its own elector over its own connections. Database time is advanced by shifting stored
/// instants back; the grant's renew throttle and its watch run on a fake clock, and the old holder's loss is awaited on
/// the grant's own <see cref="IDutyGrant.LostToken"/>, so nothing sleeps or polls.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/355_RoleHoldersAreJudgedByTheLivenessRule.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class RoleHolderLivenessE2ETests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;
  private static readonly RoleAssignmentOptions _defaults = new();

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "role-svc";
    public string HostName => "role-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private PgRoleElector _electorFor(Pod pod, TimeProvider time, RoleAssignmentOptions? options = null) => new(
    Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
    Options.Create(options ?? new RoleAssignmentOptions()),
    new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
    pod,
    NullLogger<PgRoleElector>.Instance,
    libraryVersion: null,
    timeProvider: time);

  private async Task<Pod> _joinAsync(CancellationToken ct) {
    var pod = new Pod();
    await _registerAsync(pod, ct);
    return pod;
  }

  private async Task _registerAsync(Pod pod, CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
  }

  /// <summary>
  /// The pod's heartbeat, written straight to its row: a recorded beat would also reap the peers the rule calls dead,
  /// which these tests keep registered.
  /// </summary>
  private Task _heartbeatAsync(Pod pod, CancellationToken ct) =>
    _executeAsync("UPDATE wh_service_instances SET last_heartbeat_at = now() WHERE instance_id = @id", ct, ("id", pod.InstanceId));

  private async Task _executeAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Moves the database clock forward by <paramref name="by"/>: the role's instants and every heartbeat age.</summary>
  private async Task _ageAsync(TimeSpan by, CancellationToken ct) {
    await _executeAsync("""
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
             lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
       WHERE role = @role
      """, ct, ("by", by), ("role", ROLE));
    await _executeAsync("UPDATE wh_service_instances SET last_heartbeat_at = last_heartbeat_at - @by", ct, ("by", by));
  }

  /// <summary>
  /// Completes, canceled, the moment <paramref name="token"/> is canceled: work that stands still until the code under
  /// test cancels it, waiting on that exact transition rather than on any clock.
  /// </summary>
  private static Task _untilCanceledAsync(CancellationToken token) {
    var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = token.Register(() => canceled.TrySetCanceled(token));
    return canceled.Task;
  }

  private static Task _lostAsync(IDutyGrant grant) {
    var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = grant.LostToken.Register(() => lost.TrySetResult());
    return lost.Task;
  }

  [Test]
  [Timeout(120000)]
  public async Task ARestartWithASlowStartup_KeepsOneHolder_AndTheHoldersLateVerifyRenewsItAsync(CancellationToken cancellationToken) {
    // The holder wins the role, then spends 40 seconds in startup without verifying (past its 15 second lease). It keeps
    // heartbeating, so the rule calls it live: the other instance is refused, and the holder's late verify renews.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var holder = (await _electorFor(a, time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await _ageAsync(TimeSpan.FromSeconds(40), cancellationToken);
    await _heartbeatAsync(a, cancellationToken);
    await _heartbeatAsync(b, cancellationToken);
    var other = await _electorFor(b, new FakeTimeProvider()).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(other.Refusal).IsEqualTo(DutyRefusal.Contended)
      .Because("a live holder that made progress inside its window keeps its role through a slow startup");
    time.Advance(_defaults.RenewInterval);
    await Assert.That(await holder.VerifyStillHeldAsync(cancellationToken)).IsTrue();
    await Assert.That(holder.LostToken.IsCancellationRequested).IsFalse();
    await holder.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task AConnectedButStuckHolder_LosesTheRoleAtItsProgressWindow_AndLearnsItWithinOneRenewIntervalAsync(
      CancellationToken cancellationToken) {
    // The holder keeps heartbeating but its work loop never verifies again. Past its progress window the role goes to
    // the other instance, and the stuck holder's grant watch tells it so on its next tick, without any verify.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var stuck = (await _electorFor(a, time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var lost = _lostAsync(stuck);

    await _ageAsync(_defaults.ProgressWindow + TimeSpan.FromSeconds(1), cancellationToken);
    await _heartbeatAsync(a, cancellationToken);
    await _heartbeatAsync(b, cancellationToken);
    var next = (await _electorFor(b, new FakeTimeProvider()).TryAcquireAsync(ROLE, cancellationToken)).Grant;
    await Assert.That(next).IsNotNull().Because("a stuck holder loses the role within its progress window");
    await Assert.That(lost.IsCompleted).IsFalse().Because("the watch has not ticked yet");

    time.Advance(_defaults.RenewInterval);
    await lost.WaitAsync(cancellationToken);

    await Assert.That(await stuck.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await next!.DisposeAsync();
    await stuck.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task TheOldHolder_ObservesItsLossWithinOneRenewInterval_SoExclusiveWorkNeverOverlapsAsync(
      CancellationToken cancellationToken) {
    // The holder stops heartbeating (it crashed for the rule's purposes) and another instance takes the role over. The
    // old grant's watch observes the loss on its next tick; work that obeys the grant's token stops there.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var old = (await _electorFor(a, time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var lost = _lostAsync(old);
    var oldWork = _untilCanceledAsync(old.LostToken);

    await _ageAsync(TimeSpan.FromSeconds(45), cancellationToken);
    await _heartbeatAsync(b, cancellationToken);
    var current = (await _electorFor(b, new FakeTimeProvider()).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    time.Advance(_defaults.RenewInterval);
    await lost.WaitAsync(cancellationToken);

    await Assert.That(async () => await oldWork).Throws<OperationCanceledException>()
      .Because("the old holder's exclusive work is canceled by its grant's loss");
    await Assert.That(current.LostToken.IsCancellationRequested).IsFalse();
    await current.DisposeAsync();
    await old.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task AGrantWhoseWatchCannotConfirmTheRoleForALease_FencesItselfAsync(CancellationToken cancellationToken) {
    // The database is unreachable from the holder: its watch cannot confirm the role. After one lease without a
    // confirmation it stops its exclusive work, because another instance may by then be entitled to the role.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a, time);
    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var lost = _lostAsync(grant);
    var checks = System.Threading.Channels.Channel.CreateUnbounded<bool?>();
    elector.WatchChecked += (_, confirmed) => checks.Writer.TryWrite(confirmed);
    await _executeAsync("""
      CREATE OR REPLACE FUNCTION wh_role_is_held(p_role TEXT, p_instance_id UUID, p_epoch BIGINT) RETURNS BOOLEAN
      LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'the database cannot answer' USING ERRCODE = '08006'; END $$
      """, cancellationToken);

    for (var tick = 0; tick < _defaults.MissedRenewalsBeforeLapse - 1; tick++) {
      time.Advance(_defaults.RenewInterval);
      await Assert.That(await checks.Reader.ReadAsync(cancellationToken)).IsFalse();
    }
    await Assert.That(lost.IsCompleted).IsFalse().Because("inside one lease an unconfirmed watch keeps the role");
    time.Advance(_defaults.RenewInterval);
    await lost.WaitAsync(cancellationToken);

    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsFalse();
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task AReleasedGrant_EndsItsWatch_AndCancelsItsTokenAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var grant = (await _electorFor(a, time).TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var lost = _lostAsync(grant);

    await grant.DisposeAsync();
    await lost.WaitAsync(cancellationToken);

    time.Advance(_defaults.RenewInterval);
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsFalse();
  }

  [Test]
  [Timeout(120000)]
  public async Task WithTheProgressWindowOff_AHolderLapsesAtItsLease_AsBeforeAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var off = new RoleAssignmentOptions { ProgressWindow = TimeSpan.Zero };
    var holder = (await _electorFor(a, new FakeTimeProvider(), off).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await _ageAsync(off.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    await _heartbeatAsync(a, cancellationToken);
    var next = await _electorFor(b, new FakeTimeProvider(), off).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(next.Grant).IsNotNull();
    await next.Grant!.DisposeAsync();
    await holder.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task TheVoteManyPath_DeclaresTheWindowForEveryRoleItWinsAsync(CancellationToken cancellationToken) {
    const string other = "liveness-other-role";
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var attempts = await _electorFor(a, new FakeTimeProvider()).TryAcquireManyAsync([ROLE, other], cancellationToken);
    await Assert.That(attempts.All(x => x.Grant is not null)).IsTrue();

    await _ageAsync(TimeSpan.FromSeconds(40), cancellationToken);
    await _executeAsync("""
      UPDATE wh_role_assignments
         SET renewed_at = renewed_at - INTERVAL '40 seconds', lease_expires_at = lease_expires_at - INTERVAL '40 seconds'
       WHERE role = @role
      """, cancellationToken, ("role", other));
    await _heartbeatAsync(a, cancellationToken);
    var contested = await _electorFor(b, new FakeTimeProvider()).TryAcquireManyAsync([ROLE, other], cancellationToken);

    await Assert.That(contested.All(x => x.Refusal == DutyRefusal.Contended)).IsTrue();
    foreach (var attempt in attempts) {
      await attempt.Grant!.DisposeAsync();
    }
  }
  [Test]
  [Timeout(120000)]
  public async Task OnASchemaWithoutTheWatch_TheGrantKeepsItsRole_AndStopsWatchingAsync(CancellationToken cancellationToken) {
    // A database that has not applied 355 has no wh_role_is_held: the grant is judged by its lease, as before, and its
    // watch stops instead of fencing on a check that can never succeed.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a, time);
    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var checks = System.Threading.Channels.Channel.CreateUnbounded<bool?>();
    elector.WatchChecked += (_, confirmed) => checks.Writer.TryWrite(confirmed);
    await _executeAsync("SELECT drop_all_overloads('wh_role_is_held')", cancellationToken);

    time.Advance(_defaults.RenewInterval);
    await Assert.That(await checks.Reader.ReadAsync(cancellationToken)).IsNull();
    for (var tick = 0; tick < _defaults.MissedRenewalsBeforeLapse; tick++) {
      time.Advance(_defaults.RenewInterval);
    }

    await Assert.That(checks.Reader.TryRead(out _)).IsFalse().Because("the watch stopped after the schema said it has none");
    await Assert.That(grant.LostToken.IsCancellationRequested).IsFalse();
    await grant.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task AWatchCheck_ConfirmsTheRole_AndADeclarationThatFails_LeavesTheGrantHeldAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    await _executeAsync("SELECT drop_all_overloads('wh_declare_role_progress_window')", cancellationToken);
    var elector = _electorFor(a, time);
    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var checks = System.Threading.Channels.Channel.CreateUnbounded<bool?>();
    elector.WatchChecked += (_, confirmed) => checks.Writer.TryWrite(confirmed);

    time.Advance(_defaults.RenewInterval);

    await Assert.That(await checks.Reader.ReadAsync(cancellationToken)).IsTrue();
    await Assert.That(grant.LostToken.IsCancellationRequested).IsFalse();
    await grant.DisposeAsync();
  }
  [Test]
  [Timeout(120000)]
  public async Task AWatchTickBehindARelease_FindsTheTenureEnded_AndAsksNothingAsync(CancellationToken cancellationToken) {
    // The tick fires while the release holds the tenure: it waits for it, sees the role was let go, and asks the
    // database nothing, so a released grant is never reported lost by its own watch.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a, time);
    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var checks = System.Threading.Channels.Channel.CreateUnbounded<bool?>();
    elector.WatchChecked += (_, confirmed) => checks.Writer.TryWrite(confirmed);

    var release = grant.DisposeAsync().AsTask();
    time.Advance(_defaults.RenewInterval);
    await release;

    // The release's round trip to the database yields, so the tick fired while the release held the tenure.
    await Assert.That(await checks.Reader.ReadAsync(cancellationToken)).IsNull();
    await Assert.That(grant.LostToken.IsCancellationRequested).IsTrue();
  }
  [Test]
  [Timeout(120000)]
  public async Task ATickWhileTheLastCheckIsStillRunning_Skips_SoChecksNeverPileUpAsync(CancellationToken cancellationToken) {
    // Two renew intervals pass at once: the second tick finds the first check still waiting on the database and skips.
    // The first check is HELD at the database by a lock on the table it reads, so it is still running when the second
    // tick fires, by construction. Without the hold, a runner that preempted the test thread between the two timer
    // callbacks let the first check finish in between, and the second tick then ran a full check of its own.
    var time = new FakeTimeProvider();
    var a = await _joinAsync(cancellationToken);
    var elector = _electorFor(a, time);
    var grant = (await elector.TryAcquireAsync(ROLE, cancellationToken)).Grant!;
    var checks = System.Threading.Channels.Channel.CreateUnbounded<bool?>();
    elector.WatchChecked += (_, confirmed) => checks.Writer.TryWrite(confirmed);

    await using var hold = new NpgsqlConnection(ConnectionString);
    await hold.OpenAsync(cancellationToken);
    await using var holdTx = await hold.BeginTransactionAsync(cancellationToken);
    await using (var lockCmd = new NpgsqlCommand("LOCK TABLE wh_role_assignments IN ACCESS EXCLUSIVE MODE", hold, holdTx)) {
      await lockCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    time.Advance(_defaults.RenewInterval * 2);

    // Advance runs both timer callbacks on this thread before it returns. The first marked its check running before it
    // first yielded, and that check cannot finish while the table is locked, so the second tick found it running and
    // skipped. Only now is the first check let through.
    await holdTx.RollbackAsync(cancellationToken);

    await Assert.That(await checks.Reader.ReadAsync(cancellationToken)).IsTrue();
    await Assert.That(checks.Reader.TryRead(out _)).IsFalse().Because("the overlapping tick asked nothing and reported nothing");
    await grant.DisposeAsync();
  }
}
