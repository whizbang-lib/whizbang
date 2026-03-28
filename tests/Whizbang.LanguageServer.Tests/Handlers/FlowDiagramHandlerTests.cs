using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Protocol;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Handlers;

public class FlowDiagramHandlerTests {
  [Test]
  public async Task HandleGenerateFlowDiagram_KnownMessage_ReturnsMermaidAsync() {
    // Arrange
    var resolver = new SymbolResolver("https://docs.whizbang.dev");
    resolver.SetRegistryData([
      new MessageRegistryEntry {
        Type = "CreateOrderCommand",
        IsCommand = true,
        IsEvent = false,
        DispatcherCount = 1,
        ReceptorCount = 1,
        PerspectiveCount = 0,
        TestCount = 3
      }
    ]);

    var generator = new MermaidGenerator();
    var handler = new FlowDiagramHandler(resolver, generator);
    var request = new GenerateFlowDiagramParams { MessageType = "CreateOrderCommand" };

    // Act
    var result = handler.Handle(request);

    // Assert
    await Assert.That(result.MermaidCode).Contains("graph LR");
    await Assert.That(result.MermaidCode).Contains("CreateOrderCommand");
  }

  [Test]
  public async Task HandleGenerateFlowDiagram_UnknownMessage_ReturnsEmptyMermaidAsync() {
    // Arrange
    var resolver = new SymbolResolver("https://docs.whizbang.dev");
    var generator = new MermaidGenerator();
    var handler = new FlowDiagramHandler(resolver, generator);
    var request = new GenerateFlowDiagramParams { MessageType = "NonExistentMessage" };

    // Act
    var result = handler.Handle(request);

    // Assert
    await Assert.That(result.MermaidCode).Contains("graph LR");
    await Assert.That(result.MermaidCode).Contains("Message not found");
  }
}
