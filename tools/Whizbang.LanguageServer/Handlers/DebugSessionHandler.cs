using Whizbang.LanguageServer.Debug;

namespace Whizbang.LanguageServer.Handlers;

/// <summary>
/// Handles debug session notifications from the IDE, delegating to <see cref="DebugSessionManager"/>.
/// </summary>
public sealed class DebugSessionHandler {
  private readonly DebugSessionManager _manager;

  public DebugSessionHandler(DebugSessionManager manager) {
    _manager = manager;
  }

  /// <summary>Handles a debugger pause notification.</summary>
  public void HandlePaused() => _manager.NotifyPaused();

  /// <summary>Handles a debugger resume notification.</summary>
  public void HandleResumed() => _manager.NotifyResumed();
}
