using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Unit tests for <see cref="WhizbangManagedHealthCheckExtensions"/>, which registers the
/// paired liveness and readiness probes over the shared health aggregator.
/// </summary>
public class WhizbangManagedHealthCheckExtensionsTests {

  private static ServiceProvider _buildProvider(Action<IHealthChecksBuilder>? configure = null) {
    var services = new ServiceCollection();
    services.AddSingleton(new WhizbangHealthOptions());
    services.AddSingleton<WhizbangHealthAggregator>();
    var builder = services.AddHealthChecks();
    (configure ?? (b => b.AddWhizbangManagedHealthChecks()))(builder);
    return services.BuildServiceProvider();
  }

  private static List<HealthCheckRegistration> _registrations(ServiceProvider provider)
      => provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToList();

  [Test]
  public async Task AddWhizbangManagedHealthChecks_ReturnsSameBuilderForChainingAsync() {
    var services = new ServiceCollection();
    var builder = services.AddHealthChecks();

    var returned = builder.AddWhizbangManagedHealthChecks();

    await Assert.That(returned).IsSameReferenceAs(builder);
  }

  [Test]
  public async Task AddWhizbangManagedHealthChecks_RegistersLivenessAndReadinessAsync() {
    using var provider = _buildProvider();

    var names = _registrations(provider).Select(r => r.Name).ToList();

    await Assert.That(names).Contains("whizbang-live");
    await Assert.That(names).Contains("whizbang-ready");
  }

  [Test]
  public async Task AddWhizbangManagedHealthChecks_TagsEachProbeForItsEndpointAsync() {
    using var provider = _buildProvider();
    var registrations = _registrations(provider);

    var live = registrations.Single(r => r.Name == "whizbang-live");
    var ready = registrations.Single(r => r.Name == "whizbang-ready");

    await Assert.That(live.Tags).Contains("live");
    await Assert.That(ready.Tags).Contains("ready");
  }

  [Test]
  public async Task LivenessFactory_ResolvesAgainstTheAggregatorAsync() {
    // The registration factories are only run when the health check is materialised;
    // resolving them here is what exercises the lambdas rather than just their registration.
    using var provider = _buildProvider();
    var live = _registrations(provider).Single(r => r.Name == "whizbang-live");

    var check = live.Factory(provider);

    await Assert.That(check).IsNotNull();
    await Assert.That(check).IsTypeOf<WhizbangManagedHealthCheck>();
  }

  [Test]
  public async Task ReadinessFactory_ResolvesAgainstTheAggregatorAsync() {
    using var provider = _buildProvider();
    var ready = _registrations(provider).Single(r => r.Name == "whizbang-ready");

    var check = ready.Factory(provider);

    await Assert.That(check).IsNotNull();
    await Assert.That(check).IsTypeOf<WhizbangManagedHealthCheck>();
  }

  [Test]
  public async Task AddWhizbangManagedHealthChecks_HonoursCustomProbeNamesAsync() {
    using var provider = _buildProvider(b => b.AddWhizbangManagedHealthChecks("live-x", "ready-x"));

    var names = _registrations(provider).Select(r => r.Name).ToList();

    await Assert.That(names).Contains("live-x");
    await Assert.That(names).Contains("ready-x");
  }

  [Test]
  public async Task AddWhizbangManagedHealthChecks_WithNullBuilder_ThrowsAsync() {
    await Assert.That(() => ((IHealthChecksBuilder)null!).AddWhizbangManagedHealthChecks())
        .ThrowsExactly<ArgumentNullException>();
  }
}
