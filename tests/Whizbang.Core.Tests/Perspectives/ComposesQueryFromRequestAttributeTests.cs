using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The marker an integration puts on its own attribute to declare that a request shapes the query.
/// </summary>
/// <remarks>
/// The analyzer reads this through Roslyn symbols, so nothing in the build path ever constructs it.
/// That makes its default worth pinning here rather than assuming: the default is what every
/// integration gets by writing <c>[ComposesQueryFromRequest]</c> with no argument, and a change to it
/// would silently widen or narrow every such declaration in every consumer at once.
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
public class ComposesQueryFromRequestAttributeTests {

  /// <summary>The default is what a sorting and filtering middleware offers.</summary>
  [Test]
  public async Task TheDefaultIsOrderingAndFilteringAsync() {
    var marker = new ComposesQueryFromRequestAttribute();

    await Assert.That(marker.Exposure)
      .IsEqualTo(QueryExposures.Ordering | QueryExposures.Filtering)
      .Because("an integration writing the marker with no argument is declaring the ordinary case, "
        + "which is a middleware that both orders and narrows");
  }

  /// <summary>An explicit exposure is kept as written.</summary>
  [Test]
  [Arguments(QueryExposures.Expression)]
  [Arguments(QueryExposures.Filtering)]
  [Arguments(QueryExposures.Ordering)]
  [Arguments(QueryExposures.None)]
  public async Task AnExplicitExposureIsKeptAsync(QueryExposures declared) {
    await Assert.That(new ComposesQueryFromRequestAttribute(declared).Exposure).IsEqualTo(declared);
  }
}
