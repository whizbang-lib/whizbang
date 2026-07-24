using Whizbang.LanguageServer.Debugging;

namespace Whizbang.LanguageServer.Handlers;

/// <summary>
/// Handles debug session notifications from the IDE, delegating to <see cref="DebugSessionManager"/>.
/// </summary>
public sealed class DebugSessionHandler(DebugSessionManager manager) {
  private readonly DebugSessionManager _manager = manager;

  /// <summary>Handles a debugger pause notification.</summary>
  public void HandlePaused() => _manager.NotifyPaused();

  /// <summary>Handles a debugger resume notification.</summary>
  public void HandleResumed() => _manager.NotifyResumed();
}
