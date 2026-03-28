using Whizbang.LanguageServer.Handlers;
using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Handlers;

public class SymbolHandlerTests {
  [Test]
  public async Task HandleGetSymbol_KnownSymbol_ReturnsInfoAsync() {
    // Arrange
    var resolver = new SymbolResolver("https://docs.whizbang.dev");
    resolver.SetRegistryData([
      new MessageRegistryEntry {
        Type = "Whizbang.Core.CreateOrderCommand",
        IsCommand = true,
        IsEvent = false,
        DispatcherCount = 2,
        ReceptorCount = 1,
        PerspectiveCount = 0,
        TestCount = 5
      }
    ]);

    var handler = new SymbolHandler(resolver);
    var request = new Protocol.GetSymbolInfoParams { Symbol = "CreateOrderCommand" };

    // Act
    var result = handler.Handle(request);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Whizbang.Core.CreateOrderCommand");
    await Assert.That(result.Kind).IsEqualTo("command");
    await Assert.That(result.IsCommand).IsTrue();
    await Assert.That(result.DispatcherCount).IsEqualTo(2);
  }

  [Test]
  public async Task HandleGetSymbol_UnknownSymbol_ReturnsNullAsync() {
    // Arrange
    var resolver = new SymbolResolver("https://docs.whizbang.dev");
    var handler = new SymbolHandler(resolver);
    var request = new Protocol.GetSymbolInfoParams { Symbol = "NonExistentSymbol" };

    // Act
    var result = handler.Handle(request);

    // Assert
    await Assert.That(result).IsNull();
  }
}
