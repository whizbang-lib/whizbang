using Whizbang.LanguageServer.Debugging;
using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Handlers;

/// <summary>
/// Coverage-focused tests for <see cref="StatusHandler"/> targeting the "message" registry-kind
/// branch (a registered type that is neither flagged as a command nor an event) that the
/// primary test suite does not reach.
/// </summary>
public class StatusHandlerCoverageTests {
  // If the "message" branch stopped incrementing MessageCount, a plain message type wired into
  // the registry (neither IsCommand nor IsEvent) would vanish from the VSCode extension's
  // status-bar counts even though it is a real, dispatched message.
  [Test]
  public async Task Handle_MessageWithoutCommandOrEventFlag_CountsTowardMessageCountOnlyAsync() {
    // Arrange
    var resolver = new SymbolResolver("https://docs.whizbang.dev");
    resolver.SetRegistryData([
      new MessageRegistryEntry {
        Type = "ArchiveOrderMessage",
        IsCommand = false,
        IsEvent = false,
        DispatcherCount = 1,
        ReceptorCount = 1,
        PerspectiveCount = 0,
        TestCount = 1
      }
    ]);

    var testCoverage = new TestCoverageService();
    using var debugManager = new DebugSessionManager();
    var handler = new StatusHandler(resolver, testCoverage, debugManager);

    // Act
    var result = handler.Handle();

    // Assert
    await Assert.That(result.MessageCount).IsEqualTo(1);
    await Assert.That(result.CommandCount).IsEqualTo(0);
    await Assert.That(result.EventCount).IsEqualTo(0);
  }
}
