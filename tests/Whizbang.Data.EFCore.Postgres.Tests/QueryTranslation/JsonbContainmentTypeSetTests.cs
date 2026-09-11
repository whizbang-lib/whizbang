using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Pins the set of types a containment rewrite is allowed to touch.
/// </summary>
/// <remarks>
/// <para>
/// The same set is duplicated in <c>PerspectiveFilterIndexAnalyzer</c>, which decides whether WHIZ302
/// should stay quiet because containment already indexes the filter. The duplication is forced: an
/// analyzer is referenced as an analyzer rather than as a library, so neither assembly can see the
/// other's list and no single test can compare them.
/// </para>
/// <para>
/// So each side pins its own, and each names the other. Adding a type here without adding it to
/// <c>EqualityContainmentCanServe_IsNotReportedAsync</c> in the generators tests leaves the analyzer
/// warning about a filter that is already a lookup; removing one without removing it there leaves a
/// filter that scans with no advisory. Neither produces a wrong answer, which is why the pin is a
/// list rather than a build break.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Shard1")]
public class JsonbContainmentTypeSetTests {
  /// <summary>
  /// Exactly these types, and the reason each is here: the text the serializer writes and the text
  /// PostgreSQL generates for the same value are the same, so a containment test means what the
  /// equality it replaces meant.
  /// </summary>
  [Test]
  public async Task EligibleTypes_AreExactlyTheOnesWhoseTextFormsAgreeAsync() {
    // Compared by name: a deep equivalence over Type recurses into members that throw for
    // non-generic types, and the names are what a reader of this test wants to see anyway.
    var expected = new[] {
      typeof(string), typeof(Guid), typeof(bool),
      typeof(short), typeof(int), typeof(long), typeof(decimal),
    }.Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    var actual = JsonbContainment.Overloads
      .Select(m => m.GetParameters()[0].ParameterType.FullName!)
      .OrderBy(n => n, StringComparer.Ordinal)
      .ToArray();

    await Assert.That(actual).IsEquivalentTo(expected);
  }

  /// <summary>
  /// The excluded types, named so that adding one is a deliberate act with a reason rather than an
  /// oversight. Dates and times, enumerations and binary floating point all have two text forms that
  /// are not guaranteed to agree, and a containment test that disagrees matches nothing silently.
  /// </summary>
  [Test]
  [Arguments(typeof(DateTime))]
  [Arguments(typeof(DateTimeOffset))]
  [Arguments(typeof(DateOnly))]
  [Arguments(typeof(TimeOnly))]
  [Arguments(typeof(double))]
  [Arguments(typeof(float))]
  [Arguments(typeof(byte))]
  [Arguments(typeof(char))]
  public async Task ExcludedTypes_HaveNoOverloadAsync(Type excluded) {
    await Assert.That(JsonbContainment.OverloadFor(excluded)).IsNull();
  }

  /// <summary>A nullable member resolves to its underlying overload, since only non-null values are rewritten.</summary>
  [Test]
  [Arguments(typeof(int?), typeof(int))]
  [Arguments(typeof(Guid?), typeof(Guid))]
  [Arguments(typeof(bool?), typeof(bool))]
  [Arguments(typeof(decimal?), typeof(decimal))]
  public async Task NullableTypes_ResolveToTheUnderlyingOverloadAsync(Type nullable, Type underlying) {
    var overload = JsonbContainment.OverloadFor(nullable);

    await Assert.That(overload).IsNotNull();
    await Assert.That(overload!.GetParameters()[0].ParameterType).IsEqualTo(underlying);
  }
}
