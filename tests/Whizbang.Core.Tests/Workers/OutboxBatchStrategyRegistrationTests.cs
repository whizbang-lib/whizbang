using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the slice 12 (Half B) DI contract for <see cref="IOutboxBatchStrategy"/>:
/// <list type="bullet">
///   <item>Default registration via <see cref="WorkerPipelineExtensions.AddWhizbangWorkers"/>
///   resolves to <see cref="SlidingWindowOutboxBatchStrategy"/> with the 50 ms / 1 s / 100
///   defaults.</item>
///   <item><see cref="WorkerPipelineExtensions.AddWhizbangOutboxStrategy{TStrategy}"/> swaps
///   the registered implementation. Used by low-throughput tenants who opt to
///   <see cref="ImmediateOutboxBatchStrategy"/> for back-compat / strict-ordering semantics.</item>
///   <item><see cref="SlidingWindowOutboxOptions"/> defaults remain 50 ms / 1 s / 100.</item>
/// </list>
/// </summary>
public class OutboxBatchStrategyRegistrationTests {

  [Test]
  public async Task AddWhizbang_DefaultRegistration_ResolvesSlidingWindowOutboxAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IOutboxBatchStrategy>();

    await Assert.That(resolved).IsTypeOf<SlidingWindowOutboxBatchStrategy>();
  }

  [Test]
  public async Task AddWhizbangOutboxStrategy_ImmediateOverride_ResolvesImmediateAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    services.AddWhizbangOutboxStrategy<ImmediateOutboxBatchStrategy>();

    await using var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IOutboxBatchStrategy>();

    await Assert.That(resolved).IsTypeOf<ImmediateOutboxBatchStrategy>();
  }

  [Test]
  public async Task SlidingWindowOutboxOptions_DefaultsLockedAt50ms1s100Async() {
    var defaults = new SlidingWindowOutboxOptions();
    await Assert.That(defaults.SlidingWindow).IsEqualTo(TimeSpan.FromMilliseconds(50));
    await Assert.That(defaults.MaxWait).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(defaults.MaxSize).IsEqualTo(100);
  }
}
