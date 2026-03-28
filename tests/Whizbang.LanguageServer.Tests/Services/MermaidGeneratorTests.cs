using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Services;

public class MermaidGeneratorTests {
  private readonly MermaidGenerator _sut = new();

  [Test]
  public async Task Generate_CommandWithDispatchersAndReceptors_ProducesValidMermaidAsync() {
    // Arrange
    var dispatchers = new List<(string, string)> {
            ("OrderController", "CreateOrder"),
            ("AdminController", "ForceCreate")
        };
    var receptors = new List<(string, string)> {
            ("CreateOrderReceptor", "HandleAsync")
        };
    var perspectives = new List<string>();

    // Act
    var result = _sut.Generate("CreateOrderCommand", isCommand: true, isEvent: false,
        dispatchers, receptors, perspectives);

    // Assert
    await Assert.That(result).Contains("graph LR");
    await Assert.That(result).Contains("CreateOrderCommand");
    await Assert.That(result).Contains("subgraph Dispatchers");
    await Assert.That(result).Contains("OrderController.CreateOrder");
    await Assert.That(result).Contains("subgraph Receptors");
    await Assert.That(result).Contains("CreateOrderReceptor.HandleAsync");
    await Assert.That(result).DoesNotContain("subgraph Perspectives");
  }

  [Test]
  public async Task Generate_EventWithPerspectives_IncludesPerspectiveSubgraphAsync() {
    // Arrange
    var dispatchers = new List<(string, string)> { ("OrderService", "Publish") };
    var receptors = new List<(string, string)>();
    var perspectives = new List<string> {
            "OrderSummaryPerspective",
            "InventoryLevelsPerspective"
        };

    // Act
    var result = _sut.Generate("OrderCreatedEvent", isCommand: false, isEvent: true,
        dispatchers, receptors, perspectives);

    // Assert
    await Assert.That(result).Contains("subgraph Perspectives");
    await Assert.That(result).Contains("OrderSummaryPerspective");
    await Assert.That(result).Contains("InventoryLevelsPerspective");
    // Event uses round shape (())
    await Assert.That(result).Contains("((OrderCreatedEvent))");
  }

  [Test]
  public async Task Generate_CommandShape_UsesParallelogramAsync() {
    // Arrange & Act
    var result = _sut.Generate("DoSomething", isCommand: true, isEvent: false,
        new List<(string, string)>(), new List<(string, string)>(), new List<string>());

    // Assert — command uses /text\ shape
    await Assert.That(result).Contains("[/DoSomething\\]");
  }

  [Test]
  public async Task Generate_EventShape_UsesCircleAsync() {
    // Arrange & Act
    var result = _sut.Generate("SomethingHappened", isCommand: false, isEvent: true,
        new List<(string, string)>(), new List<(string, string)>(), new List<string>());

    // Assert — event uses ((text)) shape
    await Assert.That(result).Contains("((SomethingHappened))");
  }

  [Test]
  public async Task Generate_NoDispatchersOrReceptors_ProducesMinimalDiagramAsync() {
    // Arrange & Act
    var result = _sut.Generate("OrphanMessage", isCommand: false, isEvent: false,
        new List<(string, string)>(), new List<(string, string)>(), new List<string>());

    // Assert
    await Assert.That(result).Contains("graph LR");
    await Assert.That(result).Contains("OrphanMessage");
    await Assert.That(result).DoesNotContain("subgraph Dispatchers");
    await Assert.That(result).DoesNotContain("subgraph Receptors");
    await Assert.That(result).DoesNotContain("subgraph Perspectives");
  }

  [Test]
  public async Task Generate_EmptyMethodName_ShowsClassNameOnlyAsync() {
    // Arrange
    var dispatchers = new List<(string, string)> { ("SomeClass", "") };

    // Act
    var result = _sut.Generate("Msg", isCommand: true, isEvent: false,
        dispatchers, new List<(string, string)>(), new List<string>());

    // Assert — should show just the class, not "SomeClass."
    await Assert.That(result).Contains("SomeClass");
    await Assert.That(result).DoesNotContain("SomeClass.");
  }

  [Test]
  public async Task Generate_SpecialCharactersInName_SanitizesIdAsync() {
    // Arrange & Act
    var result = _sut.Generate("Whizbang.Core.Events<T>", isCommand: false, isEvent: true,
        new List<(string, string)>(), new List<(string, string)>(), new List<string>());

    // Assert — ID should not contain . < > characters
    await Assert.That(result).Contains("Whizbang_Core_Events_T_");
  }

  [Test]
  public async Task Generate_IncludesStylingForAllNodesAsync() {
    // Arrange
    var dispatchers = new List<(string, string)> { ("A", "B") };
    var receptors = new List<(string, string)> { ("C", "D") };
    var perspectives = new List<string> { "E" };

    // Act
    var result = _sut.Generate("Msg", isCommand: true, isEvent: false,
        dispatchers, receptors, perspectives);

    // Assert — styling for message (blue), dispatchers (green), receptors (orange), perspectives (purple)
    await Assert.That(result).Contains("style Msg fill:#4fc3f7");
    await Assert.That(result).Contains("style D0 fill:#a5d6a7");
    await Assert.That(result).Contains("style R0 fill:#ffcc80");
    await Assert.That(result).Contains("style P0 fill:#ce93d8");
  }

  [Test]
  public async Task Generate_MultipleDispatchers_CreatesEdgesForEachAsync() {
    // Arrange
    var dispatchers = new List<(string, string)> {
            ("A", "X"), ("B", "Y"), ("C", "Z")
        };

    // Act
    var result = _sut.Generate("Msg", isCommand: true, isEvent: false,
        dispatchers, new List<(string, string)>(), new List<string>());

    // Assert
    await Assert.That(result).Contains("D0 --> Msg");
    await Assert.That(result).Contains("D1 --> Msg");
    await Assert.That(result).Contains("D2 --> Msg");
  }
}
