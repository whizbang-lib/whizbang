using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

public class SignalBusRegistrationTests {
  private readonly record struct RegSignal(int Value) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  [Test]
  public async Task AddWhizbangSignalBus_ResolvesBusAndDeliversViaDefaultTransportAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangSignalBus();
    await using var provider = services.BuildServiceProvider();

    var bus = provider.GetRequiredService<ISignalBus>();
    await provider.GetRequiredService<SignalBus>().StartAsync();

    var delivered = false;
    using var sub = bus.Subscribe<RegSignal>(_ => { delivered = true; return ValueTask.CompletedTask; });
    await bus.PublishAsync(new RegSignal(1));

    await Assert.That(delivered).IsTrue();
  }

  [Test]
  public async Task AddWhizbangSignalBus_RegistersInMemoryTransportByDefaultAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangSignalBus();
    await using var provider = services.BuildServiceProvider();

    var transports = provider.GetServices<ISignalTransport>().ToList();

    await Assert.That(transports.Any(t => t is InMemorySignalTransport)).IsTrue();
  }
}
