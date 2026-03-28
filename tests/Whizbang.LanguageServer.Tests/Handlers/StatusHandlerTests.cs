using Whizbang.LanguageServer.Debug;
using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Handlers;

public class StatusHandlerTests {
  [Test]
  public async Task HandleGetStatus_ReturnsCountsAsync() {
    // Arrange
    var resolver = new SymbolResolver("https://docs.whizbang.dev");
    resolver.SetRegistryData([
      new MessageRegistryEntry {
        Type = "CreateOrderCommand",
        IsCommand = true,
        IsEvent = false,
        DispatcherCount = 1,
        ReceptorCount = 1,
        PerspectiveCount = 0,
        TestCount = 2
      },
      new MessageRegistryEntry {
        Type = "OrderCreatedEvent",
        IsCommand = false,
        IsEvent = true,
        DispatcherCount = 1,
        ReceptorCount = 0,
        PerspectiveCount = 2,
        TestCount = 3
      }
    ]);

    resolver.SetVscodeFeedData(new Dictionary<string, VscodeFeedEntry> {
      ["IDispatcher"] = new VscodeFeedEntry {
        Docs = "core-concepts/dispatcher",
        Title = "Dispatcher"
      }
    });

    var testCoverage = new TestCoverageService();
    testCoverage.SetData(new Dictionary<string, IReadOnlyList<TestCoverageEntry>> {
      ["Dispatcher"] = [
        new TestCoverageEntry {
          TestFile = "DispatcherTests.cs",
          TestMethod = "TestAsync",
          TestClass = "DispatcherTests"
        }
      ]
    });

    using var debugManager = new DebugSessionManager();
    var handler = new StatusHandler(resolver, testCoverage, debugManager);

    // Act
    var result = handler.Handle();

    // Assert
    await Assert.That(result.MessageCount).IsEqualTo(2);
    await Assert.That(result.CommandCount).IsEqualTo(1);
    await Assert.That(result.EventCount).IsEqualTo(1);
    await Assert.That(result.TypeDocCount).IsEqualTo(1);
    await Assert.That(result.TestCount).IsEqualTo(1);
  }
}
