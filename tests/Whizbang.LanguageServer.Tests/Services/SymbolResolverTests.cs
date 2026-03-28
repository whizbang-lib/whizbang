using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Services;

public class SymbolResolverTests {
  private readonly SymbolResolver _sut = new("https://whizbang-lib.github.io/docs/v1.0.0");

  [Test]
  public async Task Resolve_MessageInRegistry_ReturnsFullInfoAsync() {
    // Arrange
    _sut.SetRegistryData([
        new MessageRegistryEntry {
                Type = "Whizbang.Sample.CreateOrderCommand",
                IsCommand = true,
                IsEvent = false,
                FilePath = "src/Commands/CreateOrderCommand.cs",
                LineNumber = 12,
                DispatcherCount = 2,
                ReceptorCount = 1,
                PerspectiveCount = 0,
                TestCount = 5
            }
    ]);

    // Act
    var result = _sut.Resolve("Whizbang.Sample.CreateOrderCommand");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Whizbang.Sample.CreateOrderCommand");
    await Assert.That(result.IsCommand).IsTrue();
    await Assert.That(result.IsEvent).IsFalse();
    await Assert.That(result.DispatcherCount).IsEqualTo(2);
    await Assert.That(result.ReceptorCount).IsEqualTo(1);
    await Assert.That(result.PerspectiveCount).IsEqualTo(0);
    await Assert.That(result.TestCount).IsEqualTo(5);
    await Assert.That(result.SourceFile).IsEqualTo("src/Commands/CreateOrderCommand.cs");
    await Assert.That(result.SourceLine).IsEqualTo(12);
    await Assert.That(result.Kind).IsEqualTo("command");
  }

  [Test]
  public async Task Resolve_TypeInVscodeFeed_ReturnsDocsInfoAsync() {
    // Arrange
    _sut.SetVscodeFeedData(new Dictionary<string, VscodeFeedEntry> {
      ["IDispatcher"] = new VscodeFeedEntry {
        Docs = "core-concepts/dispatcher",
        Title = "IDispatcher Interface",
        File = "src/Whizbang.Core/IDispatcher.cs",
        Line = 8,
        Tests = ["DispatcherTests.cs"]
      }
    });

    // Act
    var result = _sut.Resolve("IDispatcher");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("IDispatcher");
    await Assert.That(result.DocsUrl).IsEqualTo("https://whizbang-lib.github.io/docs/v1.0.0/core-concepts/dispatcher");
    await Assert.That(result.DocsTitle).IsEqualTo("IDispatcher Interface");
    await Assert.That(result.SourceFile).IsEqualTo("src/Whizbang.Core/IDispatcher.cs");
    await Assert.That(result.SourceLine).IsEqualTo(8);
    await Assert.That(result.TestCount).IsEqualTo(1);
    await Assert.That(result.Kind).IsEqualTo("type");
  }

  [Test]
  public async Task Resolve_TypeInCodeDocsMap_ReturnsDocsAsync() {
    // Arrange
    _sut.SetCodeDocsMapData(new Dictionary<string, CodeDocsEntry> {
      ["MessageEnvelope"] = new CodeDocsEntry {
        File = "src/Whizbang.Core/MessageEnvelope.cs",
        Line = 15,
        Symbol = "MessageEnvelope",
        Docs = "core-concepts/message-envelope"
      }
    });

    // Act
    var result = _sut.Resolve("MessageEnvelope");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("MessageEnvelope");
    await Assert.That(result.DocsUrl).IsEqualTo("https://whizbang-lib.github.io/docs/v1.0.0/core-concepts/message-envelope");
    await Assert.That(result.SourceFile).IsEqualTo("src/Whizbang.Core/MessageEnvelope.cs");
    await Assert.That(result.SourceLine).IsEqualTo(15);
    await Assert.That(result.Kind).IsEqualTo("type");
  }

