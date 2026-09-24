using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The declaration surface for a composite or partial perspective index.
/// </summary>
/// <remarks>
/// The generator reads this attribute from source symbols rather than from an instance, so these
/// cases cover the part a consuming application actually touches: that the constructor keeps the
/// properties in the order given, and that each option is readable as written. Order is the one
/// that matters, because a composite index answers a filter on a leading subset of its properties
/// and cannot be used for one that skips the leading property.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveIndexAttribute.cs</code-under-test>
[Category("Perspectives")]
public class PerspectiveIndexAttributeTests {

  [Test]
  public async Task TheProperties_KeepTheOrderTheyWereGivenAsync() {
    var declaration = new PerspectiveIndexAttribute("TenantId", "EntityType");

    await Assert.That(declaration.Properties).IsEquivalentTo(["TenantId", "EntityType"])
      .Because("the order is part of what a composite index means, so the declaration has to "
             + "preserve it rather than treat the properties as a set.");
    await Assert.That(declaration.Properties[0]).IsEqualTo("TenantId")
      .Because("the leading property is the one a filter must include for the index to be usable.");
  }

  [Test]
  public async Task WithoutOptions_NothingIsDeclaredAsync() {
    var declaration = new PerspectiveIndexAttribute("TenantId");

    await Assert.That(declaration.Name).IsNull()
      .Because("an absent name means the generator derives one.");
    await Assert.That(declaration.Where).IsNull()
      .Because("an absent predicate means the index covers every row.");
    await Assert.That(declaration.Unique).IsFalse();
  }

  [Test]
  public async Task EachOption_IsReadableAsWrittenAsync() {
    var declaration = new PerspectiveIndexAttribute("TenantId") {
      Name = "idx_chosen",
      Where = "(data ->> 'Status') = 'active'",
      Unique = true,
      Method = PerspectiveIndexMethod.Gin,
      OperatorClass = "jsonb_path_ops",
      Expressions = ["lower(name)"],
    };

    await Assert.That(declaration.Name).IsEqualTo("idx_chosen");
    await Assert.That(declaration.Where).IsEqualTo("(data ->> 'Status') = 'active'")
      .Because("the predicate is passed through verbatim: it has to match the text of the query's "
             + "own filter, because PostgreSQL decides whether a partial index applies by reasoning "
             + "about that text rather than by evaluating it.");
    await Assert.That(declaration.Unique).IsTrue();
    await Assert.That(declaration.Method).IsEqualTo(PerspectiveIndexMethod.Gin)
      .Because("the method is the difference between an index that answers a containment filter "
             + "and one that cannot");
    await Assert.That(declaration.OperatorClass).IsEqualTo("jsonb_path_ops")
      .Because("the operator class decides what the index can answer, and is the author's to name");
    await Assert.That(declaration.Expressions).IsEquivalentTo(["lower(name)"])
      .Because("an expression is indexable where a property alone is not, and it is passed through "
             + "verbatim for the same reason the predicate is");
  }

  /// <summary>
  /// A declaration naming one property is still legitimate, because a predicate makes it a
  /// partial index that <c>[Indexed]</c> cannot express.
  /// </summary>
  [Test]
  public async Task OneProperty_IsAllowedAsync() {
    var declaration = new PerspectiveIndexAttribute("TenantId") { Where = "tenant_id IS NOT NULL" };

    await Assert.That(declaration.Properties).Count().IsEqualTo(1);
    await Assert.That(declaration.Where).IsNotNull();
  }

  /// <summary>
  /// No properties leaves an empty list rather than null, so a reader never has to guard for it.
  /// </summary>
  /// <remarks>
  /// The generator drops such a declaration -- an index over no properties is not an index -- but it
  /// reads the list to find that out, and a null there would be a crash in a source generator rather
  /// than a skipped index.
  /// </remarks>
  [Test]
  public async Task NoProperties_LeavesAnEmptyListNotNullAsync() {
    var declaration = new PerspectiveIndexAttribute();

    await Assert.That(declaration.Properties).IsNotNull();
    await Assert.That(declaration.Properties).IsEmpty();
  }

  /// <summary>
  /// A null array is treated as none, for the same reason.
  /// </summary>
  /// <remarks>
  /// Reachable through reflection and through an explicit null, and a source generator that faulted
  /// on it would fail the whole compilation rather than skip one declaration.
  /// </remarks>
  [Test]
  public async Task ANullArray_IsTreatedAsNoneAsync() {
    var declaration = new PerspectiveIndexAttribute(null!);

    await Assert.That(declaration.Properties).IsNotNull();
    await Assert.That(declaration.Properties).IsEmpty();
  }
}
