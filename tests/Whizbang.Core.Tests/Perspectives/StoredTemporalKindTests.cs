using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The kinds keep their ordinals, because the database function that rewrites a stored temporal
/// takes the kind as that ordinal.
/// </summary>
/// <remarks>
/// A convention between a C# enum and a SQL function has no compiler to enforce it. Reordering the
/// enum would make a day read as an offset instant and nothing would fail until a row did. This is
/// the assertion that would.
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
public class StoredTemporalKindTests {
  /// <summary>Each kind's ordinal is the number <c>wh_canonicalize_temporal</c> takes for it.</summary>
  [Test]
  [Arguments(StoredTemporalKind.Instant, 0)]
  [Arguments(StoredTemporalKind.OffsetInstant, 1)]
  [Arguments(StoredTemporalKind.Day, 2)]
  [Arguments(StoredTemporalKind.TimeOfDay, 3)]
  [Arguments(StoredTemporalKind.Duration, 4)]
  public async Task AKindKeepsItsOrdinalAsync(StoredTemporalKind kind, int ordinal) =>
    await Assert.That((int)kind).IsEqualTo(ordinal)
      .Because("the SQL function takes the kind as this number, and a reordered enum would rewrite "
        + "every row of one kind as another");

  /// <summary>There are exactly five, so the function's own list is complete.</summary>
  [Test]
  public async Task ThereAreExactlyFiveKindsAsync() =>
    await Assert.That(Enum.GetValues<StoredTemporalKind>().Length).IsEqualTo(5);
}
