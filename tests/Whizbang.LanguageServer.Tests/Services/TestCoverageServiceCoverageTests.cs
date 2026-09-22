using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Services;

/// <summary>
/// Coverage-round tests for <see cref="TestCoverageService"/> targeting the reverse-lookup miss
/// path in GetCodeForTest. The primary suite only ever looks up test class names that are
/// actually present in the reverse index.
/// </summary>
/// <tests>Whizbang.LanguageServer/Services/TestCoverageService.cs:68</tests>
public class TestCoverageServiceCoverageTests {

  // The VSCode extension's "jump from test to code" command calls GetCodeForTest for whatever
  // class the developer's cursor is in. If the miss path threw or returned null instead of an
  // empty list, invoking that command from a test class the coverage map has never indexed
  // (a brand-new test file, say) would crash the extension instead of just showing
  // "no linked code found".
  [Test]
  public async Task GetCodeForTest_UnknownTestClass_ReturnsEmptyAsync() {
    // Arrange
    var sut = new TestCoverageService();
    sut.SetData(new Dictionary<string, IReadOnlyList<TestCoverageEntry>> {
      ["Whizbang.Core.IDispatcher"] = [
        new() { TestFile = "DispatcherTests.cs", TestMethod = "Dispatch_SendsMessageAsync", TestClass = "DispatcherTests" }
      ]
    });

    // Act
    var codeSymbols = sut.GetCodeForTest("SomeUnrelatedTestClass");

    // Assert
    await Assert.That(codeSymbols).IsEmpty()
      .Because("a test class the reverse index has never seen must yield an empty result, not "
             + "null or a thrown exception");
  }
}
