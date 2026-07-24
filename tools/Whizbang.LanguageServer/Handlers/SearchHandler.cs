using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Handlers;

public sealed class SearchHandler(SearchService searchService) {
  private readonly SearchService _searchService = searchService;

  public IReadOnlyList<SearchResult> Handle(SearchDocsParams request) {
    if (string.IsNullOrWhiteSpace(request.Query)) {
      return [];
    }

    return _searchService.Search(request.Query);
  }
}
