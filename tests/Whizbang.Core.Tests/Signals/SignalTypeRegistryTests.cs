using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

public class SignalTypeRegistryTests {
  private readonly record struct SigA(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private readonly record struct SigB(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Targeted;
  }

  private sealed class FakeSource(IReadOnlyList<SignalTypeEntry> entries) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => entries;
  }

  [Test]
  public async Task GetAll_IncludesEntriesFromEveryRegisteredSourceAsync() {
    // Unique wire names so the assertions are robust to whatever else has registered into the
    // process-wide static registry (module initializers, other tests).
    var a = new FakeSource([
      new SignalTypeEntry(typeof(SigA), "utest-sig-a", SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast),
    ]);
    var b = new FakeSource([
      new SignalTypeEntry(typeof(SigB), "utest-sig-b", SignalDeliveryClass.Durable, SignalTargeting.Targeted),
    ]);
    SignalTypeRegistry.Register(a);
    SignalTypeRegistry.Register(b);

    var all = SignalTypeRegistry.GetAll();

    var entryA = all.SingleOrDefault(e => e.WireName == "utest-sig-a");
    var entryB = all.SingleOrDefault(e => e.WireName == "utest-sig-b");
    await Assert.That(entryA).IsNotNull();
    await Assert.That(entryB).IsNotNull();
    await Assert.That(entryA!.SignalType).IsEqualTo(typeof(SigA));
    await Assert.That(entryA!.DeliveryClass).IsEqualTo(SignalDeliveryClass.BestEffort);
    await Assert.That(entryB!.Targeting).IsEqualTo(SignalTargeting.Targeted);
  }
}
