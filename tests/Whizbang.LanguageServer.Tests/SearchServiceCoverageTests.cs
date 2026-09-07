using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests;

/// <summary>
/// Coverage-round test for the one SearchService._expandQuery branch SearchServiceTests does
/// not reach: looking up an individual term (not the whole query) against the reverse synonym
/// map. SearchServiceTests' reverse-lookup case ("Search_ForASynonymValue_FindsTheCanonicalTermAsync")
/// uses a single-word query, which is caught by the earlier whole-query shortcut and returns
/// before the per-term loop runs at all. This test uses a multi-word query so the whole-query
/// lookup misses and the per-term reverse lookup -- the unit under test -- is what has to find
/// the match.
/// </summary>
/// <tests>Whizbang.LanguageServer/Services/SearchService.cs:*</tests>
public class SearchServiceCoverageTests : IDisposable {
  private readonly SearchService _sut = new();

  // A synonym search that only works when the synonym is the entire query would miss the
  // ordinary case of someone typing it as one word among several -- search would silently
  // return nothing for a large share of real queries typed in natural language.
  [Test]
  public async Task Search_ForASynonymValueEmbeddedInAMultiWordQuery_FindsTheCanonicalTermAsync() {
    // Arrange
    _sut.BuildIndex([
      new SearchDocument {
        Slug = "core-concepts/dispatcher",
        Title = "Dispatcher",
        Category = "Core Concepts",
        Content = "The Dispatcher routes commands and events to their handlers.",
        Preview = "The Dispatcher routes commands and events..."
      }
    ]);
    _sut.SetSynonyms(new Dictionary<string, IReadOnlyList<string>> {
      ["dispatcher"] = ["router", "message bus"],
    });

    // Act -- "router" is only one of three words, so the whole-query reverse lookup at the top
    // of _expandQuery misses and the per-term loop is what has to catch it.
    var results = _sut.Search("find router now");

    // Assert
    await Assert.That(results.Any(r => r.Slug == "core-concepts/dispatcher")).IsTrue()
      .Because("the per-term reverse-synonym lookup must expand 'router' to 'dispatcher' even when it isn't the whole query");
  }

  public void Dispose() {
    _sut.Dispose();
    GC.SuppressFinalize(this);
  }
}
