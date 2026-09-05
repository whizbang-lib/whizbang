using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Services;

public class SearchServiceTests : IDisposable {
  private readonly SearchService _sut = new();

  private static readonly List<SearchDocument> _sampleDocs =
  [
    new SearchDocument {
      Slug = "core-concepts/dispatcher",
      Title = "Dispatcher",
      Category = "Core Concepts",
      Content = "The Dispatcher routes commands and events to their handlers using SendAsync and PublishAsync methods.",
      Preview = "The Dispatcher routes commands and events..."
    },
    new SearchDocument {
      Slug = "core-concepts/receptor",
      Title = "Receptor",
      Category = "Core Concepts",
      Content = "A Receptor handles incoming commands by implementing the HandleAsync method.",
      Preview = "A Receptor handles incoming commands..."
    },
    new SearchDocument {
      Slug = "core-concepts/perspective",
      Title = "Perspective",
      Category = "Core Concepts",
      Content = "A Perspective builds read models by projecting events. It maintains a denormalized view of the data.",
      Preview = "A Perspective builds read models..."
    },
    new SearchDocument {
      Slug = "getting-started/quick-start",
      Title = "Quick Start Guide",
      Category = "Getting Started",
      Content = "Get up and running with Whizbang in minutes. Install the NuGet package and configure your first command.",
      Preview = "Get up and running with Whizbang..."
    }
  ];

  private void _buildDefaultIndex() {
    _sut.BuildIndex(_sampleDocs);
  }

