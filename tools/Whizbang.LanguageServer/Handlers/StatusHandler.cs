using Whizbang.LanguageServer.Debugging;
using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Handlers;

public sealed class StatusHandler {
  private readonly SymbolResolver _symbolResolver;
  private readonly TestCoverageService _testCoverageService;
  private readonly DebugSessionManager _debugSessionManager;

  public StatusHandler(SymbolResolver symbolResolver, TestCoverageService testCoverageService, DebugSessionManager debugSessionManager) {
    _symbolResolver = symbolResolver;
    _testCoverageService = testCoverageService;
    _debugSessionManager = debugSessionManager;
  }

  public StatusInfo Handle() {
    var allSymbols = _symbolResolver.GetAllSymbols();
    var commandCount = 0;
    var eventCount = 0;
    var messageCount = 0;
    var typeDocCount = 0;

    foreach (var symbol in allSymbols) {
      var info = _symbolResolver.Resolve(symbol);
      if (info is null) {
        continue;
      }

      switch (info.Kind) {
        case "command":
          commandCount++;
          messageCount++;
          break;
        case "event":
          eventCount++;
          messageCount++;
          break;
        case "message":
          messageCount++;
          break;
        case "type":
          typeDocCount++;
          break;
      }
    }

    return new StatusInfo {
      MessageCount = messageCount,
      CommandCount = commandCount,
      EventCount = eventCount,
      TypeDocCount = typeDocCount,
      TestCount = _testCoverageService.GetTestCount(),
      IsDebugPaused = _debugSessionManager.IsPaused
    };
  }
}
