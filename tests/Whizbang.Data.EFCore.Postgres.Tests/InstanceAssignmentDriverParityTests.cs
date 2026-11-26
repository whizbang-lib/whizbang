// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2595: both Postgres drivers read each instance's assignment through the one shared reader, so the fleet section and
/// the assignment reader answer the same on either driver. The Dapper driver had no fleet status source at all; it now
/// registers one, and this test fails the moment the two drivers' answers over one database differ.
/// </summary>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresFleetStatus.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupFleetStatusSource.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperPostgresStartupFleetStatusSource.cs</code-under-test>
[Category("Integration")]
[Category("Shard3")]
public class InstanceAssignmentDriverParityTests : EFCoreTestBase {
  [Test]
  [Timeout(120000)]
  public async Task BothDrivers_RegisterTheFleetSourceAndTheAssignmentReader_AndAnswerAlikeAsync(CancellationToken cancellationToken) {
    var (worker, holder, waiting) = await _seedAsync(cancellationToken);
    await using var efCore = _efCore();
    await using var dapper = _dapper();

    var efFleet = await efCore.GetRequiredService<IStartupFleetStatusSource>().GetFleetAsync(cancellationToken);
    var dapperFleet = await dapper.GetRequiredService<IStartupFleetStatusSource>().GetFleetAsync(cancellationToken);
    var efAssignments = await efCore.GetRequiredService<IInstanceAssignmentReader>().ReadAssignmentsAsync(cancellationToken);
    var dapperAssignments = await dapper.GetRequiredService<IInstanceAssignmentReader>().ReadAssignmentsAsync(cancellationToken);

    await Assert.That(_describe(dapperFleet)).IsEquivalentTo(_describe(efFleet));
    await Assert.That(_describe(dapperAssignments)).IsEquivalentTo(_describe(efAssignments));
    var byId = efAssignments.ToDictionary(a => a.InstanceId);
    await Assert.That(byId[worker].Kind).IsEqualTo(InstanceAssignmentKind.StandardWorker);
    await Assert.That(byId[worker].OwnedPartitions).IsEqualTo(6000);
    await Assert.That(byId[holder].Kind).IsEqualTo(InstanceAssignmentKind.RoleHolder);
    await Assert.That(byId[holder].Roles).IsEquivalentTo([PartitionAssignerOptions.ROLE]);
    await Assert.That(byId[waiting].Kind).IsEqualTo(InstanceAssignmentKind.AwaitingShare);
    var fleetRow = dapperFleet.Single(r => r.InstanceId == holder);
    await Assert.That(fleetRow.Assignment).IsEqualTo(InstanceAssignmentKind.RoleHolder);
    await Assert.That(fleetRow.OwnedPartitions).IsEqualTo(4000);
    await Assert.That(fleetRow.Capabilities).Contains(PartitionAssignerOptions.ROLE);
    await Assert.That(byId[waiting].LibraryVersion).IsNull();
    await Assert.That(byId[worker].LibraryVersion).IsEqualTo("1.2.3");
    await Assert.That(byId[worker].MapRevision).IsNull().Because("no map is published yet");

    // A published map's revision is part of every row, and a Dapper driver whose connection string names the schema
    // reads it there.
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        INSERT INTO wh_published_partition_assignment
          (singleton, role, epoch, revision, assigner_instance_id, members, published_at, lease, lease_expires_at)
        VALUES (TRUE, 'partition-assigner', 1, 12, @holder, ARRAY[@worker, @holder]::uuid[], now(), interval '1 minute',
                now() + interval '1 minute')
        """;
      cmd.Parameters.AddWithValue("holder", holder);
      cmd.Parameters.AddWithValue("worker", worker);
      _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var named = _dapper(ConnectionString + ";Search Path=public");
    var revised = await named.GetRequiredService<IInstanceAssignmentReader>().ReadAssignmentsAsync(cancellationToken);
    await Assert.That(revised.Single(a => a.InstanceId == worker).MapRevision).IsEqualTo(12L);
    await Assert.That(_describe(await named.GetRequiredService<IStartupFleetStatusSource>().GetFleetAsync(cancellationToken)))
      .IsEquivalentTo(_describe(await efCore.GetRequiredService<IStartupFleetStatusSource>().GetFleetAsync(cancellationToken)));
  }

  [Test]
  [Timeout(120000)]
  public async Task BothDrivers_RegisterTheMonitorAndTheAssignmentHealthComponent_ThatReadTheInstancesOwnRowAsync(
      CancellationToken cancellationToken) {
    var (worker, _, _) = await _seedAsync(cancellationToken);
    var self = new FixedInstance(worker);
    await using var efCore = _efCore(self);
    await using var dapper = _dapper(instance: self);

    foreach (var provider in new[] { efCore, dapper }) {
      var monitor = provider.GetRequiredService<InstanceAssignmentMonitor>();
      var observation = await monitor.ObserveAsync(cancellationToken);
      await Assert.That(observation.Failure).IsNull();
      await Assert.That(observation.Assignment!.Kind).IsEqualTo(InstanceAssignmentKind.StandardWorker);
      var report = await new Whizbang.Core.Health.InstanceAssignmentHealthSource(monitor).ReportAsync(cancellationToken);
      await Assert.That(report.State).IsEqualTo(Whizbang.Core.Health.ComponentState.Operational);
      await Assert.That(report.Detail).IsEqualTo("Assigned: standard worker, 6000 partitions, no map published");
    }

    // The monitor is hosted from the Coordinate stage on, by the registration both drivers share.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider>(self);
    services.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, []);
    await using var composed = services.BuildServiceProvider();
    var hosted = services.Single(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
      && d.ImplementationFactory?.Method.ReturnType == typeof(StartupStageGatedService<InstanceAssignmentMonitor>));
    await Assert.That(hosted.ImplementationFactory!(composed)).IsTypeOf<StartupStageGatedService<InstanceAssignmentMonitor>>();
    // The assignment component is registered by the same shared registration, over the same monitor.
    var component = services.Single(d => d.ServiceType == typeof(Whizbang.Core.Health.IWhizbangHealthSource)
      && d.ImplementationFactory?.Method.ReturnType == typeof(Whizbang.Core.Health.InstanceAssignmentHealthSource));
    var source = (Whizbang.Core.Health.IWhizbangHealthSource)component.ImplementationFactory!(composed);
    await Assert.That(source.Component).IsEqualTo("assignment");
    await Assert.That((await source.ReportAsync(cancellationToken)).Detail).IsEqualTo("not yet observed")
      .Because("before the monitor's first read the component says it has not read yet, and is not degraded for it");
  }

  [Test]
  public async Task TheOptions_BindFromConfiguration_AndKeepTheirDefaultsWithoutItAsync() {
    var bound = new InstanceAssignmentOptions();
    new Whizbang.Data.Postgres.Notifications.ConfigureInstanceAssignmentOptionsFromConfiguration(new ConfigurationBuilder()
      .AddInMemoryCollection([
        new("Whizbang:Database:InstanceAssignment:ObserveInterval", "00:00:10"),
        new("Whizbang:Database:InstanceAssignment:AwaitingShareGrace", "00:02:00"),
      ]).Build()).Configure(bound);
    await Assert.That(bound.ObserveInterval).IsEqualTo(TimeSpan.FromSeconds(10));
    await Assert.That(bound.AwaitingShareGrace).IsEqualTo(TimeSpan.FromMinutes(2));

    var defaults = new InstanceAssignmentOptions();
    new Whizbang.Data.Postgres.Notifications.ConfigureInstanceAssignmentOptionsFromConfiguration(new ConfigurationBuilder()
      .AddInMemoryCollection([new("Whizbang:Database:InstanceAssignment:ObserveInterval", "soon")]).Build()).Configure(defaults);
    await Assert.That(defaults.ObserveInterval).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(defaults.AwaitingShareGrace).IsEqualTo(TimeSpan.Zero);
  }

  private sealed class FixedInstance(Guid id) : IServiceInstanceProvider {
    public Guid InstanceId => id;
    public string ServiceName => "parity-svc";
    public string HostName => "parity-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = id, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  [Test]
  public async Task KindOf_ReadsEveryNameTheFunctionWrites_AndRefusesAnUnknownOneAsync() {
    await Assert.That(Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("starting")).IsEqualTo(InstanceAssignmentKind.Starting);
    await Assert.That(Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("standard-worker")).IsEqualTo(InstanceAssignmentKind.StandardWorker);
    await Assert.That(Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("role-holder")).IsEqualTo(InstanceAssignmentKind.RoleHolder);
    await Assert.That(Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("awaiting-share")).IsEqualTo(InstanceAssignmentKind.AwaitingShare);
    await Assert.That(Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("draining")).IsEqualTo(InstanceAssignmentKind.Draining);
    await Assert.That(Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("ineligible")).IsEqualTo(InstanceAssignmentKind.Ineligible);
    await Assert.That(Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("evicted")).IsEqualTo(InstanceAssignmentKind.Evicted);
    await Assert.That(() => Whizbang.Data.Postgres.PostgresFleetStatus.KindOf("something-new")).Throws<InvalidOperationException>()
      .Because("a kind a newer release writes must not read as a known one");
  }

  /// <summary>
  /// A name only near a known one is still unknown: the reader matches each name exactly, so a kind a newer release
  /// writes (or a name with a typo, a different case, or a known name's length) never reads as one this release knows.
  /// Each row is a different way the match can miss: no name at all, a length no kind has, the length of one or two
  /// kinds with any other text, and every kind's own name with one character changed.
  /// </summary>
  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("standard-worker-pool")]
  [Arguments("nine-char")]
  [Arguments("twelve-chars")]
  [Arguments("thirteen-char")]
  [Arguments("xtarting")]
  [Arguments("startinG")]
  [Arguments("drainin_")]
  [Arguments("Standard-worker")]
  [Arguments("role-holdeR")]
  [Arguments("awaiting-sharE")]
  [Arguments("ineligiblE")]
  [Arguments("evicteD")]
  public async Task KindOf_ANameNoKindHasExactly_IsRefusedAsync(string? name) {
    await Assert.That(() => Whizbang.Data.Postgres.PostgresFleetStatus.KindOf(name!)).Throws<InvalidOperationException>()
      .Because($"'{name}' is not exactly a name wh_instance_assignments() writes, so it must not read as a known kind");
  }

  [Test]
  [Timeout(120000)]
  public async Task TheFleet_BeforeTheAssignmentFunctionExists_StillReads_WithNoAssignmentAsync(CancellationToken cancellationToken) {
    // The stand-by handshake reads the fleet in Preflight, ahead of this release's main pass, so the fleet read cannot
    // depend on a function that pass creates.
    var (worker, _, _) = await _seedAsync(cancellationToken);
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "DROP FUNCTION wh_instance_assignments()";
      _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var dapper = _dapper();

    var row = (await dapper.GetRequiredService<IStartupFleetStatusSource>().GetFleetAsync(cancellationToken)).Single(r => r.InstanceId == worker);

    await Assert.That(row.Assignment).IsNull();
    await Assert.That(row.OwnedPartitions).IsNull();
    await Assert.That(row.IsLive).IsTrue();
  }

  private async Task<(Guid Worker, Guid Holder, Guid Waiting)> _seedAsync(CancellationToken ct) {
    var worker = (Guid)TrackedGuid.New();
    var holder = (Guid)TrackedGuid.New();
    var waiting = (Guid)TrackedGuid.New();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_ownership_mode', 'map', 'string');
      INSERT INTO wh_service_instances
        (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, connection_mode,
         liveness_window_seconds, startup_stage, lifecycle_phase, metadata, library_version)
      SELECT id, 'parity-svc', 'parity-host', 1, NOW(), NOW() - INTERVAL '1 hour', 'pooled', 30, 70, 'Running',
             '{"partition_ownership_level": 1}'::jsonb, CASE WHEN id = @waiting THEN NULL ELSE '1.2.3' END
        FROM unnest(ARRAY[@worker, @holder, @waiting]::uuid[]) id;
      INSERT INTO wh_partition_ownership (partition_number, owner_instance_id, epoch)
      SELECT g, CASE WHEN g < 6000 THEN @worker ELSE @holder END, 1 FROM generate_series(0, 9999) g;
      SELECT outcome FROM wh_vote_role('partition-assigner', @holder, interval '15 seconds', interval '15 seconds', NULL, NULL, NULL);
      """;
    cmd.Parameters.AddWithValue(nameof(worker), worker);
    cmd.Parameters.AddWithValue(nameof(holder), holder);
    cmd.Parameters.AddWithValue(nameof(waiting), waiting);
    _ = await cmd.ExecuteNonQueryAsync(ct);
    return (worker, holder, waiting);
  }

