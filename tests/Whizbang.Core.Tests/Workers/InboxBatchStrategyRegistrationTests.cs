using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the slice 7 (Half A) DI contract for <see cref="IInboxBatchStrategy"/>:
/// <list type="bullet">
///   <item>Default registration via <see cref="WorkerPipelineExtensions.AddWhizbangWorkers"/>
///   resolves to <see cref="SlidingWindowInboxBatchStrategy"/> with the 50 ms / 1 s / 100
///   defaults.</item>
///   <item><see cref="WorkerPipelineExtensions.AddWhizbangInboxStrategy{TStrategy}"/> swaps
///   the registered implementation. Used by low-throughput tenants who opt to
///   <see cref="ImmediateInboxBatchStrategy"/> for back-compat / strict-ordering semantics.</item>
///   <item><see cref="SlidingWindowInboxOptions"/> defaults remain 50 ms / 1 s / 100.</item>
///   <item>Options bind cleanly from <see cref="IConfiguration"/> for ops overrides.</item>
/// </list>
/// </summary>
public class InboxBatchStrategyRegistrationTests {

  [Test]
  public async Task AddWhizbang_DefaultRegistration_ResolvesSlidingWindowStrategyAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IInboxBatchStrategy>();

    await Assert.That(resolved).IsTypeOf<SlidingWindowInboxBatchStrategy>();
  }

  [Test]
  public async Task AddWhizbangInboxStrategy_ImmediateOverride_ResolvesImmediateStrategyAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangInboxStrategy<ImmediateInboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IInboxBatchStrategy>();

    await Assert.That(resolved).IsTypeOf<ImmediateInboxBatchStrategy>();
  }

  [Test]
  public async Task SlidingWindowInboxOptions_DefaultsLockedAt300ms3s1000Async() {
    // Slice 23 defaults: 300ms / 3s / 1000.
    var defaults = new SlidingWindowInboxOptions();
    await Assert.That(defaults.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(300));
    await Assert.That(defaults.MaxWait).IsEqualTo(TimeSpan.FromSeconds(3));
    await Assert.That(defaults.MaxSize).IsEqualTo(1000);
  }

  [Test]
  public async Task AddWhizbangWorkers_ResolvesIReceptorRegistryQueryToAdapterAsync() {
    // Slice 4 / slice 3 / slice 6 all depend on the receptor registry being injected via
    // production DI. If AddWhizbangWorkers' registration is dropped, the gates silently
    // fall back to legacy "always store / always deserialize" behavior. The slice 4
    // production-bug fix (commit 338f6ac7) demonstrated how easy it is for the registry
    // to be wrong in production while tests pass; this lock catches the upstream
    // registration regression.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IReceptorRegistryQuery>();

    await Assert.That(resolved).IsTypeOf<WhizbangReceptorRegistryQueryAdapter>()
      .Because("AddWhizbangWorkers must register the source-gen-backed adapter as the default IReceptorRegistryQuery — gates depend on it.");
  }

  [Test]
  public async Task SlidingWindowInboxOptions_OverrideViaConfiguration_ReadsCorrectValuesAsync() {
    var configValues = new Dictionary<string, string?> {
      ["Whizbang:Inbox:SlidingWindow"] = "00:00:00.250",
      ["Whizbang:Inbox:MaxWait"] = "00:00:05",
      ["Whizbang:Inbox:MaxSize"] = "500",
    };
    var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.Configure<SlidingWindowInboxOptions>(config.GetSection("Whizbang:Inbox"));

    await using var sp = services.BuildServiceProvider();
    var bound = sp.GetRequiredService<IOptions<SlidingWindowInboxOptions>>().Value;

    await Assert.That(bound.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(250));
    await Assert.That(bound.MaxWait).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(bound.MaxSize).IsEqualTo(500);
  }
}
