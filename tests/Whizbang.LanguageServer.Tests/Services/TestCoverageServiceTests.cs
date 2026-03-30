using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Services;

public class TestCoverageServiceTests {
  private static TestCoverageService _createSutWithSampleData() {
    var sut = new TestCoverageService();
    var data = new Dictionary<string, IReadOnlyList<TestCoverageEntry>> {
      ["Whizbang.Core.IDispatcher"] = new List<TestCoverageEntry> {
        new() { TestFile = "DispatcherTests.cs", TestMethod = "Dispatch_SendsMessageAsync", TestClass = "DispatcherTests", LinkSource = "convention" },
        new() { TestFile = "DispatcherTests.cs", TestMethod = "Dispatch_ThrowsOnNullAsync", TestClass = "DispatcherTests", LinkSource = "convention" },
        new() { TestFile = "DispatcherTests.cs", TestMethod = "Dispatch_RoutesToCorrectHandlerAsync", TestClass = "DispatcherTests", LinkSource = "convention" },
        new() { TestFile = "IntegrationTests.cs", TestMethod = "FullPipeline_DispatchesAsync", TestClass = "IntegrationTests", LinkSource = "tag" },
        new() { TestFile = "IntegrationTests.cs", TestMethod = "FullPipeline_RetriesAsync", TestClass = "IntegrationTests", LinkSource = "tag" },
      },
      ["Whizbang.Core.IReceptor"] = new List<TestCoverageEntry> {
        new() { TestFile = "ReceptorTests.cs", TestMethod = "Handle_ProcessesMessageAsync", TestClass = "ReceptorTests", LinkSource = "convention" },
        new() { TestFile = "ReceptorTests.cs", TestMethod = "Handle_RejectsInvalidAsync", TestClass = "ReceptorTests", LinkSource = "convention" },
      },
    };
    sut.SetData(data);
    return sut;
  }

  [Test]
  public async Task GetTests_KnownSymbol_ReturnsTestsAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act
    var result = sut.GetTests("Whizbang.Core.IDispatcher");

    // Assert
    await Assert.That(result).Count().IsEqualTo(5);
    await Assert.That(result[0].TestMethod).IsEqualTo("Dispatch_SendsMessageAsync");
    await Assert.That(result[4].TestMethod).IsEqualTo("FullPipeline_RetriesAsync");
  }

  [Test]
  public async Task GetTests_UnknownSymbol_ReturnsEmptyAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act
    var result = sut.GetTests("NonExistent");

    // Assert
    await Assert.That(result).Count().IsEqualTo(0);
  }

  [Test]
  public async Task GetTests_PartialMatch_FindsEndsWithAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act -- "IDispatcher" should match "Whizbang.Core.IDispatcher" via EndsWith
    var result = sut.GetTests("IDispatcher");

    // Assert
    await Assert.That(result).Count().IsEqualTo(5);
  }

  [Test]
  public async Task GetTestCount_ReturnsCorrectCountAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act
    var count = sut.GetTestCount();

    // Assert -- 5 for IDispatcher + 2 for IReceptor = 7
    await Assert.That(count).IsEqualTo(7);
  }

  [Test]
  public async Task GetTestedSymbols_ReturnsAllSymbolNamesAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act
    var symbols = sut.GetTestedSymbols();

    // Assert
    await Assert.That(symbols).Count().IsEqualTo(2);
    await Assert.That(symbols).Contains("Whizbang.Core.IDispatcher");
    await Assert.That(symbols).Contains("Whizbang.Core.IReceptor");
  }

  [Test]
  public async Task GetCodeForTest_ReverseMatchAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act -- DispatcherTests maps back to IDispatcher
    var codeSymbols = sut.GetCodeForTest("DispatcherTests");

    // Assert
    await Assert.That(codeSymbols).Count().IsEqualTo(1);
    await Assert.That(codeSymbols).Contains("Whizbang.Core.IDispatcher");
  }

  [Test]
  public async Task GetCodeForTest_MultipleSymbols_ReturnsAllAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act -- IntegrationTests maps to IDispatcher (has entries with TestClass=IntegrationTests)
    var codeSymbols = sut.GetCodeForTest("IntegrationTests");

    // Assert
    await Assert.That(codeSymbols).Count().IsEqualTo(1);
    await Assert.That(codeSymbols).Contains("Whizbang.Core.IDispatcher");
  }

  [Test]
  public async Task SetData_UpdatesLookupAsync() {
    // Arrange
    var sut = _createSutWithSampleData();

    // Act -- replace with new data
    var newData = new Dictionary<string, IReadOnlyList<TestCoverageEntry>> {
      ["Whizbang.Core.IPerspective"] = new List<TestCoverageEntry> {
        new() { TestFile = "PerspectiveTests.cs", TestMethod = "Apply_WorksAsync", TestClass = "PerspectiveTests" },
      },
    };
    sut.SetData(newData);

    // Assert -- old data gone, new data present
    await Assert.That(sut.GetTests("Whizbang.Core.IDispatcher")).Count().IsEqualTo(0);
    await Assert.That(sut.GetTests("Whizbang.Core.IPerspective")).Count().IsEqualTo(1);
    await Assert.That(sut.GetTestCount()).IsEqualTo(1);
    await Assert.That(sut.GetTestedSymbols()).Count().IsEqualTo(1);
  }

  [Test]
  public async Task GetTests_MultipleEntries_ReturnsAllAsync() {
    // Arrange
    var sut = new TestCoverageService();
    var entries = new List<TestCoverageEntry>();
    for (var i = 0; i < 25; i++) {
      entries.Add(new TestCoverageEntry {
        TestFile = $"TestFile{i}.cs",
        TestMethod = $"TestMethod{i}Async",
        TestClass = $"TestClass{i}",
        LinkSource = "convention",
      });
    }
    var data = new Dictionary<string, IReadOnlyList<TestCoverageEntry>> {
      ["Whizbang.Core.BigSymbol"] = entries,
    };
    sut.SetData(data);

    // Act
    var result = sut.GetTests("Whizbang.Core.BigSymbol");

    // Assert -- all 25 tests returned
    await Assert.That(result).Count().IsEqualTo(25);
    await Assert.That(result[0].TestMethod).IsEqualTo("TestMethod0Async");
    await Assert.That(result[24].TestMethod).IsEqualTo("TestMethod24Async");
  }
}