  private ServiceProvider _efCore(IServiceInstanceProvider? instance = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    if (instance is null) {
      services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    } else {
      services.AddSingleton(instance);
    }
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(ConnectionString));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    return services.BuildServiceProvider();
  }

  private ServiceProvider _dapper(string? connectionString = null, IServiceInstanceProvider? instance = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    if (instance is not null) {
      services.AddSingleton(instance);
    }
    services.AddWhizbangPostgres(connectionString ?? ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, []);
    return services.BuildServiceProvider();
  }

  private static List<string> _describe(IEnumerable<FleetInstanceStatus> rows) =>
    [.. rows.Select(r => $"{r.InstanceId} {r.ServiceName} {r.HostName} live={r.IsLive} evicted={r.Evicted} "
      + $"caps=[{string.Join(',', r.Capabilities)}] {r.Assignment} {r.OwnedPartitions} {r.LibraryVersion} {r.LifecyclePhase}")];

  private static List<string> _describe(IEnumerable<InstanceAssignment> rows) =>
    [.. rows.Select(a => $"{a.InstanceId} {a.ServiceName} {a.HostName} {a.Kind} eligible={a.Eligible} live={a.IsLive} "
      + $"roles=[{string.Join(',', a.Roles)}] {a.OwnedPartitions} {a.MapRevision} {a.LibraryVersion}")];
}
