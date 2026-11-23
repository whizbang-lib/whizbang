// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;
using Whizbang.Data.Postgres.TestSupport;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper driver coordinates its instances as the EF Core driver does (#2559): role assignment holds every exclusive
/// duty on one instance, and the elected partition assigner publishes the map its claims present. Each simulated
/// instance is its own container over the same database, exactly as separate pods would be.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresCoordinationRegistration.cs</code-under-test>
[Category("Integration")]
public class DapperCoordinationTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noPerspectives = [];
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  private ServiceCollection _services(Guid instanceId, bool schemaSqlOverload = false) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(instanceId, "dapper", "dapper-host", 1));
    if (schemaSqlOverload) {
      services.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null);
    } else {
      services.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, _noPerspectives);
    }
    return services;
  }

  private async Task<ServiceProvider> _instanceAsync(CancellationToken cancellationToken) {
    var id = (Guid)TrackedGuid.New();
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await PublishedPartitionMap.RegisterAsync(conn, id, InstanceConnectionModes.POOLED, cancellationToken);
    }
    return _services(id).BuildServiceProvider();
  }

  private static Guid _idOf(IServiceProvider provider) => provider.GetRequiredService<IServiceInstanceProvider>().InstanceId;

  private async Task _executeAsync(string sql, CancellationToken cancellationToken, object? args = null) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    _ = await conn.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: cancellationToken));
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task AddWhizbangPostgres_RegistersRoleAssignmentAndTheElectedAssignerAsync(bool schemaSqlOverload) {
    await using var provider = _services((Guid)TrackedGuid.New(), schemaSqlOverload).BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IDutyElector>()).IsSameReferenceAs(provider.GetRequiredService<PgRoleElector>())
      .Because("an exclusive duty is held by one instance, not run on every one");
    await Assert.That(provider.GetRequiredService<IPartitionAssignmentSource>())
      .IsSameReferenceAs(provider.GetRequiredService<PartitionAssignmentCache>())
      .Because("claims present the published assignment, as the EF Core driver's do");
    await Assert.That(provider.GetRequiredService<IPartitionAssignmentStore>()).IsTypeOf<PgPartitionAssignmentStore>();
    var hosted = provider.GetServices<IHostedService>().ToList();
    await Assert.That(hosted.OfType<StartupStageGatedService<PartitionAssignerWorker>>().Single().Inner)
      .IsSameReferenceAs(provider.GetRequiredService<PartitionAssignerWorker>())
      .Because("the assigner starts at Coordinate, as it does on the EF Core driver");
    await Assert.That(hosted.OfType<StartupStageGatedService<DutyHolderWorker>>().Single().Inner)
      .IsSameReferenceAs(provider.GetRequiredService<DutyHolderWorker>());
    var roles = provider.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value;
    await Assert.That(roles.Manages(PartitionAssignerOptions.ROLE)).IsTrue();
    await Assert.That(roles.Manages(StartupDuties.MAINTAINER)).IsTrue();
    await Assert.That(provider.GetRequiredService<INotificationConnectionStringFallback>().GetConnectionString()).IsEqualTo(ConnectionString)
      .Because("with no notification configuration the coordination connects with the driver's own connection string");
  }

  [Test]
  [Timeout(120000)]
  public async Task TwoInstances_ExactlyOneHoldsTheMaintainerAndTheAssignerRoles_AndOnlyItDoesTheMaintainersWorkAsync(
      CancellationToken cancellationToken) {
    await using var a = await _instanceAsync(cancellationToken);
    await using var b = await _instanceAsync(cancellationToken);
    var holderA = a.GetRequiredService<DutyHolderWorker>();
    var holderB = b.GetRequiredService<DutyHolderWorker>();
    try {
      // Both instances contend at once: the real race, one pass each.
      await Task.WhenAll(holderA.RunOnceAsync(cancellationToken), holderB.RunOnceAsync(cancellationToken));

      await Assert.That(new[] { holderA, holderB }.Count(h => h.Holds(StartupDuties.MAINTAINER))).IsEqualTo(1)
        .Because("two instances, one maintainer: the duty is elected on Dapper as on EF Core");
      await Assert.That(new[] { holderA, holderB }.Count(h => h.Holds(PartitionAssignerOptions.ROLE))).IsEqualTo(1);
      await Assert.That(new IServiceProvider[] { a, b }.Count(MaintainerDuty.IsThisInstancesTurn)).IsEqualTo(1)
        .Because("the maintenance sweep and every other fleet-wide job runs on the holder alone");
    } finally {
      await holderA.ReleaseHeldAsync();
      await holderB.ReleaseHeldAsync();
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task TheRebuildGuardsDuty_IsHeldByOneInstanceAtATimeAsync(CancellationToken cancellationToken) {
    await using var a = await _instanceAsync(cancellationToken);
    await using var b = await _instanceAsync(cancellationToken);
    var duty = PerspectiveRebuildDuty.For("wh_per_dapper_guard_probe");

    var first = await a.GetRequiredService<IDutyElector>().TryAcquireAsync(duty, cancellationToken);
    await using var held = first.Grant;
    var second = await b.GetRequiredService<IDutyElector>().TryAcquireAsync(duty, cancellationToken);

    await Assert.That(held).IsNotNull();
    await Assert.That(second.Grant).IsNull();
    await Assert.That(second.Refusal).IsEqualTo(DutyRefusal.Contended)
      .Because("a second blue-green rebuild of the table on another instance is refused as in flight");
  }

  [Test]
  [Timeout(120000)]
  public async Task WhenTheMaintainerDies_TheSurvivorTakesTheDutyOverAsync(CancellationToken cancellationToken) {
    await using var a = await _instanceAsync(cancellationToken);
    await using var b = await _instanceAsync(cancellationToken);
    var holderA = a.GetRequiredService<DutyHolderWorker>();
    var holderB = b.GetRequiredService<DutyHolderWorker>();
    var roles = a.GetRequiredService<IOptions<RoleAssignmentOptions>>().Value;
    try {
      await holderA.RunOnceAsync(cancellationToken);
      await Assert.That(holderA.Holds(StartupDuties.MAINTAINER)).IsTrue();

      // A dies: no pass renews its role, and the database clock moves past the lease and the progress window. Every
      // instant is moved back rather than waited for, so the test reads no clock.
      var by = roles.Lease + roles.ProgressWindow + TimeSpan.FromSeconds(1);
      await _executeAsync("""
        UPDATE wh_role_assignments
           SET assigned_at = assigned_at - @by, renewed_at = renewed_at - @by,
               lease_expires_at = lease_expires_at - @by, last_vacated_at = last_vacated_at - @by
        """, cancellationToken, new { by });
      await _executeAsync("UPDATE wh_service_instances SET last_heartbeat_at = last_heartbeat_at - @by", cancellationToken, new { by });
      await _executeAsync("UPDATE wh_service_instances SET last_heartbeat_at = now() WHERE instance_id = @id", cancellationToken, new { id = _idOf(b) });

      await holderB.RunOnceAsync(cancellationToken);

      await Assert.That(holderB.Holds(StartupDuties.MAINTAINER)).IsTrue()
        .Because("the survivor takes over a duty whose holder stopped renewing");
      await Assert.That(MaintainerDuty.IsThisInstancesTurn(b)).IsTrue();
    } finally {
      await holderB.ReleaseHeldAsync();
      await holderA.ReleaseHeldAsync();
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task TwoInstances_OneIsElectedAssigner_AndTheOthersClaimPresentsItsMapAsync(CancellationToken cancellationToken) {
    await using var a = await _instanceAsync(cancellationToken);
    await using var b = await _instanceAsync(cancellationToken);
    var assignerA = a.GetRequiredService<PartitionAssignerWorker>();
    var assignerB = b.GetRequiredService<PartitionAssignerWorker>();
    var published = new TaskCompletionSource<(PartitionAssignerWorker Worker, PartitionAssignment Assignment)>(
      TaskCreationOptions.RunContinuationsAsynchronously);
    assignerA.OnPublished += assignment => published.TrySetResult((assignerA, assignment));
    assignerB.OnPublished += assignment => published.TrySetResult((assignerB, assignment));

    await assignerA.StartAsync(cancellationToken);
    await assignerB.StartAsync(cancellationToken);
    try {
      var (assigner, assignment) = await published.Task.WaitAsync(_signalDeadline, cancellationToken);
      await Assert.That(assignment.Members.Order()).IsEquivalentTo(new[] { _idOf(a), _idOf(b) }.Order())
        .Because("the elected assigner publishes every live instance");

      // The other instance's claim presents the published version, and the database accepts it as current.
      var claimer = assigner == assignerA ? b : a;
      var version = await claimer.GetRequiredService<IPartitionAssignmentSource>().ForClaimAsync(_idOf(claimer), cancellationToken);
      await Assert.That(version).IsEqualTo(assignment.Version);
      await _executeAsync("""
        INSERT INTO wh_perspective_events
          (event_work_id, stream_id, perspective_name, event_id, instance_id, lease_expiry, partition_number, status, attempts, created_at)
        VALUES (gen_random_uuid(), gen_random_uuid(), 'TestPerspective', gen_random_uuid(), NULL, NULL, 0, 0, 0, NOW())
        """, cancellationToken);
      var batch = await claimer.GetRequiredService<IWorkCoordinator>().ClaimWorkAsync(new ClaimWorkRequest(
        _idOf(claimer), "dapper", "dapper-host", 1, MaxStreams: 50, PartitionCount: 100, LeaseSeconds: 300,
        PartitionAssignment: version), cancellationToken);
      await Assert.That(batch.PartitionAssignmentStale).IsFalse()
        .Because("the claim ranks by the published map rather than by itself");
    } finally {
      await assignerA.StopAsync(CancellationToken.None);
      await assignerB.StopAsync(CancellationToken.None);
    }
  }
}
