using Whizbang.LanguageServer.Debugging;
using Whizbang.LanguageServer.Handlers;

namespace Whizbang.LanguageServer.Tests.Handlers;

public class DebugSessionHandlerTests {
  [Test]
  public async Task HandlePaused_CallsManagerNotifyPausedAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    var handler = new DebugSessionHandler(manager);

    // Act
    handler.HandlePaused();

    // Assert
    await Assert.That(manager.IsPaused).IsTrue();
  }

  [Test]
  public async Task HandleResumed_CallsManagerNotifyResumedAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    var handler = new DebugSessionHandler(manager);
    manager.NotifyPaused();

    // Act
    handler.HandleResumed();

    // Assert
    await Assert.That(manager.IsPaused).IsFalse();
  }
}