  [Test]
  public async Task Resolve_CombinesRegistryAndFeedAsync() {
    // Arrange
    _sut.SetRegistryData([
        new MessageRegistryEntry {
                Type = "OrderCreatedEvent",
                IsCommand = false,
                IsEvent = true,
                DispatcherCount = 1,
                ReceptorCount = 0,
                PerspectiveCount = 3,
                TestCount = 7
            }
    ]);
    _sut.SetVscodeFeedData(new Dictionary<string, VscodeFeedEntry> {
      ["OrderCreatedEvent"] = new VscodeFeedEntry {
        Docs = "events/order-created",
        Title = "OrderCreatedEvent",
        File = "src/Events/OrderCreatedEvent.cs",
        Line = 5,
        Tests = ["OrderCreatedEventTests.cs", "OrderFlowTests.cs"]
      }
    });

    // Act
    var result = _sut.Resolve("OrderCreatedEvent");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.IsEvent).IsTrue();
    await Assert.That(result.IsCommand).IsFalse();
    await Assert.That(result.DispatcherCount).IsEqualTo(1);
    await Assert.That(result.PerspectiveCount).IsEqualTo(3);
    await Assert.That(result.DocsUrl).IsEqualTo("https://whizbang-lib.github.io/docs/v1.0.0/events/order-created");
    await Assert.That(result.DocsTitle).IsEqualTo("OrderCreatedEvent");
    await Assert.That(result.SourceFile).IsEqualTo("src/Events/OrderCreatedEvent.cs");
    await Assert.That(result.SourceLine).IsEqualTo(5);
    await Assert.That(result.Kind).IsEqualTo("event");
  }

  [Test]
  public async Task Resolve_UnknownSymbol_ReturnsNullAsync() {
    // Arrange — no data sources set

    // Act
    var result = _sut.Resolve("NonExistentType");

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Resolve_PartialMatch_FindsEndsWithAsync() {
    // Arrange
    _sut.SetRegistryData([
        new MessageRegistryEntry {
                Type = "Whizbang.Core.CreateOrderCommand",
                IsCommand = true,
                IsEvent = false,
                DispatcherCount = 1,
                ReceptorCount = 1,
                PerspectiveCount = 0,
                TestCount = 2
            }
    ]);

    // Act — partial name, should match via EndsWith
    var result = _sut.Resolve("CreateOrderCommand");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Whizbang.Core.CreateOrderCommand");
    await Assert.That(result.IsCommand).IsTrue();
    await Assert.That(result.DispatcherCount).IsEqualTo(1);
  }

  [Test]
  public async Task GetAllSymbols_ReturnsUnionOfAllSourcesAsync() {
    // Arrange
    _sut.SetRegistryData([
        new MessageRegistryEntry {
                Type = "CommandA",
                IsCommand = true,
                IsEvent = false
            },
            new MessageRegistryEntry {
                Type = "EventB",
                IsCommand = false,
                IsEvent = true
            }
    ]);
    _sut.SetVscodeFeedData(new Dictionary<string, VscodeFeedEntry> {
      ["TypeC"] = new VscodeFeedEntry {
        Docs = "types/c",
        Title = "TypeC"
      },
      ["CommandA"] = new VscodeFeedEntry {
        Docs = "commands/a",
        Title = "CommandA"
      }
    });
    _sut.SetCodeDocsMapData(new Dictionary<string, CodeDocsEntry> {
      ["TypeD"] = new CodeDocsEntry {
        File = "src/TypeD.cs",
        Line = 1,
        Symbol = "TypeD",
        Docs = "types/d"
      },
      ["EventB"] = new CodeDocsEntry {
        File = "src/EventB.cs",
        Line = 1,
        Symbol = "EventB",
        Docs = "events/b"
      }
    });

    // Act
    var result = _sut.GetAllSymbols();

    // Assert — should be deduplicated union: CommandA, EventB, TypeC, TypeD
    await Assert.That(result.Count).IsEqualTo(4);
    await Assert.That(result).Contains("CommandA");
    await Assert.That(result).Contains("EventB");
    await Assert.That(result).Contains("TypeC");
    await Assert.That(result).Contains("TypeD");
  }
}
