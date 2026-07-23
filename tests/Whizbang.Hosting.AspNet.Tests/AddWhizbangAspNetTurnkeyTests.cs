using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Covers the turnkey wiring in <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/>: it
/// auto-registers the schema-availability gate (via startup filter) and the managed liveness/readiness
/// health checks, idempotently.
/// </summary>
public class AddWhizbangAspNetTurnkeyTests {

  [Test]
  public async Task RegistersAvailabilityStartupFilter_AndManagedHealthChecksAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAspNet();

    var hasGateFilter = services.Any(d =>
      d.ServiceType == typeof(IStartupFilter) && d.ImplementationType == typeof(WhizbangAvailabilityStartupFilter));
    await Assert.That(hasGateFilter).IsTrue();

    using var provider = services.BuildServiceProvider();
    var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
    await Assert.That(registrations.Any(r => r.Name == "whizbang-ready")).IsTrue();
    await Assert.That(registrations.Any(r => r.Name == "whizbang-live")).IsTrue();
  }

  [Test]
  public async Task Idempotent_NoDuplicateHealthChecksAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangAspNet();
    services.AddWhizbangAspNet();

    using var provider = services.BuildServiceProvider();
    var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
    await Assert.That(registrations.Count(r => r.Name == "whizbang-ready")).IsEqualTo(1);
    await Assert.That(registrations.Count(r => r.Name == "whizbang-live")).IsEqualTo(1);
  }

  [Test]
  public async Task AvailabilityOptions_DefaultToMutationsOnlyAndEnabledAsync() {
    var options = new WhizbangAvailabilityOptions();
    await Assert.That(options.Enabled).IsTrue();
    await Assert.That(options.Mode).IsEqualTo(AvailabilityGateMode.MutationsOnly);
  }
}
