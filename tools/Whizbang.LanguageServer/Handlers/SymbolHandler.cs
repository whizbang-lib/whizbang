using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Handlers;

public sealed class SymbolHandler(SymbolResolver symbolResolver) {
  private readonly SymbolResolver _symbolResolver = symbolResolver;

  public SymbolInfo? Handle(GetSymbolInfoParams request) {
    return _symbolResolver.Resolve(request.Symbol);
  }
}
