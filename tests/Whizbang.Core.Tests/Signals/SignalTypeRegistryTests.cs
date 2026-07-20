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
      new SignalTypeEntry(typeof(SigA), "utest-sig-a", SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<SigA>(default, ct)),
    ]);
    var b = new FakeSource([
      new SignalTypeEntry(typeof(SigB), "utest-sig-b", SignalDeliveryClass.Durable, SignalTargeting.Targeted,
        static (sink, ct) => sink.ReceiveAsync<SigB>(default, ct)),
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

  [Test]
  public async Task Entry_Dispatch_DeliversDefaultDoorbellSignalToSinkAsync() {
    // The generator emits this Dispatch shape: reconstruct a default doorbell instance and hand it
    // to the sink (a wire subscriber then fetches authoritative state from the DB).
    var bus = new SignalBus([]);
    SigA? received = null;
    using var sub = bus.Subscribe<SigA>(s => { received = s; return ValueTask.CompletedTask; });

    var entry = new SignalTypeEntry(
      typeof(SigA), "utest-dispatch", SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
      static (sink, ct) => sink.ReceiveAsync<SigA>(default, ct));
    await entry.Dispatch(bus, default);

    await Assert.That(received).IsNotNull();
  }

  [Test]
  public async Task Register_NullSource_ThrowsAsync() {
    await Assert.That(() => SignalTypeRegistry.Register(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task RegisteredCount_IncreasesWithEachRegisterAsync() {
    // Use a unique-per-test wire-name so the assertion is robust to whatever else has registered
    // into the process-wide static registry.
    var before = SignalTypeRegistry.RegisteredCount;
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(SigA), "utest-count", SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<SigA>(default, ct)),
    ]));

    await Assert.That(SignalTypeRegistry.RegisteredCount).IsEqualTo(before + 1);
  }
}
