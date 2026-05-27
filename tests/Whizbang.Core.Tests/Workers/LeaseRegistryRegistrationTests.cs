using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 9 slice 7 — DI registration locks for LeaseRegistry + LeaseHandleOptions
/// + TimeProvider. Same pattern as <see cref="RecentlyProcessedEventCacheRegistrationTests"/>.
/// </summary>
public class LeaseRegistryRegistrationTests {

  private static ServiceCollection _services() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ITimeProvider, SystemTimeProvider>();
    services.AddWhizbangWorkers();
    return services;
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersLeaseRegistry_AsSingletonAsync() {
    var services = _services();

    var descriptor = services.SingleOrDefault(s => s.ServiceType == typeof(LeaseRegistry));

    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton)
      .Because("workers + LeaseRenewalWorker must share the same LeaseRegistry instance");
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTimeProvider_AsSingletonAsync() {
    var services = _services();

    var descriptor = services.SingleOrDefault(s => s.ServiceType == typeof(TimeProvider));

    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersLeaseHandleOptions_WithDefaultsAsync() {
    var services = _services();
    var sp = services.BuildServiceProvider();

    var opts = sp.GetRequiredService<IOptions<LeaseHandleOptions>>().Value;

    await Assert.That(opts.LeaseGraceSeconds).IsEqualTo(30);
    await Assert.That(opts.MaxRenewalsPerWork).IsEqualTo(6);
  }
}
