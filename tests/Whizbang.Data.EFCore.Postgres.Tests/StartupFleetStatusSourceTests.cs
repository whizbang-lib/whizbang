using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The fleet section of the startup status surface, end to end against a real database: instances
/// that joined the fleet through the real heartbeat path are what the source reports, each with a
/// heartbeat timestamp fresh enough for the surface to compute an honest per-row age.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupFleetStatusSource.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class StartupFleetStatusSourceTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  public async Task GetFleet_ReportsInstancesThatJoinedThroughTheRealHeartbeatPathAsync(
      CancellationToken cancellationToken) {
    // Two "pods" join the fleet through the real coordinator path.
    await using var ctxA = CreateDbContext();
    await using var ctxB = CreateDbContext();
    var podA = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctxA, JsonContextRegistry.CreateCombinedOptions());
    var podB = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctxB, JsonContextRegistry.CreateCombinedOptions());
    var idA = (Guid)TrackedGuid.NewMedo();
    var idB = (Guid)TrackedGuid.NewMedo();
    await podA.RecordHeartbeatAsync(new HeartbeatRequest(idA, "fleet-svc", "host-a", 1), cancellationToken);
    await podB.RecordHeartbeatAsync(new HeartbeatRequest(idB, "fleet-svc", "host-b", 1), cancellationToken);

    // The source resolves the consumer's DbContext from a fresh scope per call, exactly as the
    // driver registers it.
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    await using var provider = services.BuildServiceProvider();
    var source = new EFCorePostgresStartupFleetStatusSource(
      provider.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext));

    var fleet = await source.GetFleetAsync(cancellationToken);

    var rowA = fleet.FirstOrDefault(r => r.InstanceId == idA);
    var rowB = fleet.FirstOrDefault(r => r.InstanceId == idB);
    await Assert.That(rowA).IsNotNull()
      .Because("an instance that heartbeats is a live instance the fleet section must show");
    await Assert.That(rowB).IsNotNull();
    await Assert.That(rowA!.HostName).IsEqualTo("host-a");
    await Assert.That((DateTimeOffset.UtcNow - rowA.LastHeartbeatAt).TotalMinutes).IsLessThan(2)
      .Because("the heartbeat just happened — a stale timestamp here would make every per-row "
             + "age the surface reports a lie");
  }
}
