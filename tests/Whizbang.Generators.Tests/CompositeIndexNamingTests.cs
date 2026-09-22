extern alias shared;

using System.Collections.Immutable;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
// Through the extern alias, the way every other test of a shared type reaches it. A plain using
// resolves against the ILRepack-merged copy inside one of the generator assemblies instead, which
// compiles and is the wrong type identity -- the alias exists to keep those apart.
using CompositeIndexElement = shared::Whizbang.Generators.Shared.Models.CompositeIndexElement;
using CompositeIndexInfo = shared::Whizbang.Generators.Shared.Models.CompositeIndexInfo;
using CompositeIndexSql = shared::Whizbang.Generators.Shared.Models.CompositeIndexSql;

namespace Whizbang.Generators.Tests;

/// <summary>
/// How a composite index's name is derived, and what happens when it does not fit.
/// </summary>
/// <remarks>
/// A composite's derived name concatenates every property it covers, so it reaches PostgreSQL's
/// identifier limit far sooner than a single-property name does. PostgreSQL truncates rather than
/// refusing, so two long names sharing a prefix arrive as one, and CREATE INDEX IF NOT EXISTS
/// quietly skips the second -- the author declares two indexes and is given one, silently.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/CompositeIndexInfo.cs</code-under-test>
[Category("SourceGenerators")]
public class CompositeIndexNamingTests {

  private static CompositeIndexInfo _index(params string[] properties) =>
    new([.. properties.Select(p => new CompositeIndexElement(p, p.ToLowerInvariant()))]);

  [Test]
  public async Task ADerivedName_NamesEveryPropertyInDeclaredOrderAsync() {
    var name = CompositeIndexSql.Name(_index("TenantId", "EntityType"), "doc");

    await Assert.That(name).IsEqualTo("idx_doc_tenantid_entitytype")
      .Because("the order is part of what the index means, so it is part of its name: reordering "
             + "the properties is a different index, not the same one left alone by IF NOT EXISTS.");
  }

  [Test]
  public async Task AnExplicitName_WinsOverTheDerivedOneAsync() {
    var name = CompositeIndexSql.Name(
      new CompositeIndexInfo([new CompositeIndexElement("A", "a")], Name: "idx_chosen"), "doc");

    await Assert.That(name).IsEqualTo("idx_chosen");
  }

  [Test]
  public async Task AnOverLongDerivedName_IsShortenedToFitAsync() {
    var name = CompositeIndexSql.Name(
      _index("AnExtremelyLongPropertyNameOne", "AnExtremelyLongPropertyNameTwo", "AndAThirdOne"),
      "averylongperspectivetableprefix");

    await Assert.That(name.Length).IsLessThanOrEqualTo(63)
      .Because("PostgreSQL truncates an over-long identifier rather than refusing it, so a name "
             + $"that does not fit is a collision waiting to happen. Name was: {name}");
  }

  /// <summary>
  /// Two over-long names that would truncate to the same thing stay distinct.
  /// </summary>
  /// <remarks>
  /// This is the failure the shortening exists to prevent, rather than merely fitting: both names
  /// share a long prefix and differ only at the end, which is exactly what truncation would discard.
  /// </remarks>
  [Test]
  public async Task TwoOverLongNamesSharingAPrefix_DoNotCollideAsync() {
    const string prefix = "averylongperspectivetableprefix";
    var first = CompositeIndexSql.Name(
      _index("AnExtremelyLongSharedPropertyName", "DiffersHereAlpha"), prefix);
    var second = CompositeIndexSql.Name(
      _index("AnExtremelyLongSharedPropertyName", "DiffersHereBravo"), prefix);

    await Assert.That(first).IsNotEqualTo(second)
      .Because("truncation alone would give both the same name, and the second CREATE INDEX IF NOT "
             + $"EXISTS would silently do nothing. Names were:\n{first}\n{second}");
  }

  /// <summary>
  /// The same declaration produces the same name on every run.
  /// </summary>
  /// <remarks>
  /// The suffix is a hash, and string.GetHashCode is randomized per process, so a name built from
  /// it would differ between builds: every build would look like a change to the incremental cache,
  /// and the schema pass would create a second index rather than recognizing its own.
  /// </remarks>
  [Test]
  public async Task AShortenedName_IsTheSameOnEveryRunAsync() {
    var index = _index("AnExtremelyLongSharedPropertyName", "AndAnotherLongOneHere");
    var first = CompositeIndexSql.Name(index, "averylongperspectivetableprefix");
    var second = CompositeIndexSql.Name(index, "averylongperspectivetableprefix");

    await Assert.That(first).IsEqualTo(second);
    await Assert.That(first.Length).IsLessThanOrEqualTo(63);
  }

  [Test]
  public async Task APartialIndex_SaysSoInItsDerivedNameAsync() {
    var partial = CompositeIndexSql.Name(
      new CompositeIndexInfo([new CompositeIndexElement("A", "a")], Where: "a IS NOT NULL"), "doc");
    var full = CompositeIndexSql.Name(_index("A"), "doc");

    await Assert.That(partial).IsNotEqualTo(full)
      .Because("a partial index over the same property is a different index, and one name for both "
             + "would leave whichever was created second missing.");
  }

  [Test]
  public async Task AUniqueDeclaration_EmitsAUniqueIndexAsync() {
    var statement = CompositeIndexSql.CreateStatement(
      new CompositeIndexInfo([new CompositeIndexElement("A", "a")], Unique: true), "s.t", "doc");

    await Assert.That(statement).Contains("CREATE UNIQUE INDEX IF NOT EXISTS", StringComparison.Ordinal);
  }

  [Test]
  public async Task AnEmptyDeclaration_EmitsNothingAsync() {
    var statement = CompositeIndexSql.CreateStatement(
      new CompositeIndexInfo(ImmutableArray<CompositeIndexElement>.Empty), "s.t", "doc");

    await Assert.That(statement).IsEmpty()
      .Because("an index over no properties is not an index; emitting one would be a syntax error "
             + "in the middle of the schema pass.");
  }
}
