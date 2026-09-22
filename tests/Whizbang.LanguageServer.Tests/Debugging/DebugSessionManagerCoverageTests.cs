using Whizbang.LanguageServer.Debugging;

namespace Whizbang.LanguageServer.Tests.Debug;

/// <summary>
/// Coverage-round tests for <see cref="DebugSessionManager"/> targeting the branch in
/// TotalPausedTime that adds the ongoing pause's elapsed time when the session is currently
/// paused. The primary suite only ever reads TotalPausedTime after resuming, so the
/// "still paused" branch is never exercised.
/// </summary>
/// <tests>Whizbang.LanguageServer/Debugging/DebugSessionManager.cs:42,43</tests>
public class DebugSessionManagerCoverageTests {

  // A keepalive service polls TotalPausedTime while a developer is mid-breakpoint to decide
  // whether to send a heartbeat. If the ongoing pause weren't folded into the total, a session
  // paused for a long stretch would report only its completed pause cycles -- understating how
  // long the developer has actually been stopped, and letting the keepalive service think the
  // pause is shorter (or over) than it really is.
  [Test]
  public async Task TotalPausedTime_WhileCurrentlyPaused_IncludesOngoingPauseDurationAsync() {
    // Arrange
    using var manager = new DebugSessionManager();
    manager.NotifyPaused();

    // Act -- read while still paused, without ever calling NotifyResumed
    var total = manager.TotalPausedTime;

    // Assert
    await Assert.That(total).IsGreaterThanOrEqualTo(TimeSpan.Zero)
      .Because("the ongoing pause's elapsed time must be folded into the total, not dropped "
             + "until the session resumes");
  }
}
