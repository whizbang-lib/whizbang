using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What the index-discovery helpers answer when they are asked about nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these takes a symbol and every caller gets that symbol from the semantic model, which
/// returns null for a type it could not resolve. That happens for real: a model referencing a type
/// from an assembly that failed to load, or mid-edit source in the IDE, where an analyzer runs against
/// a compilation that does not yet bind. So the null answer is a contract rather than a formality.
/// </para>
/// <para>
/// The answer each one gives is the safe direction, and that is the part worth pinning. An unresolved
/// type is not indexable, is not temporal, and is not polymorphic, so a null makes the framework do
/// nothing rather than emit an index over an expression it could not see, or classify a model by a
/// type it never read.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class JsonIndexDiscoveryGuardTests {

  /// <summary>An unresolved type carries no index, rather than one built over an unknown cast.</summary>
  [Test]
  public async Task AnUnresolvedTypeHasNoCastAsync() =>
    await Assert.That(JsonIndexDiscovery.CastFor(null)).IsNull()
      .Because("an index is built from a cast, and there is no cast for a type nobody could resolve");

  /// <summary>An unresolved type is not temporal, so nothing converts its stored form.</summary>
  /// <remarks>
  /// The wrong answer here would be the expensive one: classifying an unresolved type as temporal
  /// would emit a value conversion and a backfill for a property whose type was never read.
  /// </remarks>
  [Test]
  public async Task AnUnresolvedTypeIsNotTemporalAsync() =>
    await Assert.That(CanonicalTemporalDiscovery.KindOf(null)).IsEqualTo(CanonicalTemporalKind.None)
      .Because("a conversion and a backfill would be emitted for a type that was never read");

  /// <summary>An unresolved type does not force a model into opaque storage.</summary>
  /// <remarks>
  /// This is the guard whose absence was a real defect once before, from the other direction: a model
  /// wrongly classified as polymorphic loses indexing, containment rewriting and value conversion
  /// together, and nothing fails.
  /// </remarks>
  [Test]
  public async Task AnUnresolvedTypeIsNotPolymorphicAsync() =>
    await Assert.That(PolymorphicModelDiscovery.IsPolymorphicType(null!)).IsFalse()
      .Because("a model classified as polymorphic is stored as one value, which silently costs it "
        + "indexing, the containment rewrite and value conversion at once");

  /// <summary>A model that is not there declares no indexes.</summary>
  [Test]
  public async Task AModelThatIsNotThereDeclaresNothingAsync() =>
    await Assert.That(JsonIndexDiscovery.From(null).IsEmpty).IsTrue();

  /// <summary>
  /// A cast this SQL mapping does not name applies no cast, rather than guessing one.
  /// </summary>
  /// <remarks>
  /// Reached when the cast enumeration gains a member and the mapping does not. Answering null there
  /// builds the index over the bare extraction, which is a correct index over text rather than a
  /// statement naming a type PostgreSQL has never heard of, and the type's own test is what fails.
  /// </remarks>
  [Test]
  public async Task ACastWithNoSqlNameAppliesNoCastAsync() {
    var unmapped = (JsonIndexCast)9999;

    await Assert.That(JsonIndexSql.StoreType(unmapped)).IsNull();
    await Assert.That(JsonIndexSql.Expression("data", "Rank", unmapped))
      .IsEqualTo(JsonIndexSql.Expression("data", "Rank", JsonIndexCast.None))
      .Because("with no cast to apply it has to read exactly as the uncast form, not as a statement "
        + "naming a type that does not exist");
  }

  /// <summary>A declaration that is not there produces no statements, rather than a malformed one.</summary>
  /// <remarks>
  /// The schema pass runs this over every discovered field on every start, so the empty answer is
  /// what keeps one absent declaration from emitting <c>CREATE INDEX</c> over nothing and failing the
  /// initialization that every other perspective's schema is waiting behind.
  /// </remarks>
  [Test]
  public async Task ADeclarationThatIsNotThereCreatesNothingAsync() =>
    await Assert.That(JsonIndexSql.CreateStatements(null!, "public.wh_per_x", "x")).IsEmpty();

  /// <summary>A property that is not there is silent, which is not the same as opting out.</summary>
  /// <remarks>
  /// Null and zero are different answers here and the difference decides whether a model-level
  /// declaration still applies. Asserted alongside the opt-out so the two cannot be confused.
  /// </remarks>
  [Test]
  public async Task APropertyThatIsNotThereIsSilentRatherThanOptedOutAsync() =>
    await Assert.That(JsonIndexDiscovery.DeclaredKind(null)).IsNull()
      .Because("zero would mean the field declined an index, which overrides a blanket declaration; "
        + "null means it said nothing, so the blanket still applies");
}
