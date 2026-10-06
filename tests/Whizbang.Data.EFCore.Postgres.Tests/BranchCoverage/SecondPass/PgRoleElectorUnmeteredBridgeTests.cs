// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// A bridged voter built without metrics still ends a lapsed bridged holder's lock session and wins
/// the role. The metered case is <c>RoleAssignmentResilienceE2ETests</c> (same shard); this one takes
/// the path where there is no counter to increment, so ending the session must not depend on one.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgRoleElector.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class PgRoleElectorUnmeteredBridgeTests : EFCoreTestBase {
  private const string ROLE = StartupDuties.MAINTAINER;
  private static readonly RoleAssignmentOptions _defaults = new();

  [Test]
  [Timeout(120000)]
  public async Task ABridgedVoterWithoutMetrics_EndsALapsedBridgedHoldersSession_AndWinsAsync(CancellationToken cancellationToken) {
    var a = await _joinAsync(cancellationToken);
    var b = await _joinAsync(cancellationToken);
    var stuck = (await _electorFor(a).TryAcquireAsync(ROLE, cancellationToken)).Grant!;

    await _ageAsync(ROLE, _defaults.Lease + TimeSpan.FromSeconds(1), cancellationToken);
    var takeover = await _electorFor(b).TryAcquireAsync(ROLE, cancellationToken);

    await Assert.That(takeover.Grant).IsNotNull()
      .Because($"the stuck holder's session was ended, so the lock and the vote are free. Refusal: {takeover.Refusal} — {takeover.Detail}");
    await Assert.That(takeover.Grant!.Epoch).IsEqualTo(stuck.Epoch + 1);
    await Assert.That(await stuck.VerifyStillHeldAsync(cancellationToken)).IsFalse()
      .Because("the stuck holder's bridge session is gone, so it knows it lost the role");
    await takeover.Grant.DisposeAsync();
    await stuck.DisposeAsync();
  }

  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "role-svc";
    public string HostName => "role-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private static IConfiguration _config() => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  private WhizbangNotificationOptions _notification() => new() { DirectConnectionString = ConnectionString };

  private PgRoleElector _electorFor(Pod pod) =>
    new(
      Options.Create(_notification()),
      Options.Create(new RoleAssignmentOptions { HoldLegacySessionLock = true }),
      _config(),
      pod,
      new PgDutyElector(Options.Create(_notification()), _config(), pod, NullLogger<PgDutyElector>.Instance),
      NullLogger<PgRoleElector>.Instance,
      null,
      timeProvider: null,
      metrics: null);

  private async Task<Pod> _joinAsync(CancellationToken ct) {
    var pod = new Pod();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(pod.InstanceId, pod.ServiceName, pod.HostName, 1), ct);
    return pod;
  }

  private async Task _ageAsync(string role, TimeSpan by, CancellationToken ct) {
    await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      WITH a AS (
        UPDATE wh_role_assignments
           SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
               lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
         WHERE role = @role RETURNING 1)
      UPDATE wh_role_candidates SET last_voted_at = last_voted_at - @by WHERE role = @role
      """;
    cmd.Parameters.AddWithValue(nameof(by), by);
    cmd.Parameters.AddWithValue(nameof(role), role);
    await cmd.ExecuteNonQueryAsync(ct);
  }
}
