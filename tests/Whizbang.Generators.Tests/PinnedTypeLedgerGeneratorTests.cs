using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PinnedTypeLedgerGenerator — the rename-platform ledger bootstrap/merge. Emits
/// <c>PinnedTypeLedger.g.cs</c> (JSON constant) which an MSBuild target extracts to
/// <c>.whizbang/pinned-type-ledger.json</c>. Verifies fresh bootstrap, monotonic merge, and that a rename is
/// never auto-healed (so WHIZ120 keeps firing until acknowledged).
/// </summary>
[Category("SourceGenerators")]
[Category("RenamePlatform")]
public class PinnedTypeLedgerGeneratorTests {
  private const string LEDGER_PATH = "/repo/src/TestAssembly/.whizbang/pinned-type-ledger.json";

  private const string TWO_PINNED_TYPES = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent;
      public record OrderModel;
      [PinnedId("22222222-2222-2222-2222-222222222222")]
      public class OrderProjection : IPerspectiveFor<OrderModel, OrderPlacedEvent> {
        public OrderModel Apply(OrderModel current, OrderPlacedEvent @event) => current;
      }
      """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_NestedType_RendersLiteralPlusNotUnicodeEscapeAsync() {
    // A nested pinned type's CLR name uses '+' as the separator. The committed ledger is a human-reviewed
    // lockfile, so the '+' must appear literally — NOT as the HTML-safe encoder's 6-char unicode escape.
    const string nested = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        public static class OrderContracts {
          [PinnedId("33333333-3333-3333-3333-333333333333")]
          public record OrderPlacedEvent : IEvent;
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(nested);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("TestApp.OrderContracts+OrderPlacedEvent"); // literal '+'
    await Assert.That(generated!).DoesNotContain("\\u002B");                            // not the escaped form
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_NoExistingLedger_BootstrapsAllPinnedTypesAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(TWO_PINNED_TYPES);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    // Both pinned ids and their current CLR names, with empty formerNames.
    await Assert.That(generated!).Contains("11111111-1111-1111-1111-111111111111");
    await Assert.That(generated!).Contains("TestApp.OrderPlacedEvent");
    await Assert.That(generated!).Contains("22222222-2222-2222-2222-222222222222");
    await Assert.That(generated!).Contains("TestApp.OrderProjection");
    await Assert.That(generated!).Contains("\"\"event\"\"");         // kind survives (JSON is @-escaped: "" == ")
    await Assert.That(generated!).Contains("\"\"perspective\"\"");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ExistingLedgerWithFormerName_PreservesHistoryAndAddsNewAsync() {
    // Committed ledger already tracks the event (with a former name) but NOT the projection.
    var existing = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-1111-1111-1111-111111111111",
          "clrTypeName": "TestApp.OrderPlacedEvent",
          "kind": "event",
          "formerNames": ["TestApp.OrderCreatedEvent"] }
      ] }
      """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      TWO_PINNED_TYPES, [(LEDGER_PATH, existing)]);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("TestApp.OrderCreatedEvent");   // former name preserved
    await Assert.That(generated!).Contains("22222222-2222-2222-2222-222222222222"); // new projection added
    await Assert.That(generated!).Contains("TestApp.OrderProjection");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RenamedType_DoesNotAutoHealClrNameAsync() {
    // The committed ledger records the OLD name for this pinned id; the compiled type now has a NEW name.
    // The merge must PRESERVE the old clrTypeName (so WHIZ120 keeps firing), not silently update it.
    const string renamed = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        [PinnedId("11111111-1111-1111-1111-111111111111")]
        public record OrderPlacedEvent : IEvent;
        """;
    var existing = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-1111-1111-1111-111111111111",
          "clrTypeName": "TestApp.OrderCreatedEvent",
          "kind": "event",
          "formerNames": [] }
      ] }
      """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(
      renamed, [(LEDGER_PATH, existing)]);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("TestApp.OrderCreatedEvent");        // old name preserved
    await Assert.That(generated!).DoesNotContain("TestApp.OrderPlacedEvent");   // NOT auto-updated to current name
  }
}
