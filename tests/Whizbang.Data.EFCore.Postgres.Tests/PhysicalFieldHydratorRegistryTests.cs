using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The registry that maps a perspective row type to the delegate which copies physical column
/// values onto its model after materialization.
/// <para>
/// Physical fields are stored as real columns alongside the JSON document so they can be indexed
/// and filtered in SQL. Nothing in the generated model puts them back on the CLR object — this
/// hydrator does, keyed by row type. A lookup that misses means the row materializes with those
/// fields left at their defaults, which reads downstream as a row whose values are genuinely empty
/// rather than as a wiring gap.
/// </para>
/// <para>
/// The registry is process-wide static, so these tests clear it around themselves and serialize
/// against each other: a registration left behind would change what an unrelated test's
/// materialization produces.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PhysicalFieldHydratorRegistry.cs</code-under-test>
[NotInParallel("PhysicalFieldHydratorRegistry")]
[Category("Shard4")]
public class PhysicalFieldHydratorRegistryTests {

  private sealed class HydratorTestModel { public string? Name { get; set; } }

  [Before(Test)]
  public void ClearBefore() => PhysicalFieldHydratorRegistry.Clear();

  [After(Test)]
  public void ClearAfter() => PhysicalFieldHydratorRegistry.Clear();

  [Test]
  public async Task ARegisteredHydrator_IsFoundByItsRowTypeAsync() {
    static void Hydrate(MaterializationInterceptionData data, object entity) { }
    PhysicalFieldHydratorRegistry.Register<HydratorTestModel>(Hydrate);

    var found = PhysicalFieldHydratorRegistry.TryGetHydrator(
      typeof(Whizbang.Core.Lenses.PerspectiveRow<HydratorTestModel>), out var hydrator);

    await Assert.That(found).IsTrue()
      .Because("the lookup is keyed by the ROW type, not the model — a mismatch here leaves "
             + "physical fields at their defaults and the row reads as genuinely empty");
    await Assert.That(hydrator).IsNotNull();
  }

  [Test]
  public async Task AnUnregisteredType_ReportsMissingRatherThanReturningNullSilentlyAsync() {
    var found = PhysicalFieldHydratorRegistry.TryGetHydrator(typeof(string), out var hydrator);

    await Assert.That(found).IsFalse()
      .Because("callers branch on the boolean; a true with a null delegate would throw at the "
             + "point of materialization instead of at the lookup");
    await Assert.That(hydrator).IsNull();
  }

  [Test]
  public async Task RegisteringNull_IsRejectedAtRegistrationRatherThanAtMaterializationAsync() {
    // A null delegate accepted here would surface as a NullReferenceException deep inside EF's
    // materialization pipeline, far from the registration that caused it.
    await Assert.That(() => PhysicalFieldHydratorRegistry.Register<HydratorTestModel>(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Clearing_RemovesRegistrationsSoTestsDoNotLeakIntoEachOtherAsync() {
    static void Hydrate(MaterializationInterceptionData data, object entity) { }
    PhysicalFieldHydratorRegistry.Register<HydratorTestModel>(Hydrate);

    PhysicalFieldHydratorRegistry.Clear();

    await Assert.That(PhysicalFieldHydratorRegistry.TryGetHydrator(
      typeof(Whizbang.Core.Lenses.PerspectiveRow<HydratorTestModel>), out _)).IsFalse()
      .Because("the registry is process-wide static, so a leftover registration changes what an "
             + "unrelated test's materialization produces");
  }
}
