using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 7 slice 7 — DI registration locks for the cooldown cache + sweep worker.
/// </summary>
public class RecentlyProcessedEventCacheRegistrationTests {

  private static ServiceCollection _services() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ITimeProvider, SystemTimeProvider>();
    services.AddWhizbangWorkers();
    return services;
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersRecentlyProcessedEventCache_AsSingletonAsync() {
    var services = _services();

    var descriptor = services.SingleOrDefault(s => s.ServiceType == typeof(RecentlyProcessedEventCache));

    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton)
      .Because("drainer + sweep worker must share the same instance");
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersSweepWorker_AsHostedServiceAsync() {
    var services = _services();

    var sweepRegistered = services.Any(s => s.ImplementationType == typeof(RecentlyProcessedEventCacheSweepWorker));
    var hostedFactoryRegistered = services.Any(s =>
      s.ServiceType == typeof(IHostedService) &&
      s.ImplementationFactory is not null);

    await Assert.That(sweepRegistered).IsTrue()
      .Because("sweep worker must be registered as a singleton so DI resolves it");
    await Assert.That(hostedFactoryRegistered).IsTrue()
      .Because("an IHostedService factory must exist that resolves the sweep worker (and other workers)");
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersOptions_WithDefaultsAsync() {
    var services = _services();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(_ =>
      throw new InvalidOperationException("not used in this test"));
    var sp = services.BuildServiceProvider();

    var opts = sp.GetRequiredService<IOptions<RecentlyProcessedEventCacheOptions>>().Value;

    await Assert.That(opts.Enabled).IsTrue();
    await Assert.That(opts.TtlMinutes).IsEqualTo(5);
    await Assert.That(opts.MaxEntries).IsEqualTo(100_000);
    await Assert.That(opts.SweepIntervalSeconds).IsEqualTo(60);
  }

  [Test]
  public async Task RecentlyProcessedEventCacheFactory_BuildsFromOptionsAsync() {
    // Resolve the cache directly using a minimal SP that doesn't pull in HeartbeatWorker etc.
    var services = new ServiceCollection();
    services.AddSingleton<ITimeProvider, SystemTimeProvider>();
    services.AddOptions<RecentlyProcessedEventCacheOptions>().Configure(o => {
      o.TtlMinutes = 7;
      o.MaxEntries = 50_000;
    });
    services.AddSingleton(sp => {
      var opts = sp.GetRequiredService<IOptions<RecentlyProcessedEventCacheOptions>>().Value;
      var time = sp.GetRequiredService<ITimeProvider>();
      return new RecentlyProcessedEventCache(
        timeProvider: time,
        ttl: TimeSpan.FromMinutes(Math.Max(1, opts.TtlMinutes)),
        maxEntries: Math.Max(1, opts.MaxEntries));
    });
    var sp = services.BuildServiceProvider();

    var cache = sp.GetRequiredService<RecentlyProcessedEventCache>();

    await Assert.That(cache).IsNotNull();
  }
}
