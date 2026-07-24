using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Handlers;

public class SearchHandlerTests {
  [Test]
  public async Task HandleSearch_ValidQuery_ReturnsResultsAsync() {
    // Arrange
    var searchService = new SearchService();
    searchService.BuildIndex([
      new SearchDocument {
        Slug = "getting-started",
        Title = "Getting Started",
        Category = "Guides",
        Content = "How to get started with Whizbang",
        Preview = "Learn the basics"
      },
      new SearchDocument {
        Slug = "dispatcher",
        Title = "Dispatcher",
        Category = "Core Concepts",
        Content = "The dispatcher routes messages to handlers",
        Preview = "Message routing"
      }
    ]);

    var handler = new SearchHandler(searchService);
    var request = new SearchDocsParams { Query = "dispatcher" };

    // Act
    var results = handler.Handle(request);

    // Assert
    await Assert.That(results).IsNotEmpty();
    await Assert.That(results[0].Title).Contains("Dispatcher");
  }

  [Test]
  public async Task HandleSearch_EmptyQuery_ReturnsEmptyAsync() {
    // Arrange
    var searchService = new SearchService();
    searchService.BuildIndex([
      new SearchDocument {
        Slug = "test",
        Title = "Test",
        Category = "Test",
        Content = "Test content",
        Preview = "Test preview"
      }
    ]);

    var handler = new SearchHandler(searchService);
    var request = new SearchDocsParams { Query = "" };

    // Act
    var results = handler.Handle(request);

    // Assert
    await Assert.That(results).IsEmpty();
  }
}
