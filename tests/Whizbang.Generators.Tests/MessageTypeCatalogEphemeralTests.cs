using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests that MessageTypeCatalogGenerator resolves a type's effective ephemeral mode at COMPILE TIME
/// (zero reflection) and stamps it onto the catalog entry. Resolution walks own type → base records →
/// implemented interfaces, most-specific-wins — so <c>[Ephemeral]</c> composed on a base or interface
/// propagates, and a receiver/registry can read the mode without the compiled attribute.
/// </summary>
public class MessageTypeCatalogEphemeralTests {
  private static async Task<string> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();
    return code!;
  }

  [Test]
  public async Task DirectAttribute_StampsResolvedModeAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
      public record UserIsTyping : IEvent;
""");

    await Assert.That(code).Contains("typeof(global::MyApp.UserIsTyping)");
    await Assert.That(code).Contains("EphemeralInfo(");
    await Assert.That(code).Contains("Destruction.WhenConsumed");
    await Assert.That(code).Contains("TransientStorage.InMemory");
  }

  [Test]
  public async Task ViaProfileInterface_ResolvesFromInterfaceAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.TtlRow)]
      public interface IPresenceSignal : IEvent { }
      public record UserWentIdle : IPresenceSignal;
""");

    await Assert.That(code).Contains("typeof(global::MyApp.UserWentIdle)");
    await Assert.That(code).Contains("TransientStorage.TtlRow");
  }

  [Test]
  public async Task ViaBaseRecord_ResolvesFromBaseAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.TtlRow)]
      public abstract record SessionState : IEvent;
      public record TabsReordered : SessionState;
""");

    await Assert.That(code).Contains("typeof(global::MyApp.TabsReordered)");
    await Assert.That(code).Contains("TransientStorage.TtlRow");
  }

  [Test]
  public async Task ViaShippedMarker_ResolvesToDefaultsAsync() {
    // IEphemeralEvent is the framework default profile — resolved through the same interface walk.
    var code = await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record CursorMoved : IEphemeralEvent;
""");

    await Assert.That(code).Contains("typeof(global::MyApp.CursorMoved)");
    await Assert.That(code).Contains("EphemeralInfo(");
    await Assert.That(code).Contains("Destruction.WhenConsumed");
    await Assert.That(code).Contains("TransientStorage.InMemory");
  }

  [Test]
  public async Task SourcedEvent_HasNoEphemeralInfoAsync() {
    var code = await _generateAsync("""
      using Whizbang.Core;
      namespace MyApp;
      public record OrderPlaced : IEvent;
""");

    await Assert.That(code).Contains("typeof(global::MyApp.OrderPlaced)");
    await Assert.That(code).DoesNotContain("EphemeralInfo");
  }

  [Test]
  public async Task MostSpecificWins_OwnAttributeOverridesInterfaceAsync() {
    // The type's own [Ephemeral] refines the profile it inherits — resolves to AfterTtl, not WhenConsumed.
    var code = await _generateAsync("""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.InMemory)]
      public interface IPresenceSignal : IEvent { }
      [Ephemeral(Destruction = Destruction.AfterTtl, Storage = TransientStorage.TtlRow)]
      public record PinnedPresence : IPresenceSignal;
""");

    await Assert.That(code).Contains("typeof(global::MyApp.PinnedPresence)");
    await Assert.That(code).Contains("Destruction.AfterTtl");
    await Assert.That(code).DoesNotContain("Destruction.WhenConsumed");
  }
}
