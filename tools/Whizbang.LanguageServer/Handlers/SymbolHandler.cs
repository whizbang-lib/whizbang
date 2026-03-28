using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Handlers;

public sealed class SymbolHandler {
  private readonly SymbolResolver _symbolResolver;

  public SymbolHandler(SymbolResolver symbolResolver) {
    _symbolResolver = symbolResolver;
  }

  public SymbolInfo? Handle(GetSymbolInfoParams request) {
    return _symbolResolver.Resolve(request.Symbol);
  }
}
