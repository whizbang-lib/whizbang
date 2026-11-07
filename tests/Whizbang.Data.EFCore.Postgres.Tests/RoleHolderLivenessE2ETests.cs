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
    await _heartbeatAsync(pod, ct);
    return pod;
  }

  private async Task _heartbeatAsync(Pod pod, CancellationToken ct) {
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
  }

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
    var oldWork = Task.Delay(Timeout.InfiniteTimeSpan, old.LostToken);

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
    var checks = System.Threading.Channels.Channel.CreateUnbounded<bool>();
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
}
