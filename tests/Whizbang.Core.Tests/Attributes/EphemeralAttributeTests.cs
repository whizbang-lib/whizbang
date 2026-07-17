using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

/// <summary>
/// Contract tests for the ephemeral declaration surface — the compile-time authority the analyzer,
/// generators, and AOT read. Locks the attribute targets (so it can be composed on a base record or a
/// marker interface), the inheritance flag, the enum values, and the shipped default-profile marker.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <tests>Whizbang.Core/Attributes/EphemeralAttribute.cs</tests>
[Category("Core")]
[Category("Attributes")]
public class EphemeralAttributeTests {
  private static AttributeUsageAttribute _usage() =>
    typeof(EphemeralAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

  [Test]
  public async Task Ephemeral_IsComposableOnTypeBaseAndInterfaceAsync() {
    // Class + Struct → the event/base record; Interface → a reusable ephemeral profile.
    var valid = _usage().ValidOn;
    await Assert.That(valid.HasFlag(AttributeTargets.Class)).IsTrue();
    await Assert.That(valid.HasFlag(AttributeTargets.Interface)).IsTrue();
    await Assert.That(valid.HasFlag(AttributeTargets.Struct)).IsTrue();
  }

  [Test]
  public async Task Ephemeral_IsInheritedAndSingleAsync() {
    // Inherited so a base/interface propagates it; single so there is one authority per type.
    await Assert.That(_usage().Inherited).IsTrue();
    await Assert.That(_usage().AllowMultiple).IsFalse();
  }

  [Test]
  public async Task Ephemeral_DefaultsToWhenConsumedPersistedRowAsync() {
    // The default read-model store is PersistedRow (persisted, restart-safe, no expiry) — the safe general
    // choice. InMemory (lost on rebalance) and TtlRow (expires) are explicit opt-ins for their niches.
    var attr = new EphemeralAttribute();
    await Assert.That(attr.Destruction).IsEqualTo(Destruction.WhenConsumed);
    await Assert.That(attr.Storage).IsEqualTo(TransientStorage.PersistedRow);
  }

  [Test]
  public async Task Ephemeral_CarriesConfiguredValuesAsync() {
    var attr = new EphemeralAttribute { Destruction = Destruction.AfterTtl, Storage = TransientStorage.TtlRow };
    await Assert.That(attr.Destruction).IsEqualTo(Destruction.AfterTtl);
    await Assert.That(attr.Storage).IsEqualTo(TransientStorage.TtlRow);
  }

  [Test]
  public async Task Destruction_HasTheFourStrategiesAsync() {
    // E1 wires WhenConsumed; AfterTtl (E2) / OnCompaction (E3) / Archived (A1) declared for the roadmap.
    await Assert.That(Enum.GetNames<Destruction>()).Contains("WhenConsumed");
    await Assert.That(Enum.GetNames<Destruction>()).Contains("AfterTtl");
    await Assert.That(Enum.GetNames<Destruction>()).Contains("OnCompaction");
    await Assert.That(Enum.GetNames<Destruction>()).Contains("Archived");
  }

  [Test]
  public async Task TransientStorage_HasPersistedRowInMemoryAndTtlRowAsync() {
    // PersistedRow is first (the zero-value), so an uninitialized/default TransientStorage is the safe option.
    await Assert.That(Enum.GetNames<TransientStorage>()).Contains("PersistedRow");
    await Assert.That(Enum.GetNames<TransientStorage>()).Contains("InMemory");
    await Assert.That(Enum.GetNames<TransientStorage>()).Contains("TtlRow");
  }

  [Test]
  public async Task IEphemeralEvent_IsTheShippedDefaultProfileAsync() {
    // The marker is not a separate mechanism — it is an IEvent-derived interface CARRYING [Ephemeral]
    // with defaults, resolved via the same interface walk as any developer-authored profile.
    var attr = typeof(IEphemeralEvent).GetCustomAttribute<EphemeralAttribute>(inherit: false);
    await Assert.That(attr).IsNotNull();
    await Assert.That(attr!.Destruction).IsEqualTo(Destruction.WhenConsumed);
    await Assert.That(typeof(IEvent).IsAssignableFrom(typeof(IEphemeralEvent))).IsTrue();
  }
}
