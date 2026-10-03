using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Resilience;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// <see cref="CircuitBreakerOptions"/> are named options: each breaker reads
/// <c>Whizbang:CircuitBreakers:&lt;name&gt;</c> over its code values, and the unnamed instance keeps
/// its defaults.
/// </summary>
public class CircuitBreakerOptionsConfigurationTests {
  private static IConfiguration _config(params (string Key, string Value)[] values) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
      .Build();

  private static ServiceProvider _provider(IConfiguration? configuration, Action<IServiceCollection>? arrange = null) {
    var services = new ServiceCollection();
    if (configuration is not null) {
      services.AddSingleton(configuration);
    }
    arrange?.Invoke(services);
    services.TryAddWhizbangDefaults();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task NamedBreaker_PicksUpItsOwnKeys_AndNotAnotherBreakersAsync() {
    await using var provider = _provider(_config(
      ("Whizbang:CircuitBreakers:payments:FailureThreshold", "3"),
      ("Whizbang:CircuitBreakers:payments:InitialCooldownSeconds", "7"),
      ("Whizbang:CircuitBreakers:payments:CooldownBackoffMultiplier", "1.5"),
      ("Whizbang:CircuitBreakers:payments:MaxCooldownSeconds", "90"),
      ("Whizbang:CircuitBreakers:payments:SuccessCacheDurationSeconds", "11"),
      ("Whizbang:CircuitBreakers:search:FailureThreshold", "20")));
    var monitor = provider.GetRequiredService<IOptionsMonitor<CircuitBreakerOptions>>();

    var payments = monitor.Get("payments");
    var search = monitor.Get("search");

    await Assert.That(payments.FailureThreshold).IsEqualTo(3);
    await Assert.That(payments.InitialCooldownSeconds).IsEqualTo(7);
    await Assert.That(payments.CooldownBackoffMultiplier).IsEqualTo(1.5);
    await Assert.That(payments.MaxCooldownSeconds).IsEqualTo(90);
    await Assert.That(payments.SuccessCacheDurationSeconds).IsEqualTo(11);
    await Assert.That(search.FailureThreshold).IsEqualTo(20);
    await Assert.That(search.InitialCooldownSeconds).IsEqualTo(3)
      .Because("a key under another breaker's name must not leak into this one");
  }

  [Test]
  public async Task UnnamedInstance_KeepsDefaultsAsync() {
    await using var provider = _provider(_config(
      ("Whizbang:CircuitBreakers:payments:FailureThreshold", "3")));

    var unnamed = provider.GetRequiredService<IOptions<CircuitBreakerOptions>>().Value;

    await Assert.That(unnamed.FailureThreshold).IsEqualTo(5);
    await Assert.That(unnamed.MaxCooldownSeconds).IsEqualTo(300);
  }

  [Test]
  public async Task CodeValues_StayTheDefaults_ConfigurationOverridesPerKeyAsync() {
    await using var provider = _provider(
      _config(("Whizbang:CircuitBreakers:payments:FailureThreshold", "3")),
      services => services.Configure<CircuitBreakerOptions>("payments", o => {
        o.FailureThreshold = 9;
        o.MaxCooldownSeconds = 60;
      }));

    var payments = provider.GetRequiredService<IOptionsMonitor<CircuitBreakerOptions>>().Get("payments");

    await Assert.That(payments.FailureThreshold).IsEqualTo(3).Because("configuration wins over code");
    await Assert.That(payments.MaxCooldownSeconds).IsEqualTo(60).Because("an unconfigured key keeps the code value");
  }

  [Test]
  public async Task NoConfiguration_LeavesCodeValuesAloneAsync() {
    await using var provider = _provider(null,
      services => services.Configure<CircuitBreakerOptions>("payments", o => o.FailureThreshold = 9));

    var payments = provider.GetRequiredService<IOptionsMonitor<CircuitBreakerOptions>>().Get("payments");

    await Assert.That(payments.FailureThreshold).IsEqualTo(9);
  }

  [Test]
  public async Task RepeatedDefaults_RegisterTheBinderOnceAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.TryAddWhizbangDefaults();

    var binders = services.Count(d => d.ServiceType == typeof(IPostConfigureOptions<CircuitBreakerOptions>));

    await Assert.That(binders).IsEqualTo(1);
  }

  [Test]
  public async Task PostConfigure_RejectsNullOptionsAsync() {
    await using var provider = _provider(_config());
    var binder = provider.GetServices<IPostConfigureOptions<CircuitBreakerOptions>>().Single();

    await Assert.That(() => binder.PostConfigure("payments", null!)).Throws<ArgumentNullException>();
  }
}
