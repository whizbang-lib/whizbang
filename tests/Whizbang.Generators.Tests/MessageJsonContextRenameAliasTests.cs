using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// P1 of the rename-management platform: the MessageJsonContextGenerator reads the committed
/// <c>.whizbang/pinned-type-ledger.json</c> (via AdditionalFiles) and emits an extra
/// <c>JsonContextRegistry.RegisterTypeName(formerName, typeof(currentType), …)</c> for every FORMER
/// name a pinned type has had, so events written into the append-only log under a prior CLR name still
/// deserialize to the current type after a rename.
/// </summary>
[Category("SourceGenerators")]
[Category("RenamePlatform")]
public class MessageJsonContextRenameAliasTests {
  private const string EVENT_SOURCE = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;
      [PinnedId("11111111-2222-3333-4444-555555555555")]
      public record OrderPlacedEvent : IEvent;
      """;

  private const string LEDGER_PATH = "/repo/src/TestAssembly/.whizbang/pinned-type-ledger.json";

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_LedgerRecordsFormerName_EmitsAliasRegistrationAsync() {
    // Ledger says the current type TestApp.OrderPlacedEvent was formerly TestApp.OrderCreatedEvent.
    var ledger = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-2222-3333-4444-555555555555",
          "clrTypeName": "TestApp.OrderPlacedEvent",
          "kind": "event",
          "formerNames": ["TestApp.OrderCreatedEvent"] }
      ] }
      """;

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(
      EVENT_SOURCE, [(LEDGER_PATH, ledger)]);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");

    await Assert.That(generated).IsNotNull();
    // The former assembly-qualified name must resolve to the CURRENT type.
    await Assert.That(generated!).Contains("\"TestApp.OrderCreatedEvent, TestAssembly\"");
    await Assert.That(generated!).Contains("typeof(global::TestApp.OrderPlacedEvent)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_NoLedger_EmitsNoAliasAsync() {
    // Without a ledger the generator emits only the current-name registration — no former-name alias.
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(EVENT_SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).DoesNotContain("OrderCreatedEvent");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_LedgerFormerName_GeneratedAliasCompilesAsync() {
    // The emitted alias registration (RegisterTypeName + typeof + MessageEnvelope<T>) must be valid C#.
    // The single-generator harness produces a facade referencing WhizbangIdJsonContext (a SIBLING generator's
    // output), so a baseline error set exists independent of the ledger. Assert the alias adds no NEW error.
    var ledger = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-2222-3333-4444-555555555555",
          "clrTypeName": "TestApp.OrderPlacedEvent",
          "kind": "event",
          "formerNames": ["TestApp.OrderCreatedEvent"] }
      ] }
      """;

    var baseline = GeneratorTestHelper.GetGeneratedCompilationErrors<MessageJsonContextGenerator>(EVENT_SOURCE)
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).OrderBy(m => m).ToArray();
    var withLedger = GeneratorTestHelper.GetGeneratedCompilationErrors<MessageJsonContextGenerator>(
      EVENT_SOURCE, [(LEDGER_PATH, ledger)])
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).OrderBy(m => m).ToArray();

    // No NEW compile error, and nothing referencing the alias'd former name — the generated alias is valid C#.
    await Assert.That(withLedger).IsEquivalentTo(baseline);
    await Assert.That(withLedger.Any(m => m.Contains("OrderCreatedEvent"))).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_FormerNameShadowsLivingType_SkipsAliasAsync() {
    // Pathological name-reuse: the ledger's former name equals a DIFFERENT living type's current name.
    // The alias must NOT be emitted, or it would shadow the live type's own registration.
    const string twoTypes = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        [PinnedId("11111111-2222-3333-4444-555555555555")]
        public record OrderPlacedEvent : IEvent;
        [PinnedId("22222222-3333-4444-5555-666666666666")]
        public record OrderCreatedEvent : IEvent;
        """;
    var ledger = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-2222-3333-4444-555555555555",
          "clrTypeName": "TestApp.OrderPlacedEvent",
          "kind": "event",
          "formerNames": ["TestApp.OrderCreatedEvent"] }
      ] }
      """;

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(
      twoTypes, [(LEDGER_PATH, ledger)]);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");

    await Assert.That(generated).IsNotNull();
    // The living OrderCreatedEvent must map to ITSELF, never be redirected to OrderPlacedEvent by the alias.
    await Assert.That(generated!).DoesNotContain(
      "\"TestApp.OrderCreatedEvent, TestAssembly\",\n    typeof(global::TestApp.OrderPlacedEvent)");
  }
}
