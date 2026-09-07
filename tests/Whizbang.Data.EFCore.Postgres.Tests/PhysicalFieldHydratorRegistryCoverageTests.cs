using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for the non-generic <c>Register(Type, Action)</c> overload -- the one generated code
/// calls directly with a closed <c>PerspectiveRow&lt;TModel&gt;</c> type it already has in hand.
/// The sibling test file only ever exercised the generic <c>Register&lt;TModel&gt;</c> convenience
/// wrapper, leaving this overload's own null guards and assignment unexercised.
/// </summary>
/// <remarks>
/// The registry is process-wide static, so these tests clear it around themselves and serialize
/// against the sibling test class under the SAME constraint key: a registration left behind by
/// either class would change what the other's materialization produces.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PhysicalFieldHydratorRegistry.cs</code-under-test>
[NotInParallel("PhysicalFieldHydratorRegistry")]
[Category("Shard1")]
public class PhysicalFieldHydratorRegistryCoverageTests {

  private sealed class HydratorCoverageModel { public string? Name { get; set; } }

  [Before(Test)]
  public void ClearBefore() => PhysicalFieldHydratorRegistry.Clear();

  [After(Test)]
  public void ClearAfter() => PhysicalFieldHydratorRegistry.Clear();

  [Test]
  public async Task Register_WithExplicitRowType_StoresHydratorRetrievableByThatTypeAsync() {
    // Generated code for a split-mode perspective calls this overload with the closed row type it
    // already computed at compile time. If the assignment regressed, physical columns would
    // silently stop hydrating onto the model for every perspective wired through this path.
    static void Hydrate(MaterializationInterceptionData data, object entity) { }
    var rowType = typeof(Whizbang.Core.Lenses.PerspectiveRow<HydratorCoverageModel>);

    PhysicalFieldHydratorRegistry.Register(rowType, Hydrate);

    var found = PhysicalFieldHydratorRegistry.TryGetHydrator(rowType, out var hydrator);

    await Assert.That(found).IsTrue()
      .Because("the non-generic overload must key the hydrator by the exact row type it was given");
    await Assert.That(hydrator).IsNotNull();
  }

  [Test]
  public async Task Register_NullRowType_ThrowsArgumentNullExceptionAsync() {
    // A null row type accepted here would either NRE deep inside the dictionary indexer or, worse,
    // silently key a hydrator under a null Type -- either way the failure surfaces far from the
    // generated call site that passed the bad value.
    static void Hydrate(MaterializationInterceptionData data, object entity) { }

    await Assert.That(() => PhysicalFieldHydratorRegistry.Register(null!, Hydrate))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Register_NullHydrator_ThrowsArgumentNullExceptionAsync() {
    // A null hydrator accepted here would surface as a NullReferenceException deep inside EF's
    // materialization pipeline instead of at the registration call that caused it.
    var rowType = typeof(Whizbang.Core.Lenses.PerspectiveRow<HydratorCoverageModel>);

    await Assert.That(() => PhysicalFieldHydratorRegistry.Register(rowType, null!))
      .Throws<ArgumentNullException>();
  }
}
