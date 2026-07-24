using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Handlers;

public class TestCoverageHandlerTests {
  [Test]
  public async Task HandleGetTests_KnownSymbol_ReturnsTestsAsync() {
    // Arrange
    var service = new TestCoverageService();
    service.SetData(new Dictionary<string, IReadOnlyList<TestCoverageEntry>> {
      ["Whizbang.Core.Dispatcher"] = [
        new TestCoverageEntry {
          TestFile = "DispatcherTests.cs",
          TestMethod = "Dispatch_SendsMessageAsync",
          TestClass = "DispatcherTests",
          LinkSource = "convention"
        },
        new TestCoverageEntry {
          TestFile = "DispatcherTests.cs",
          TestMethod = "Dispatch_ThrowsOnNullAsync",
          TestClass = "DispatcherTests",
          LinkSource = "convention"
        }
      ]
    });

    var handler = new TestCoverageHandler(service);
    var request = new GetTestsForSymbolParams { Symbol = "Dispatcher" };

    // Act
    var results = handler.Handle(request);

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results[0].TestFile).IsEqualTo("DispatcherTests.cs");
    await Assert.That(results[0].TestMethod).IsEqualTo("Dispatch_SendsMessageAsync");
  }

  [Test]
  public async Task HandleGetTests_UnknownSymbol_ReturnsEmptyAsync() {
    // Arrange
    var service = new TestCoverageService();
    var handler = new TestCoverageHandler(service);
    var request = new GetTestsForSymbolParams { Symbol = "NonExistent" };

    // Act
    var results = handler.Handle(request);

    // Assert
    await Assert.That(results).IsEmpty();
  }
}
