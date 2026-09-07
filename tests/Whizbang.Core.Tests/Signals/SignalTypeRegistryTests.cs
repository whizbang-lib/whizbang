using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

[NotInParallel("SignalTypeRegistryStatic")]
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
  public async Task Register_EntryBecomesQueryableByWireNameAsync() {
    // Deterministic: assert on THIS registration's unique wire-name via IsRegistered, so the test is
    // immune to whatever else registers into the process-wide static registry in parallel (module
    // initializers, other tests). Asserting the exact RegisteredCount delta would race those
    // concurrent registrations — the flake this replaces.
    const string wireName = "utest-register-queryable";
    var before = SignalTypeRegistry.RegisteredCount;
    await Assert.That(SignalTypeRegistry.IsRegistered(wireName)).IsFalse();

    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(SigA), wireName, SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<SigA>(default, ct)),
    ]));

    await Assert.That(SignalTypeRegistry.IsRegistered(wireName)).IsTrue();
    // RegisteredCount only grows, so it is at least one more than before — a race-free check
    // (>= not ==: concurrent tests may register too; asserting an exact delta is the removed flake).
    await Assert.That(SignalTypeRegistry.RegisteredCount).IsGreaterThanOrEqualTo(before + 1);
  }

  [Test]
  public async Task IsRegistered_NullWireName_ThrowsAsync() {
    await Assert.That(() => SignalTypeRegistry.IsRegistered(null!)).Throws<ArgumentNullException>();
  }

  // Clear is the registry's only way back to a known-empty state. Registration is one-way
  // (Register appends, nothing removes), so a Clear that only *appeared* to work — dropping the
  // count while leaving sources reachable through GetAll, say — would hand a caller a registry it
  // believes is empty and is not, and every wire-name lookup after it would resolve against
  // entries the caller thought were gone. The three query surfaces are therefore all asserted:
  // the count, the per-wire-name lookup, and the enumerated union.
  //
  // Everything the process registered before this point is captured and put back through the
  // public Register seam, so the union GetAll returns is unchanged once the test finishes. The
  // whole class is [NotInParallel] on the same key as the other two suites that read this static
  // registry, so nothing observes the empty window.
  [Test]
  public async Task Clear_EmptiesEveryQuerySurfaceNotJustTheCountAsync() {
    const string wireName = "utest-clear-restores";
    var preexisting = SignalTypeRegistry.GetAll();
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(SigA), wireName, SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<SigA>(default, ct)),
    ]));
    // The registry must actually be holding this before "it is gone afterwards" means anything.
    await Assert.That(SignalTypeRegistry.IsRegistered(wireName)).IsTrue();

    try {
      SignalTypeRegistry.Clear();

      await Assert.That(SignalTypeRegistry.RegisteredCount).IsEqualTo(0);
      await Assert.That(SignalTypeRegistry.GetAll()).IsEmpty()
        .Because("a source still reachable through GetAll after Clear would resolve wire names a "
               + "caller believes it has removed");
      await Assert.That(SignalTypeRegistry.IsRegistered(wireName)).IsFalse();
    } finally {
      // Restore the union the rest of the process depends on (module-initializer sources included).
      if (preexisting.Count > 0) {
        SignalTypeRegistry.Register(new FakeSource(preexisting));
      }
    }

    await Assert.That(SignalTypeRegistry.GetAll().Count).IsEqualTo(preexisting.Count)
      .Because("the restore has to put back exactly what was there — leaving the process-wide "
             + "registry short would break every later reader of it");
  }
}
