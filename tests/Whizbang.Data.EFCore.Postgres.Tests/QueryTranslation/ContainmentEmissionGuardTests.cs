using System.Globalization;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// What the containment emission does with a tree that is not the shape it understands.
/// </summary>
/// <remarks>
/// <para>
/// Both mechanisms end at one of two builders, and each builder guards the shapes it cannot express.
/// Those guards do not arise from any query a repository can write, which is exactly why they are
/// asserted here against hand-built expressions rather than left to be discovered: a future provider
/// version, a new mapping, or a translation this framework does not control can all produce one, and
/// what happens then is the difference between a lost index and a wrong answer.
/// </para>
/// <para>
/// The division between returning null and throwing is deliberate and is asserted as such.
/// <see cref="JsonbContainmentSql.TryBuild"/> is asked speculatively, so an inexpressible shape is a
/// null and the caller keeps the comparison it already had. <see cref="JsonbContainment.EmitSet"/> is
/// reached only after the rewriter has decided a path qualifies, so a shape arriving there is a defect
/// in the rewriter rather than something a user wrote, and it fails loudly instead of quietly
/// compiling a filter that matches nothing.
/// </para>
/// <para>
/// Nothing here opens a connection. A <c>ColumnExpression</c> and a <c>JsonScalarExpression</c> are
/// constructible directly, which is what makes these shapes reachable at all.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
[Category("Shard1")]
public class ContainmentEmissionGuardTests {
  private static readonly RelationalTypeMapping _jsonMapping = StringTypeMapping.Default;
  private static readonly RelationalTypeMapping _intMapping = IntTypeMapping.Default;

  /// <summary>An int stored as text, which is a converter that changes the stored form.</summary>
  private static readonly RelationalTypeMapping _convertedToText =
    (RelationalTypeMapping)IntTypeMapping.Default.WithComposedConverter(
      new ValueConverter<int, string>(v => v.ToString(CultureInfo.InvariantCulture),
                                      v => int.Parse(v, CultureInfo.InvariantCulture)));

  private static ColumnExpression _dataColumn(RelationalTypeMapping? mapping) =>
    new("data", "p", typeof(string), mapping, nullable: true);

  private static SqlExpression _value(int number) =>
    new SqlConstantExpression(number, typeof(int), _intMapping);

  /// <summary>A member at the given key, mapped the way a real one is.</summary>
  private static JsonScalarExpression _member(string key) =>
    new(_dataColumn(_jsonMapping), [new PathSegment(key)], typeof(int), _intMapping, nullable: true);

  /// <summary>
  /// The same member over a column carrying no type mapping.
  /// </summary>
  /// <remarks>
  /// Spelled as its own helper rather than as an optional argument to <see cref="_member"/>. An
  /// argument defaulted with <c>??</c> cannot express "explicitly none", and a first attempt at this
  /// silently substituted the real mapping and asserted the opposite of what it claimed.
  /// </remarks>
  private static JsonScalarExpression _unmappedMember(string key) =>
    new(_dataColumn(null), [new PathSegment(key)], typeof(int), _intMapping, nullable: true);

  /// <summary>A member reached by array position, which containment has no key for.</summary>
  private static JsonScalarExpression _positionalMember() =>
    new(_dataColumn(_jsonMapping), [new PathSegment(_value(0))], typeof(int), _intMapping, nullable: true);

  // ============================================================
  // JsonbContainmentSql.TryBuild — asked speculatively, answers null
  // ============================================================

  /// <summary>
  /// A column with no type mapping yields null, because the document has nothing to be built as.
  /// </summary>
  /// <remarks>
  /// The mapping is what says the column is jsonb, and <c>jsonb_build_object</c> has to be given it or
  /// the comparison is between a document and text. Returning null leaves the extraction in place,
  /// which costs an index and answers correctly.
  /// </remarks>
  [Test]
  public async Task AnUnmappedColumnIsNotBuiltIntoContainmentAsync() {
    var built = JsonbContainmentSql.TryBuild(_unmappedMember("Rank"), _value(7));

    await Assert.That(built).IsNull()
      .Because("without the column's mapping the containment operand would not be typed as a document");
  }

  /// <summary>
  /// An array position yields null, because containment compares keys and a position is not one.
  /// </summary>
  [Test]
  public async Task AnArrayPositionIsNotBuiltIntoContainmentAsync() {
    var built = JsonbContainmentSql.TryBuild(_positionalMember(), _value(7));

    await Assert.That(built).IsNull()
      .Because("a containment document is keys all the way down, and there is no key that means "
        + "\"the third element\"");
  }

  /// <summary>An ordinary member does build, so the two nulls above are the guards and not the rule.</summary>
  [Test]
  public async Task AnOrdinaryMemberDoesBuildAsync() {
    var built = JsonbContainmentSql.TryBuild(_member("Rank"), _value(7));

    await Assert.That(built).IsNotNull();
    await Assert.That(built!.ToString()).Contains("@>", StringComparison.Ordinal);
  }

  // ============================================================
  // JsonbContainment.Emit — falls back to the comparison it was given
  // ============================================================

