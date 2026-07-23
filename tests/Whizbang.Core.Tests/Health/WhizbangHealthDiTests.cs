using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers the health DI wiring: <c>AddWhizbangManagedHealth</c> registers an aggregator over every
/// source added with <c>AddWhizbangHealthSource</c>, and applies the configured policy.
/// </summary>
public class WhizbangHealthDiTests {

  private sealed class MigratingSource : IWhizbangHealthSource {
    public string Component => "schema";
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken)
      => new(new ComponentHealth(ComponentState.Migrating));
  }

  [Test]
  public async Task AddWhizbangManagedHealth_AggregatesRegisteredSourcesAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangManagedHealth();
    services.AddWhizbangHealthSource<MigratingSource>();
    using var provider = services.BuildServiceProvider();

    var aggregator = provider.GetRequiredService<WhizbangHealthAggregator>();
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(result.Components.Count).IsEqualTo(1);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy); // Migrating is ready under the Lenient default
  }

  [Test]
  public async Task AddWhizbangManagedHealth_AppliesPolicyConfigurationAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangManagedHealth(o => o.Components["schema"] = HealthPolicy.Strict);
    services.AddWhizbangHealthSource<MigratingSource>();
    using var provider = services.BuildServiceProvider();

    var aggregator = provider.GetRequiredService<WhizbangHealthAggregator>();
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy); // Strict: Migrating => not ready
  }
}
