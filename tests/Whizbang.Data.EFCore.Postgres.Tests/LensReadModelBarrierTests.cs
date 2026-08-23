using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Increment 6, the read seam (option A): every lens surface inherits one check — resolution
/// refuses while the read-model barrier is closed, because a lens must not read perspectives a
/// migration may have left mid-repair. Later than <c>Migrate</c>, earlier than <c>Ready</c>:
/// reads never needed the transports.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreInfrastructureRegistration.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ReadModelsReadyGate.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class LensReadModelBarrierTests : EFCoreTestBase {

  private sealed class ProbeModel {
    public Guid Id { get; set; }
  }

  private ServiceProvider _buildHost(IReadModelsReadyGate? gate) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    services.AddSingleton<Whizbang.Core.Security.IScopeContextAccessor>(
      new Whizbang.Core.Security.ScopeContextAccessor());
    services.AddOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>();
    if (gate is not null) {
      services.AddSingleton(gate);
    }
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services, typeof(WorkCoordinationDbContext), typeof(ProbeModel), "probe_model",
      new PostgresUpsertStrategy());
    return services.BuildServiceProvider();
  }

  [Test]
  [Timeout(60000)]
  public async Task LensResolution_WhileTheBarrierIsClosed_RefusesAsync(CancellationToken cancellationToken) {
    var gate = new ReadModelsReadyGate();   // closed: Migrate or the perspective scan still running
    await using var provider = _buildHost(gate);
    using var scope = provider.CreateScope();

    await Assert.ThrowsAsync<WhizbangNotReadyException>(async () => {
      _ = scope.ServiceProvider.GetRequiredService<ILensQuery<ProbeModel>>();
      await Task.CompletedTask;
    });

    // The barrier releases; the same host serves reads.
    gate.MarkReady();
    using var openScope = provider.CreateScope();
    var query = openScope.ServiceProvider.GetRequiredService<ILensQuery<ProbeModel>>();
    await Assert.That(query).IsNotNull()
      .Because("reads resume the moment the read models are trustworthy — not a moment later "
             + "waiting on transports they never needed");
  }

  [Test]
  [Timeout(60000)]
  public async Task LensResolution_WithNoBarrierRegistered_StaysUngatedAsync(CancellationToken cancellationToken) {
    await using var provider = _buildHost(gate: null);
    using var scope = provider.CreateScope();

    var query = scope.ServiceProvider.GetRequiredService<ILensQuery<ProbeModel>>();

    await Assert.That(query).IsNotNull()
      .Because("fixtures and hosts without the worker pipeline behave exactly as before");
  }
}