  /// <summary>
  /// A translation that is not two arguments is a defect in the registration, not a shape to absorb.
  /// </summary>
  [Test]
  public async Task ATranslationWithTheWrongArityIsRefusedAsync() {
    await Assert.That(() => JsonbContainment.Emit([_member("Rank")]))
      .Throws<ArgumentException>()
      .Because("the marker takes a member and a value, so any other count means the wrong method was "
        + "registered and guessing which argument is which would compile a filter nothing asked for");
  }

  /// <summary>
  /// An operand that is not a JSON member keeps the equality, rather than becoming a containment test.
  /// </summary>
  /// <remarks>
  /// The fallback is the point. Losing an index is acceptable and changing an answer is not, so a shape
  /// this does not recognize compiles to the comparison the query originally expressed.
  /// </remarks>
  [Test]
  public async Task AMemberThatIsNotAJsonScalarFallsBackToEqualityAsync() {
    var emitted = JsonbContainment.Emit([_value(1), _value(7)]);

    await Assert.That(emitted).IsTypeOf<SqlBinaryExpression>();
    await Assert.That(((SqlBinaryExpression)emitted).OperatorType)
      .IsEqualTo(System.Linq.Expressions.ExpressionType.Equal)
      .Because("the comparison the query expressed is what must survive when the rewrite cannot apply");
  }

  /// <summary>An array position falls back the same way, since no key can be built for it.</summary>
  [Test]
  public async Task AnArrayPositionFallsBackToEqualityAsync() {
    var emitted = JsonbContainment.Emit([_positionalMember(), _value(7)]);

    await Assert.That(emitted).IsTypeOf<SqlBinaryExpression>();
  }

  /// <summary>
  /// A member whose stored form a converter changed falls back, which is the wrong-answer case rather
  /// than the lost-index one.
  /// </summary>
  /// <remarks>
  /// An <c>int</c> stored as text lands in the document as <c>"7"</c>. The marker's parameter is typed
  /// by the CLR type, so the document would be built with the number 7 and match no row at all, while
  /// looking fast. An extraction still matches, because <c>-&gt;&gt;</c> renders a JSON string and a
  /// JSON number as the same text.
  /// </remarks>
  [Test]
  public async Task AConvertedMemberFallsBackToEqualityAsync() {
    var converted = new JsonScalarExpression(
      _dataColumn(_jsonMapping), [new PathSegment("Rank")], typeof(int),
      _convertedToText,
      nullable: true);

    var emitted = JsonbContainment.Emit([converted, _value(7)]);

    await Assert.That(emitted).IsTypeOf<SqlBinaryExpression>()
      .Because("building the document unconverted would compare a number with a stored string and "
        + "return nothing, where the extraction it replaced returns the right rows");
  }

  // ============================================================
  // JsonbContainment.EmitSet — reached only by the rewriter, so it fails loudly
  // ============================================================

  /// <summary>A set translation over something that is not a JSON member is a defect, not a shape.</summary>
  [Test]
  [Arguments("wrong arity")]
  [Arguments("not a member")]
  public async Task ASetTranslationOverTheWrongShapeThrowsAsync(string shape) {
    IReadOnlyList<SqlExpression> args = shape switch {
      "wrong arity" => [_member("Tags")],
      "not a member" => [_value(1), _value(7)],
      _ => throw new InvalidOperationException(shape),
    };

    await Assert.That(() => JsonbContainment.EmitSet(args))
      .Throws<InvalidOperationException>()
      .Because("the rewriter only offers a path it has already checked, so a shape arriving here is a "
        + "defect in the rewriter and must not be absorbed into a filter that matches nothing");
  }

  /// <summary>A converted member throws rather than being compiled to a membership test.</summary>
  [Test]
  public async Task ASetTranslationOverAConvertedMemberThrowsAsync() {
    var converted = new JsonScalarExpression(
      _dataColumn(_jsonMapping), [new PathSegment("Rank")], typeof(int),
      _convertedToText,
      nullable: true);

    await Assert.That(() => JsonbContainment.EmitSet([converted, _value(7)]))
      .Throws<InvalidOperationException>();
  }

  /// <summary>
  /// A nested member throws, because the helper builds single-key documents and a nested one would
  /// need a document per candidate.
  /// </summary>
  /// <remarks>
  /// The rewriter offers only depth-one members for exactly this reason, so reaching here means the two
  /// have drifted apart. Producing one nested document per candidate needs a subquery, which is why the
  /// limit is real rather than an omission.
  /// </remarks>
  [Test]
  public async Task ASetTranslationOverANestedMemberThrowsAsync() {
    var nested = new JsonScalarExpression(
      _dataColumn(_jsonMapping), [new PathSegment("Inner"), new PathSegment("City")],
      typeof(int), _intMapping, nullable: true);

    await Assert.That(() => JsonbContainment.EmitSet([nested, _value(7)]))
      .Throws<InvalidOperationException>()
      .Because("the rewriter offers depth-one members only, so a nested one here is the two having "
        + "drifted apart rather than a query a caller wrote");
  }

  /// <summary>A positional segment throws for the same reason a nested one does.</summary>
  [Test]
  public async Task ASetTranslationOverAnArrayPositionThrowsAsync() =>
    await Assert.That(() => JsonbContainment.EmitSet([_positionalMember(), _value(7)]))
      .Throws<InvalidOperationException>();
}
