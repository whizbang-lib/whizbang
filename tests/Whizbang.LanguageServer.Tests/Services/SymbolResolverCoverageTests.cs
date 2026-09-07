using Whizbang.LanguageServer.Services;

namespace Whizbang.LanguageServer.Tests.Services;

/// <summary>
/// Coverage-round tests for <see cref="SymbolResolver"/> targeting the "message" registry-kind
/// branch in _determineKind -- a registered type flagged as neither a command nor an event.
/// The primary suite only exercises the command/event/type kinds.
/// </summary>
/// <tests>Whizbang.LanguageServer/Services/SymbolResolver.cs:183</tests>
public class SymbolResolverCoverageTests {

  // StatusHandler's status-bar counts and FlowDiagramHandler's node shapes both branch on
  // SymbolInfo.Kind. If a registry entry that is neither a command nor an event stopped
  // resolving to "message" (falling through to "type" or something else instead), a plain
  // message type would be miscounted in the VSCode status bar and drawn with the wrong node
  // shape in its flow diagram.
  [Test]
  public async Task Resolve_RegistryEntryNeitherCommandNorEvent_ResolvesToMessageKindAsync() {
    // Arrange
    var sut = new SymbolResolver("https://docs.whizbang.dev");
    sut.SetRegistryData([
      new MessageRegistryEntry {
        Type = "ArchiveOrderMessage",
        IsCommand = false,
        IsEvent = false,
        DispatcherCount = 1,
        ReceptorCount = 1
      }
    ]);

    // Act
    var result = sut.Resolve("ArchiveOrderMessage");

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Kind).IsEqualTo("message")
      .Because("a registry entry with neither IsCommand nor IsEvent set is a plain message and "
             + "must resolve to the 'message' kind, not fall through to 'type'");
  }
}
