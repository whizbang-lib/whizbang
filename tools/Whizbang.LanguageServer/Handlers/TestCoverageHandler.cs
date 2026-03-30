using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Handlers;

public sealed class TestCoverageHandler {
  private readonly TestCoverageService _testCoverageService;

  public TestCoverageHandler(TestCoverageService testCoverageService) {
    _testCoverageService = testCoverageService;
  }

  public IReadOnlyList<TestEntry> Handle(GetTestsForSymbolParams request) {
    var entries = _testCoverageService.GetTests(request.Symbol);

    return entries.Select(e => new TestEntry {
      TestFile = e.TestFile,
      TestMethod = e.TestMethod,
      TestClass = e.TestClass,
      LinkSource = e.LinkSource
    }).ToList();
  }
}
