// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// A migrator that holds its role through a phase longer than its lease keeps the role (#2127): the keepalive renews
/// the assignment in database time while nothing else in the phase could.
/// </summary>
/// <remarks>
/// <para>
/// The lease is judged on the database clock, so the phase's length is played out there: each step ages the
/// assignment row by one tick (what the database clock would have done) and then advances the elector's and the
/// keepalive's fake clock by the same tick. The keepalive's verify is awaited on its own signal, so nothing here
/// depends on how long anything takes.
/// </para>
/// <para>
/// The second test is the control: the same phase, with the grant held and nothing renewing it, loses the role, and
/// the renewal that comes afterwards is refused. Without it, the first test could pass on a lease that never lapses.
/// Since #2140 a live holder keeps its role past the lease while it renewed inside its progress window, so the window
/// here is shorter than the phase: the phase outlasts the lease and the window, and only renewal keeps the role.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorDuty.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/DutyGrantKeepalive.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
[Category("Integration")]
[Category("Shard3")]
public class MigratorDutyLeaseTests : EFCoreTestBase {
  private static readonly TimeSpan _renewInterval = TimeSpan.FromSeconds(4);
  private static readonly TimeSpan _lease = TimeSpan.FromSeconds(8);
  private static readonly TimeSpan _tick = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan _progressWindow = TimeSpan.FromSeconds(12);

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "lease-svc";
    public string HostName => "lease-host";
    public int ProcessId => 11;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>The real grant, reporting each verify's answer once the database has given it.</summary>
  private sealed class SignalingGrant(IDutyGrant inner) : IDutyGrant {
    private readonly Channel<bool> _answers = Channel.CreateUnbounded<bool>();
    public string Duty => inner.Duty;
    public DateTimeOffset AcquiredAt => inner.AcquiredAt;
    public TimeSpan? RenewInterval => inner.RenewInterval;
    public async Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) {
      var held = await inner.VerifyStillHeldAsync(cancellationToken);
      _answers.Writer.TryWrite(held);
      return held;
    }
    public ValueTask<bool> NextAnswerAsync(CancellationToken cancellationToken) => _answers.Reader.ReadAsync(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
  }

  /// <summary>The real elector, handing out signaling grants.</summary>
  private sealed class SignalingElector(PgRoleElector inner) : IDutyElector {
    public SignalingGrant? Granted { get; private set; }
    public async Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      var attempt = await inner.TryAcquireAsync(duty, cancellationToken);
      if (attempt.Grant is { } grant) {
        Granted = new SignalingGrant(grant);
        return DutyAttempt.Granted(Granted);
      }
      return attempt;
    }
  }

  private async Task<PgRoleElector> _electorAsync(Pod pod, TimeProvider time, CancellationToken ct) {
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(ct);
      await using var register = conn.CreateCommand();
      register.CommandText = "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at) "
        + "VALUES (@id, 'lease-svc', 'lease-host', 11, now(), now())";
      register.Parameters.AddWithValue("id", pod.InstanceId);
      await register.ExecuteNonQueryAsync(ct);
    }
    // Past its lease, a live holder keeps the role while it renewed inside its progress window (#2140), so the window
    // is set shorter than the phase: without it, the phase would have to outlast the 2-minute default before the
    // control lost anything, and the first test could pass with no keepalive at all.
    var roles = new RoleAssignmentOptions {
      RenewInterval = _renewInterval,
      MissedRenewalsBeforeLapse = 2,
      ProgressWindow = _progressWindow,
    };
    roles.RoleLeases[StartupDuties.MIGRATOR] = _lease;
    return new PgRoleElector(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      Options.Create(roles),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      pod,
      NullLogger<PgRoleElector>.Instance,
      libraryVersion: null,
      timeProvider: time);
  }

  /// <summary>The database clock moves on by <paramref name="by"/> for the migrator's assignment.</summary>
  private async Task _ageAsync(TimeSpan by, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      UPDATE wh_role_assignments
         SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by, lease_expires_at = lease_expires_at - @by
       WHERE role = @role
      """;
    cmd.Parameters.AddWithValue(nameof(by), by);
    cmd.Parameters.AddWithValue("role", StartupDuties.MIGRATOR);
    await cmd.ExecuteNonQueryAsync(ct);
  }

  /// <summary>Whether <paramref name="pod"/> holds the migrator assignment with lease left, judged by the database.</summary>
  private async Task<bool> _holdsAsync(Pod pod, CancellationToken ct) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM wh_role_assignments WHERE role = @role AND holder_instance_id = @id "
      + "AND lease_expires_at > now())";
    cmd.Parameters.AddWithValue("role", StartupDuties.MIGRATOR);
    cmd.Parameters.AddWithValue("id", pod.InstanceId);
    return await cmd.ExecuteScalarAsync(ct) is true;
  }

  [Test]
  [Timeout(120000)]
  public async Task AMigrator_HoldingAPhaseLongerThanItsLease_StillHoldsTheRoleAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    var time = new FakeTimeProvider();
    var elector = new SignalingElector(await _electorAsync(pod, time, cancellationToken));
    var staging = await MigratorDutyStaging.ElectAsync(elector, "public", null, cancellationToken);
    await Assert.That(staging.Stage).IsEqualTo(SchemaStage.Migrator);
    await using var duty = new MigratorDuty(time);
    await duty.HoldAsync(staging);
    var grant = elector.Granted!;

    // Three leases' worth of one phase, with nothing in it but the keepalive.
    for (var elapsed = TimeSpan.Zero; elapsed < _lease * 3; elapsed += _tick) {
      await _ageAsync(_tick, cancellationToken);
      time.Advance(_tick);
      await Assert.That(await grant.NextAnswerAsync(cancellationToken)).IsTrue()
        .Because("every renewal lands while the lease still stands, so none is refused");
    }

    await Assert.That(await _holdsAsync(pod, cancellationToken)).IsTrue()
      .Because("a phase three times the lease long did not cost the migrator its role");
  }

  [Test]
  [Timeout(120000)]
  public async Task Control_TheSamePhaseWithNothingRenewing_LosesTheRoleAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    var time = new FakeTimeProvider();
    var elector = new SignalingElector(await _electorAsync(pod, time, cancellationToken));
    var staging = await MigratorDutyStaging.ElectAsync(elector, "public", null, cancellationToken);
    await using var grant = staging.Grant!;

    for (var elapsed = TimeSpan.Zero; elapsed < _lease * 3; elapsed += _tick) {
      await _ageAsync(_tick, cancellationToken);
      time.Advance(_tick);
    }

    await Assert.That(_lease * 3).IsGreaterThan(_progressWindow)
      .Because("the phase outlasts the progress window too, so the one liveness rule does not keep the role either");
    await Assert.That(await _holdsAsync(pod, cancellationToken)).IsFalse();
    await Assert.That(await grant.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("a lapsed lease is refused renewal even though nobody took it: the defect the keepalive prevents");
  }

  [Test]
  [Timeout(120000)]
  public async Task ARoleAssignmentGrant_ReportsItsRoleRenewIntervalAsync(CancellationToken cancellationToken) {
    var pod = new Pod();
    var elector = await _electorAsync(pod, new FakeTimeProvider(), cancellationToken);

    var attempt = await elector.TryAcquireAsync(StartupDuties.MIGRATOR, cancellationToken);
    await using var grant = attempt.Grant!;

    await Assert.That(grant.RenewInterval).IsEqualTo(_renewInterval);
  }
}
