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

  public void Dispose() {
    _sut.Dispose();
    GC.SuppressFinalize(this);
  }
}
