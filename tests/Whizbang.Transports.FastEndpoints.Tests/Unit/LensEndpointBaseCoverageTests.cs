using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Coverage for the one <see cref="LensEndpointBase{TModel}"/> <c>ParseSortExpression</c> corner
/// <see cref="LensEndpointBaseTests"/> doesn't reach: a comma-separated field that survives
/// <c>Split(',', RemoveEmptyEntries)</c> because it isn't zero-length (a lone space, not an
/// empty string) but becomes empty once trimmed. Reuses <see cref="TestLensEndpoint"/> from
/// <see cref="LensEndpointBaseTests"/> — it is public, in this same test project.
/// </summary>
public class LensEndpointBaseCoverageTests {

  // A sort string built by joining UI-selected fields with commas can end up with a stray blank
  // between two real ones (a deselected middle chip, a trailing separator a client forgot to
  // strip). If that whitespace-only segment weren't skipped, it would either throw deep inside
  // sort-expression construction or -- worse -- produce a SortExpression with an empty field
  // name that then fails silently against the read model, dropping the sort the caller asked for
  // instead of just ignoring the blank and applying the two real fields.
  [Test]
  public async Task ParseSortExpression_WithWhitespaceOnlySegment_SkipsItButKeepsTheRealFieldsAsync() {
    var endpoint = new TestLensEndpoint();

    var sorts = endpoint.TestParseSortExpression("name, ,status");

    await Assert.That(sorts.Count).IsEqualTo(2)
      .Because("the whitespace-only middle segment trims to empty and must be skipped, not turned into a bogus SortExpression");
    await Assert.That(sorts[0].Field).IsEqualTo("name");
    await Assert.That(sorts[1].Field).IsEqualTo("status");
  }
}
