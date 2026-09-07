using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for PinnedTypeLedgerGenerator targeting discovery-side branches not
/// exercised by PinnedTypeLedgerGeneratorTests.cs: abstract pinned types, the command/message
/// kind classifications, a [PinnedId] type that implements no recognized kind, a whitespace-only
/// pinned id, and a compilation with no pinned types at all.
/// </summary>
[Category("SourceGenerators")]
[Category("RenamePlatform")]
public class PinnedTypeLedgerGeneratorCoverageTests {
  // ==================== Abstract pinned types are excluded ====================

  /// <summary>
  /// An abstract type can never be the concrete runtime type recorded on a compiled instance, so
  /// pinning one would add a ledger entry that no compiled type can ever match — dead weight that
  /// still has to be reviewed on every ledger diff, and a WHIZ121-orphan candidate forever.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AbstractPinnedType_ExcludedFromLedgerAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        [PinnedId("77777777-7777-7777-7777-777777777777")]
        public abstract class AbstractOrderEvent : IEvent { }

        [PinnedId("88888888-8888-8888-8888-888888888888")]
        public class ConcreteOrderEvent : IEvent { }
        """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("88888888-8888-8888-8888-888888888888");
    await Assert.That(generated!).DoesNotContain("77777777-7777-7777-7777-777777777777")
      .Because("an abstract type has no concrete runtime instance to ever match a ledger entry");
    await Assert.That(generated!).DoesNotContain("AbstractOrderEvent");
  }

  // ==================== Kind classification ====================

  /// <summary>
  /// Kind drives how the VSCode extension and analyzers present a pinned type's history. A
  /// command wrongly filed as some other kind (or omitted) misdirects every tool that groups or
  /// filters pinned types by kind.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_PinnedCommand_RecordsCommandKindAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        [PinnedId("99999999-9999-9999-9999-999999999999")]
        public class ArchiveOrderCommand : ICommand { }
        """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("99999999-9999-9999-9999-999999999999");
    await Assert.That(generated!).Contains("\"\"command\"\"")
      .Because("a pinned ICommand type must be recorded with kind \"command\" (JSON is @-escaped: \"\" == \")");
  }

  /// <summary>
  /// A [PinnedId] type that implements plain IMessage (not ICommand/IEvent/IPerspectiveFor) —
  /// e.g. a raw custom message kind — must still be classified and recorded, not silently
  /// dropped for falling outside the three more specific kinds.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_PinnedPlainMessage_RecordsMessageKindAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        [PinnedId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
        public class RawNotice : IMessage { }
        """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    await Assert.That(generated!).Contains("\"\"message\"\"");
  }

  /// <summary>
  /// [PinnedId] alone isn't enough to be governed — the ledger only tracks the specific
  /// message/perspective kinds it knows how to classify. A stray pinned POCO that implements
  /// none of them must be silently skipped, never absorbed with a null/blank kind that downstream
  /// tooling can't interpret.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_PinnedTypeWithNoRecognizedKind_SkipsEntryAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        [PinnedId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")]
        public class UntrackedPojo { }

        [PinnedId("cccccccc-cccc-cccc-cccc-cccccccccccc")]
        public class RealEvent : IEvent { }
        """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("cccccccc-cccc-cccc-cccc-cccccccccccc");
    await Assert.That(generated!).DoesNotContain("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
      .Because("a type with no recognized message/perspective kind must not be added to the ledger");
    await Assert.That(generated!).DoesNotContain("UntrackedPojo");
  }

  // ==================== Pinned-id value validation ====================

  /// <summary>
  /// PinnedIdAttribute's own constructor validates a blank id, but that validation runs only when
  /// the attribute is INSTANTIATED at runtime — the generator reads the id straight off the
  /// compile-time constant and never constructs the attribute, so a whitespace-only literal must
  /// be rejected here too, or a meaningless id would be committed to the reviewed ledger file.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_PinnedIdWithWhitespaceValue_SkipsEntryAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        [PinnedId("   ")]
        public class BlankIdEvent : IEvent { }

        [PinnedId("dddddddd-dddd-dddd-dddd-dddddddddddd")]
        public class RealEvent : IEvent { }
        """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("dddddddd-dddd-dddd-dddd-dddddddddddd");
    await Assert.That(generated!).DoesNotContain("BlankIdEvent")
      .Because("a whitespace-only pinned id is not a usable identity and must not be committed to the ledger");
  }

  // ==================== No pinned types at all ====================

  /// <summary>
  /// Materializing an (empty) ledger file for every assembly that happens to compile through this
  /// generator — even ones with zero pinned types — would push a pointless
  /// .whizbang/pinned-type-ledger.json (and its MSBuild extraction step) onto every consumer
  /// project, including ones that never opted into the rename platform at all.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_NoPinnedTypesInCompilation_EmitsNoLedgerFileAsync() {
    const string source = """
        using Whizbang.Core;

        namespace TestApp;

        public class PlainEvent : IEvent { }
        """;

    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs");

    await Assert.That(generated).IsNull()
      .Because("an assembly with no pinned types must not materialize a ledger file at all");
  }
}