  [Test]
  public async Task Search_ByTitle_FindsMatchAsync() {
    // Arrange
    _buildDefaultIndex();

    // Act
    var results = _sut.Search("dispatcher");

    // Assert
    await Assert.That(results.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(results.Any(r => r.Slug == "core-concepts/dispatcher")).IsTrue();
  }

  [Test]
  public async Task Search_ByContent_FindsMatchAsync() {
    // Arrange
    _buildDefaultIndex();

    // Act
    var results = _sut.Search("SendAsync");

    // Assert
    await Assert.That(results.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(results.Any(r => r.Slug == "core-concepts/dispatcher")).IsTrue();
  }

  [Test]
  public async Task Search_SynonymExpansion_FindsRelatedAsync() {
    // Arrange
    _buildDefaultIndex();
    _sut.SetSynonyms(new Dictionary<string, IReadOnlyList<string>> {
      ["perspective"] = ["read model", "projection", "view"]
    });

    // Act
    var results = _sut.Search("read model");

    // Assert
    await Assert.That(results.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(results.Any(r => r.Slug == "core-concepts/perspective")).IsTrue();
  }

  [Test]
  public async Task Search_FuzzyMatch_FindsTyposAsync() {
    // Arrange
    _buildDefaultIndex();

    // Act
    var results = _sut.Search("recptor");

    // Assert
    await Assert.That(results.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(results.Any(r => r.Slug == "core-concepts/receptor")).IsTrue();
  }

  [Test]
  public async Task Search_PrefixMatch_FindsPartialAsync() {
    // Arrange
    _buildDefaultIndex();

    // Act
    var results = _sut.Search("disp");

    // Assert
    await Assert.That(results.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(results.Any(r => r.Slug == "core-concepts/dispatcher")).IsTrue();
  }

  [Test]
  public async Task Search_ReturnsRankedResultsAsync() {
    // Arrange -- add a doc where "dispatcher" is only in content, not title
    var docs = new List<SearchDocument>(_sampleDocs) {
      new SearchDocument {
        Slug = "advanced/custom-pipeline",
        Title = "Custom Pipeline",
        Category = "Advanced",
        Content = "You can customize the dispatcher pipeline by adding middleware.",
        Preview = "You can customize the dispatcher pipeline..."
      }
    };
    _sut.BuildIndex(docs);

    // Act
    var results = _sut.Search("dispatcher");

    // Assert -- title match (core-concepts/dispatcher) should rank higher than content match
    await Assert.That(results.Count).IsGreaterThanOrEqualTo(2);
    var titleMatchIndex = results.ToList().FindIndex(r => r.Slug == "core-concepts/dispatcher");
    var contentMatchIndex = results.ToList().FindIndex(r => r.Slug == "advanced/custom-pipeline");
    await Assert.That(titleMatchIndex).IsLessThan(contentMatchIndex);
  }

  [Test]
  public async Task Search_EmptyQuery_ReturnsEmptyAsync() {
    // Arrange
    _buildDefaultIndex();

    // Act
    var resultsEmpty = _sut.Search("");
    var resultsWhitespace = _sut.Search("   ");

    // Assert
    await Assert.That(resultsEmpty.Count).IsEqualTo(0);
    await Assert.That(resultsWhitespace.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Search_NoResults_ReturnsEmptyAsync() {
    // Arrange
    _buildDefaultIndex();

    // Act
    var results = _sut.Search("xyznonexistentterm123");

    // Assert
    await Assert.That(results.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Search_LimitsResultsAsync() {
    // Arrange -- build index with 25 docs all containing "whizbang"
    var docs = Enumerable.Range(1, 25).Select(i => new SearchDocument {
      Slug = $"docs/page-{i}",
      Title = $"Whizbang Feature {i}",
      Category = "Docs",
      Content = $"This page covers whizbang feature number {i} in detail.",
      Preview = $"Whizbang feature {i}..."
    }).ToList();
    _sut.BuildIndex(docs);

    // Act
    var results = _sut.Search("whizbang");

    // Assert -- max 20 results
    await Assert.That(results.Count).IsLessThanOrEqualTo(20);
  }

  [Test]
  public async Task Search_DeduplicatesBySlugAsync() {
    // Arrange -- multiple chunks from same document
    var docs = new List<SearchDocument> {
      new SearchDocument {
        Slug = "core-concepts/dispatcher",
        Title = "Dispatcher",
        Category = "Core Concepts",
        Content = "The Dispatcher is the central routing component. Section 1.",
        Preview = "The Dispatcher is the central routing component..."
      },
      new SearchDocument {
        Slug = "core-concepts/dispatcher",
        Title = "Dispatcher",
        Category = "Core Concepts",
        Content = "The Dispatcher supports middleware and pipelines. Section 2.",
        Preview = "The Dispatcher supports middleware..."
      },
      new SearchDocument {
        Slug = "core-concepts/receptor",
        Title = "Receptor",
        Category = "Core Concepts",
        Content = "A Receptor handles commands dispatched by the Dispatcher.",
        Preview = "A Receptor handles commands..."
      }
    };
    _sut.BuildIndex(docs);

    // Act
    var results = _sut.Search("dispatcher");

    // Assert -- should deduplicate: only one result per slug
    var dispatcherResults = results.Where(r => r.Slug == "core-concepts/dispatcher").ToList();
    await Assert.That(dispatcherResults.Count).IsEqualTo(1);
  }


  // ── Robustness: queries that arrive before, around and after a usable index ──

  [Test]
  public async Task Search_BeforeTheIndexIsBuilt_ReturnsEmptyAsync() {
    // The editor sends queries as soon as it connects, which can beat index construction. This
    // has to answer "nothing yet" rather than throw -- an exception here surfaces as the whole
    // language server falling over mid-keystroke.
    using var unbuilt = new SearchService();

    var results = unbuilt.Search("dispatcher");

    await Assert.That(results).IsEmpty();
  }

  [Test]
  [Arguments("title:[")]
  [Arguments("AND")]
  [Arguments("content:(unclosed")]
  [Arguments("*")]
  public async Task Search_WithLuceneSyntaxTheUserDidNotIntend_DoesNotThrowAsync(string query) {
    // The search box takes free text, but the query is handed to a Lucene parser, so ordinary
    // characters are operators. Someone typing a bracket must get results or nothing -- never a
    // ParseException escaping into the editor.
    _buildDefaultIndex();

    await Assert.That(() => _sut.Search(query)).ThrowsNothing();
  }

  [Test]
  public async Task Search_AfterDispose_ReturnsEmptyRatherThanFailingAsync() {
    // Dispose clears the searcher, and a query can still arrive from an editor that has not
    // noticed the shutdown. The null-searcher guard is what keeps that quiet.
    var service = new SearchService();
    service.BuildIndex(_sampleDocs);
    service.Dispose();

    await Assert.That(service.Search("dispatcher")).IsEmpty();
  }

  [Test]
  public async Task Dispose_IsSafeToCallTwiceAsync() {
    // Held by DI and disposed by the test fixture too, so a double dispose is routine rather
    // than exotic.
    var service = new SearchService();
    service.BuildIndex(_sampleDocs);

    service.Dispose();

    await Assert.That(() => service.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task Dispose_WithoutAnIndex_IsSafeAsync() {
    // A server that fails before indexing still gets disposed on shutdown.
    var service = new SearchService();

    await Assert.That(() => service.Dispose()).ThrowsNothing();
  }

  [Test]
  public async Task BuildIndex_Twice_ReplacesTheIndexRatherThanAccumulatingAsync() {
    // The registry is rebuilt when documents change. A rebuild that appended would return two
    // hits for one document and grow the index for the life of the session.
    _sut.BuildIndex(_sampleDocs);
    _sut.BuildIndex(_sampleDocs);

    var results = _sut.Search("dispatcher");

    await Assert.That(results.Count(r => r.Slug == "core-concepts/dispatcher")).IsEqualTo(1);
  }

  [Test]
  public async Task BuildIndex_WithNoDocuments_LeavesSearchAnsweringEmptyAsync() {
    // An empty documentation set is a valid state, not an error.
    _sut.BuildIndex([]);

    await Assert.That(_sut.Search("dispatcher")).IsEmpty();
  }

  // ── Synonym expansion in the reverse direction ────────────────────────────

  [Test]
  public async Task Search_ForASynonymValue_FindsTheCanonicalTermAsync() {
    // Expansion has to work both ways. Somebody searching the word they know -- "router" --
    // should reach the page titled with the word the library uses, "Dispatcher"; matching only
    // in the canonical-to-synonym direction leaves half the vocabulary unreachable.
    _sut.SetSynonyms(new Dictionary<string, IReadOnlyList<string>> {
      ["dispatcher"] = ["router", "message bus"],
    });
    _buildDefaultIndex();

    var results = _sut.Search("router");

    await Assert.That(results.Any(r => r.Slug == "core-concepts/dispatcher")).IsTrue();
  }

  [Test]
  public async Task Search_ForAMultiWordSynonym_IsTreatedAsAPhraseAsync() {
    // A multi-word synonym has to be quoted before it reaches the parser, or its words become
    // separate OR terms and the expansion pulls in unrelated documents.
    _sut.SetSynonyms(new Dictionary<string, IReadOnlyList<string>> {
      ["dispatcher"] = ["message bus"],
    });
    _buildDefaultIndex();

    await Assert.That(() => _sut.Search("message bus")).ThrowsNothing();
    await Assert.That(_sut.Search("dispatcher").Any(r => r.Slug == "core-concepts/dispatcher")).IsTrue();
  }

  [Test]
  public async Task SetSynonyms_ReplacesThePreviousMapAsync() {
    // Synonyms are reloaded with the documentation. A stale entry would keep expanding a term
    // the current vocabulary no longer uses.
    _sut.SetSynonyms(new Dictionary<string, IReadOnlyList<string>> { ["dispatcher"] = ["router"] });
    _sut.SetSynonyms(new Dictionary<string, IReadOnlyList<string>> { ["perspective"] = ["projection"] });
    _buildDefaultIndex();

    var results = _sut.Search("projection");

    await Assert.That(results.Any(r => r.Slug == "core-concepts/perspective")).IsTrue();
  }

  public void Dispose() {
    _sut.Dispose();
    GC.SuppressFinalize(this);
  }
}
