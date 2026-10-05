// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for the ordering comparisons (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) in the
/// collective WHERE compiler: a jsonb member is read as a number (a temporal as its stored microsecond count), a
/// physical column is compared as the column it is, a <c>??</c> coalesce becomes <c>COALESCE</c>, and a comparison
/// under <c>!</c> is made null-safe so it agrees with the C# the replay evaluates.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectivePredicateOrderingTests {
  private const string TABLE = "wh_per_ordered";

  static CollectivePredicateOrderingTests() {
    // What the perspective runner's [ModuleInitializer] emits for a [PhysicalField] property.
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderedModel), nameof(OrderedModel.Rank), "rank", FieldStorageMode.Split);
    PerspectivePhysicalFieldRegistry.Register(typeof(OrderedModel), nameof(OrderedModel.SeenAt), "seen_at", FieldStorageMode.Split);
  }

  [SuppressIndexAdvisory("test fixture; compiled to SQL text, never queried")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "The members are read by the expression trees under test; their setters exist for the model shape.")]
  private sealed class OrderedModel {
    public long Ordinal { get; init; }
    public long? MaybeOrdinal { get; init; }
    public int Small { get; init; }
    public decimal Amount { get; init; }
    public double Score { get; init; }
    public float Ratio { get; init; }
    public ulong Huge { get; init; }
    public DateTimeOffset ActivatedAt { get; init; }
    public DateTime StampedAt { get; init; }
    public DateOnly Day { get; init; }
    public TimeOnly OpensAt { get; init; }
    public TimeSpan Window { get; init; }
    public Stage Stage { get; init; }
    public char Letter { get; init; }
    public int Rank { get; init; }
    public DateTimeOffset SeenAt { get; init; }
  }

  private enum Stage { Draft, Active, Retired }

  [SuppressIndexAdvisory("test fixture; compiled to SQL text, never queried")]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "Read by the expression tree under test.")]
  private sealed class SiblingModel {
    public long Ordinal { get; init; }
  }

  private static CollectivePredicateSqlCompiler<OrderedModel>.CompiledWhereClause _compile(
      Expression<Func<PerspectiveRow<OrderedModel>, bool>> filter) =>
    CollectivePredicateSqlCompiler<OrderedModel>.Compile(filter, outerTableName: TABLE);

  // ── The four operators over a jsonb number ──────────────────────────────

  [Test]
  public async Task Compile_LessThanOnAJsonbNumber_ReadsTheKeyAsANumericAsync() {
    var result = _compile(r => r.Data.Ordinal < 5);

    await Assert.That(result.SqlFragment).IsEqualTo("(data->>'Ordinal')::numeric < @where_ordinal")
      .Because("->> is text, and text orders '10' before '9'. The comparison has to be numeric.");
    await Assert.That(result.Parameters["where_ordinal"]).IsEqualTo(5L);
  }

  [Test]
  public async Task Compile_LessThanOrEqual_EmitsLessThanOrEqualAsync() {
    var result = _compile(r => r.Data.Ordinal <= 5);
    await Assert.That(result.SqlFragment).IsEqualTo("(data->>'Ordinal')::numeric <= @where_ordinal");
  }

  [Test]
  public async Task Compile_GreaterThan_EmitsGreaterThanAsync() {
    var result = _compile(r => r.Data.Ordinal > 5);
    await Assert.That(result.SqlFragment).IsEqualTo("(data->>'Ordinal')::numeric > @where_ordinal");
  }

  [Test]
  public async Task Compile_GreaterThanOrEqual_EmitsGreaterThanOrEqualAsync() {
    var result = _compile(r => r.Data.Ordinal >= 5);
    await Assert.That(result.SqlFragment).IsEqualTo("(data->>'Ordinal')::numeric >= @where_ordinal");
  }

  [Test]
  public async Task Compile_ValueOnTheLeft_KeepsTheOperandOrderAsync() {
    var result = _compile(r => 5 < r.Data.Ordinal);
    await Assert.That(result.SqlFragment).IsEqualTo("@where_ordinal < (data->>'Ordinal')::numeric");
    await Assert.That(result.Parameters["where_ordinal"]).IsEqualTo(5L);
  }

  [Test]
  public async Task Compile_CapturedEventValue_BindsTheCapturedValueAsync() {
    var incoming = new { Ordinal = 42L };
    var result = _compile(r => r.Data.Ordinal < incoming.Ordinal);
    await Assert.That(result.Parameters["where_ordinal"]).IsEqualTo(42L);
  }

  [Test]
  public async Task Compile_OrderingInsideAnAndChain_ComposesWithEqualityAsync() {
    var result = _compile(r => r.Scope.TenantId == "t-1" && r.Data.Ordinal < 9);
    await Assert.That(result.SqlFragment)
      .IsEqualTo("(scope->>'t' = @where_tenantid AND (data->>'Ordinal')::numeric < @where_ordinal)");
  }

  [Test]
  public async Task Compile_OrderingOnAJsonbPath_RecordsTheNumericExpressionAsAnIndexCandidateAsync() {
    var result = _compile(r => r.Data.Ordinal < 5);
    await Assert.That(result.ReferencedJsonPaths)
      .IsEquivalentTo([new ReferencedJsonPath(TABLE, "(data->>'Ordinal')::numeric")])
      .Because("A btree index over the text extraction cannot serve a numeric range; the candidate must be the expression the WHERE compares.");
  }

  // ── Numeric binding ─────────────────────────────────────────────────────

  [Test]
  public async Task Compile_IntMember_BindsTheValueAsALongAsync() {
    var result = _compile(r => r.Data.Small > 3);
    await Assert.That(result.Parameters["where_small"]).IsEqualTo(3L);
  }

  [Test]
  public async Task Compile_DecimalMember_BindsTheDecimalAsync() {
    var result = _compile(r => r.Data.Amount >= 2.5m);
    await Assert.That(result.Parameters["where_amount"]).IsEqualTo(2.5m);
  }

  [Test]
  public async Task Compile_DoubleMember_BindsTheDoubleAsync() {
    var result = _compile(r => r.Data.Score < 0.75);
    await Assert.That(result.Parameters["where_score"]).IsEqualTo(0.75);
  }

  [Test]
  public async Task Compile_FloatMember_BindsTheFloatAsync() {
    var result = _compile(r => r.Data.Ratio < 0.5f);
    await Assert.That(result.Parameters["where_ratio"]).IsEqualTo(0.5f);
  }

  [Test]
  public async Task Compile_UnsignedLongMemberWithinLongRange_BindsALongAsync() {
    var result = _compile(r => r.Data.Huge > 7UL);
    await Assert.That(result.Parameters["where_huge"]).IsEqualTo(7L);
  }

  [Test]
  public async Task Compile_UnsignedLongMember_BindsADecimalAsync() {
    var result = _compile(r => r.Data.Huge > ulong.MaxValue - 1);
    await Assert.That(result.Parameters["where_huge"]).IsEqualTo((decimal)(ulong.MaxValue - 1))
      .Because("No Postgres integer holds every ulong, and numeric does.");
  }

  [Test]
  public async Task Compile_EnumMember_BindsItsUnderlyingNumberAsync() {
    var result = _compile(r => r.Data.Stage > Stage.Draft);
    await Assert.That(result.SqlFragment).IsEqualTo("(data->>'Stage')::numeric > @where_stage");
    await Assert.That(result.Parameters["where_stage"]).IsEqualTo(0L)
      .Because("A document stores an enumeration as its underlying number.");
  }

  [Test]
  public async Task Compile_NullComparisonValue_BindsNullAsync() {
    long? unset = null;
    var result = _compile(r => r.Data.MaybeOrdinal < unset);
    await Assert.That(result.Parameters["where_maybeordinal"]).IsNull();
  }

  // ── Temporal binding: the stored microsecond count ──────────────────────

  [Test]
  public async Task Compile_DateTimeOffsetMember_BindsEpochMicrosecondsAsync() {
    var cutoff = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2));
    var result = _compile(r => r.Data.ActivatedAt < cutoff);

    await Assert.That(result.SqlFragment).IsEqualTo("(data->>'ActivatedAt')::numeric < @where_activatedat");
    await Assert.That(result.Parameters["where_activatedat"]).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(cutoff))
      .Because("A document stores an instant as microseconds since the epoch, so the bound value must be the same number.");
  }

  [Test]
  public async Task Compile_DateTimeMember_BindsEpochMicrosecondsAsync() {
    var cutoff = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
    var result = _compile(r => r.Data.StampedAt >= cutoff);
    await Assert.That(result.Parameters["where_stampedat"]).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(cutoff));
  }

  [Test]
  public async Task Compile_DateOnlyMember_BindsEpochMicrosecondsAtMidnightAsync() {
    var day = new DateOnly(2026, 3, 1);
    var result = _compile(r => r.Data.Day > day);
    await Assert.That(result.Parameters["where_day"]).IsEqualTo(CanonicalTemporalFormat.ToEpochMicroseconds(day));
  }

  [Test]
  public async Task Compile_TimeOnlyMember_BindsMicrosecondsOfDayAsync() {
    var opens = new TimeOnly(9, 30);
    var result = _compile(r => r.Data.OpensAt <= opens);
    await Assert.That(result.Parameters["where_opensat"]).IsEqualTo(CanonicalTemporalFormat.ToMicrosecondsOfDay(opens));
  }

  [Test]
  public async Task Compile_TimeSpanMember_BindsMicrosecondsAsync() {
    var window = TimeSpan.FromMinutes(90);
    var result = _compile(r => r.Data.Window > window);
    await Assert.That(result.Parameters["where_window"]).IsEqualTo(CanonicalTemporalFormat.ToMicroseconds(window));
  }

  // ── Missing keys, null and coalescing ───────────────────────────────────

  [Test]
  public async Task Compile_CoalescedMember_EmitsCoalesceWithTheDefaultBoundAsync() {
    var result = _compile(r => (r.Data.MaybeOrdinal ?? 0) < 7);

    await Assert.That(result.SqlFragment)
      .IsEqualTo("COALESCE((data->>'MaybeOrdinal')::numeric, @where_maybeordinal_else) < @where_maybeordinal")
      .Because("A missing key reads as null; ?? is how a caller says what a missing key counts as.");
    await Assert.That(result.Parameters["where_maybeordinal_else"]).IsEqualTo(0L);
    await Assert.That(result.Parameters["where_maybeordinal"]).IsEqualTo(7L);
  }

  [Test]
  public async Task Compile_CoalescedToDefault_BindsTheTypesDefaultAsync() {
    var result = _compile(r => (r.Data.MaybeOrdinal ?? default) < 7);
    await Assert.That(result.Parameters["where_maybeordinal_else"]).IsEqualTo(0L);
  }

  [Test]
  public async Task Compile_CoalescedValueSide_EvaluatesTheCoalesceAsync() {
    long? absent = null;
    var result = _compile(r => r.Data.Ordinal < (absent ?? 11));
    await Assert.That(result.Parameters["where_ordinal"]).IsEqualTo(11L);
  }

  [Test]
  public async Task Compile_CoalescedValueSideWithAValue_UsesTheValueAsync() {
    long? present = 4;
    var result = _compile(r => r.Data.Ordinal < (present ?? 11));
    await Assert.That(result.Parameters["where_ordinal"]).IsEqualTo(4L);
  }

  [Test]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1940:Boolean checks should not be inverted", Justification = "The negated ordering is the case under test.")]
  public async Task Compile_OrderingUnderNot_IsNullSafeAsync() {
#pragma warning disable RCS1068 // the negation of an ordering is the shape under test
    var result = _compile(r => !(r.Data.MaybeOrdinal < 5));
#pragma warning restore RCS1068

    await Assert.That(result.SqlFragment)
      .IsEqualTo("NOT (COALESCE((data->>'MaybeOrdinal')::numeric < @where_maybeordinal, FALSE))")
      .Because("In C# !(null < 5) is true. In SQL NOT (NULL) is NULL and drops the row, so the comparison must be made "
        + "false before it is negated or live and replay disagree on every row with a missing key.");
  }

  // ── Physical columns ────────────────────────────────────────────────────

  [Test]
  public async Task Compile_OrderingOnAPhysicalColumn_ComparesTheColumnAsync() {
    var result = _compile(r => r.Data.Rank >= 3);

    await Assert.That(result.SqlFragment).IsEqualTo("\"rank\" >= @where_rank");
    await Assert.That(result.Parameters["where_rank"]).IsEqualTo(3)
      .Because("A physical column is typed; the value binds as the scalar it stores.");
    await Assert.That(result.ReferencedJsonPaths.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Compile_OrderingOnAPhysicalTimestamp_BindsTheInstantInUtcAsync() {
    var cutoff = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2));
    var result = _compile(r => r.Data.SeenAt < cutoff);

    await Assert.That(result.SqlFragment).IsEqualTo("\"seen_at\" < @where_seenat");
    var bound = (DateTimeOffset)result.Parameters["where_seenat"]!;
    await Assert.That(bound).IsEqualTo(cutoff);
    await Assert.That(bound.Offset).IsEqualTo(TimeSpan.Zero)
      .Because("Npgsql writes only a UTC offset to timestamptz; the instant is unchanged.");
  }

  [Test]
  public async Task Compile_CoalescedPhysicalColumn_EmitsCoalesceOverTheColumnAsync() {
    var result = _compile(r => ((int?)r.Data.Rank ?? 1) < 3);
    await Assert.That(result.SqlFragment).IsEqualTo("COALESCE(\"rank\", @where_rank_else) < @where_rank");
    await Assert.That(result.Parameters["where_rank_else"]).IsEqualTo(1);
  }

  // ── Column against column ───────────────────────────────────────────────

  [Test]
  public async Task Compile_SiblingColumnAgainstOuterColumn_ComparesBothAsNumbersAsync() {
    var q = new DapperCollectiveQuery(new Dictionary<Type, string> { [typeof(SiblingModel)] = "wh_per_sibling" });
    var result = _compile(r => q.Of<SiblingModel>().Any(s => s.Id == r.Id && s.Data.Ordinal < r.Data.Ordinal));

    await Assert.That(result.SqlFragment).IsEqualTo(
      "EXISTS (SELECT 1 FROM wh_per_sibling s WHERE (s.id = wh_per_ordered.id AND "
      + "(s.data->>'Ordinal')::numeric < (wh_per_ordered.data->>'Ordinal')::numeric))");
    await Assert.That(result.Parameters.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Compile_PhysicalTimestampAgainstJsonbTimestamp_ThrowsNotSupportedAsync() {
    await Assert.That(() => _compile(r => r.Data.SeenAt < r.Data.ActivatedAt))
      .Throws<NotSupportedException>()
      .Because("A timestamptz column and a microsecond count are not the same scale.");
  }

  [Test]
  public async Task Compile_PhysicalNumberAgainstJsonbNumber_ComparesAsync() {
    var result = _compile(r => r.Data.Rank < r.Data.Ordinal);
    await Assert.That(result.SqlFragment).IsEqualTo("\"rank\" < (data->>'Ordinal')::numeric");
  }

  // ── Refusals ────────────────────────────────────────────────────────────

  [Test]
  public async Task Compile_OrderingOnTheRowId_ThrowsNotSupportedAsync() {
    var id = Guid.NewGuid();
    await Assert.That(() => _compile(r => r.Id < id))
      .Throws<NotSupportedException>()
      .Because("Postgres orders uuid bytewise and .NET orders Guid by its fields; live and replay would disagree.");
  }

  [Test]
  public async Task Compile_OrderingOnACharMember_ThrowsNotSupportedAsync() {
    await Assert.That(() => _compile(r => r.Data.Letter < 'm'))
      .Throws<NotSupportedException>()
      .Because("A char is stored as a JSON string; only numeric and temporal members order.");
  }

  [Test]
  public async Task Compile_OrderingBetweenTwoValues_ThrowsNotSupportedAsync() {
    var a = 1L;
    var b = 2L;
    await Assert.That(() => _compile(_ => a < b))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Compile_CoalesceOverANonColumn_ThrowsNotSupportedAsync() {
    long? a = null;
    await Assert.That(() => _compile(r => (a ?? r.Data.Ordinal) < 3))
      .Throws<NotSupportedException>();
  }

  [Test]
  public async Task Compile_NumberAgainstATimestamp_ThrowsNotSupportedAsync() {
    // A long and a DateTimeOffset cannot be compared in C#; a converted operand could reach the compiler only
    // through a hand-built tree, which is exactly what this builds.
    var row = Expression.Parameter(typeof(PerspectiveRow<OrderedModel>), "r");
    var data = Expression.Property(row, nameof(PerspectiveRow<>.Data));
    var body = Expression.LessThan(
      Expression.Convert(Expression.Property(data, nameof(OrderedModel.ActivatedAt)), typeof(object)),
      Expression.Convert(Expression.Property(data, nameof(OrderedModel.Ordinal)), typeof(object)),
      liftToNull: false,
      method: typeof(CollectivePredicateOrderingTests).GetMethod(nameof(ObjectLessThan)));
    var filter = Expression.Lambda<Func<PerspectiveRow<OrderedModel>, bool>>(body, row);

    await Assert.That(() => _compile(filter)).Throws<NotSupportedException>();
  }

  /// <summary>An operator method for a hand-built comparison of two boxed operands. Never invoked.</summary>
  public static bool ObjectLessThan(object left, object right) => ReferenceEquals(left, right);
}
