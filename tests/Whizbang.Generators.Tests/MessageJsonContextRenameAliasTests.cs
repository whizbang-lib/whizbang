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
    const string ledger = """
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

  /// <summary>
  /// The emitted alias registration (RegisterTypeName + typeof + MessageEnvelope&lt;T&gt;) is valid C#.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This compiles the real generator set rather than one generator. The context generator emits a
  /// facade that references <c>WhizbangIdJsonContext</c>, which <c>WhizbangIdGenerator</c> emits, so
  /// running the context generator alone produces code that cannot compile for a reason that has
  /// nothing to do with the ledger. Run both and the generated output compiles clean, which lets this
  /// assert the thing it means: zero errors.
  /// </para>
  /// <para>
  /// It used to run the generator twice, once without the ledger, and compare the two error sets, so
  /// that the standing error was subtracted out. That made the test's expected value the output of the
  /// very machinery under test, which is the weakness that turned a single odd run into an
  /// undiagnosable flake: one half reported zero errors and the other three, and nothing in the
  /// failure said which compile had misbehaved or why. An oracle has to be independent of the subject.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_LedgerFormerName_GeneratedAliasCompilesAsync() {
    const string ledger = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-2222-3333-4444-555555555555",
          "clrTypeName": "TestApp.OrderPlacedEvent",
          "kind": "event",
          "formerNames": ["TestApp.OrderCreatedEvent"] }
      ] }
      """;

    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors(
      [new MessageJsonContextGenerator(), new WhizbangIdGenerator()],
      EVENT_SOURCE, [(LEDGER_PATH, ledger)]);

    var messages = errors
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .OrderBy(m => m, StringComparer.Ordinal)
      .ToArray();
    await Assert.That(messages).IsEmpty()
      .Because("the generated alias registration has to be valid C#, and with the sibling generator present "
        + "there is no standing error to subtract out, so the assertion is simply that the generated code "
        + $"compiles: [{string.Join("; ", messages)}]");
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
    const string ledger = """
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
