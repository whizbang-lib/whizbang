using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Handlers;

/// <summary>
/// Coverage-round tests for <see cref="FlowDiagramHandler"/> targeting the perspective-name
/// generation branch, whose lambda body only runs when a resolved symbol actually has
/// perspectives. The primary suite always resolves messages with PerspectiveCount = 0.
/// </summary>
/// <tests>Whizbang.LanguageServer/Handlers/FlowDiagramHandler.cs:36</tests>
public class FlowDiagramHandlerCoverageTests {

  // The flow diagram is how a developer sees which perspectives observe an event. If the
  // placeholder-name generation for perspectives stopped running, an event with real
  // perspectives wired up would render with an empty (or missing) Perspectives subgraph --
  // hiding read-model wiring that actually exists from whoever is diagnosing the dispatch flow.
  [Test]
  public async Task Handle_MessageWithPerspectives_IncludesPerspectiveNodesInDiagramAsync() {
    // Arrange
    var resolver = new SymbolResolver("https://docs.whizbang.dev");
    resolver.SetRegistryData([
      new MessageRegistryEntry {
        Type = "OrderCreatedEvent",
        IsCommand = false,
        IsEvent = true,
        DispatcherCount = 0,
        ReceptorCount = 0,
        PerspectiveCount = 2
      }
    ]);
    var generator = new MermaidGenerator();
    var handler = new FlowDiagramHandler(resolver, generator);
    var request = new GenerateFlowDiagramParams { MessageType = "OrderCreatedEvent" };

    // Act
    var result = handler.Handle(request);

    // Assert
    await Assert.That(result.MermaidCode).Contains("Perspective1")
      .Because("each perspective slot must get a generated placeholder node in the diagram");
    await Assert.That(result.MermaidCode).Contains("Perspective2")
      .Because("all perspective slots must be represented, not just the first");
    await Assert.That(result.MermaidCode).Contains("subgraph Perspectives");
  }
}
