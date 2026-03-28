using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Handlers;

public sealed class FlowDiagramHandler {
  private readonly SymbolResolver _symbolResolver;
  private readonly MermaidGenerator _mermaidGenerator;

  public FlowDiagramHandler(SymbolResolver symbolResolver, MermaidGenerator mermaidGenerator) {
    _symbolResolver = symbolResolver;
    _mermaidGenerator = mermaidGenerator;
  }

  public FlowDiagramResult Handle(GenerateFlowDiagramParams request) {
    var symbolInfo = _symbolResolver.Resolve(request.MessageType);

    if (symbolInfo is null) {
      return new FlowDiagramResult {
        MermaidCode = """
            graph LR
                NotFound["Message not found"]
                style NotFound fill:#ef9a9a,stroke:#c62828,color:#000
            """
      };
    }

    // Build dispatcher/receptor/perspective lists from the symbol info.
    // The SymbolResolver provides counts but not the actual names, so we generate
    // placeholder entries based on counts. The real names would come from a richer
    // registry in a future iteration.
    var dispatchers = Enumerable.Range(0, symbolInfo.DispatcherCount)
        .Select(i => ($"Dispatcher{i + 1}", "Dispatch"))
        .ToList();

    var receptors = Enumerable.Range(0, symbolInfo.ReceptorCount)
        .Select(i => ($"Receptor{i + 1}", "HandleAsync"))
        .ToList();

    var perspectives = Enumerable.Range(0, symbolInfo.PerspectiveCount)
        .Select(i => $"Perspective{i + 1}")
        .ToList();

    var mermaid = _mermaidGenerator.Generate(
        symbolInfo.Name,
        symbolInfo.IsCommand,
        symbolInfo.IsEvent,
        dispatchers,
        receptors,
        perspectives);

    return new FlowDiagramResult { MermaidCode = mermaid };
  }
}
