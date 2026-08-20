using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for MessageTypeCatalogGenerator.
/// Emits an AOT-safe IMessageTypeCatalog listing every concrete IMessage and
/// IPerspectiveFor&lt;&gt; type with its kind and optional pinned_id.
/// </summary>
public class MessageTypeCatalogGeneratorTests {

  [Test]
  public async Task Generator_WithEventsCommandsAndPerspectives_EmitsAllAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      namespace MyApp;

      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent;

      public record UnpinnedEvent : IEvent;

      [PinnedId("22222222-2222-2222-2222-222222222222")]
      public record PlaceOrderCommand : ICommand;

      public record OrderView;

      [PinnedId("33333333-3333-3333-3333-333333333333")]
      public class OrderPerspective : IPerspectiveFor<OrderView, OrderPlacedEvent> {
        public OrderView Apply(OrderView? current, OrderPlacedEvent @event) => current ?? new();
      }

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains(": IMessageTypeCatalog");

    // Entries for each discovered type
    await Assert.That(code!).Contains("typeof(global::MyApp.OrderPlacedEvent)");
    await Assert.That(code!).Contains("typeof(global::MyApp.UnpinnedEvent)");
    await Assert.That(code!).Contains("typeof(global::MyApp.PlaceOrderCommand)");
    await Assert.That(code!).Contains("typeof(global::MyApp.OrderPerspective)");

    // Pinned entries surface their pinned_id; unpinned surface null
    await Assert.That(code!).Contains("\"11111111-1111-1111-1111-111111111111\"");
    await Assert.That(code!).Contains("\"33333333-3333-3333-3333-333333333333\"");

    // Kinds
    await Assert.That(code!).Contains("\"event\"");
    await Assert.That(code!).Contains("\"command\"");
    await Assert.That(code!).Contains("\"perspective\"");
  }

  [Test]
  public async Task Generator_LedgerFormerNames_EmittedOnMatchingPinnedEntryAsync() {
    // A committed ledger records a former name for the pinned type -> the catalog entry must carry it,
    // so the registry populator can recognise the acknowledged rename and reconcile the stale row.
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent;
      public record UnpinnedEvent : IEvent;
""";
    const string ledger = """
      { "version": 1, "types": [
        { "pinnedId": "11111111-1111-1111-1111-111111111111", "clrTypeName": "MyApp.OrderPlacedEvent",
          "kind": "event", "formerNames": ["MyApp.OrderCreatedEvent"] }
      ] }
""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(
      source, [("/repo/src/MyApp/.whizbang/pinned-type-ledger.json", ledger)]);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    // The pinned entry carries its former name via the FormerNames initializer.
    await Assert.That(code!).Contains("FormerNames = new string[] { \"MyApp.OrderCreatedEvent\" }");
  }

  [Test]
  public async Task Generator_NoLedger_EmitsNoFormerNamesAsync() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp;
      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent;
""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("FormerNames");
  }

  [Test]
  public async Task Generator_SkipsAbstractTypesAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public abstract record BaseEvent : IEvent;
      public record ConcreteEvent : BaseEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).DoesNotContain("typeof(global::MyApp.BaseEvent)");
    await Assert.That(code!).Contains("typeof(global::MyApp.ConcreteEvent)");
  }

  [Test]
  public async Task Generator_WithNoTypes_GeneratesNothingAsync() {
    const string source = """
      namespace MyApp;
      public class PlainService { }
""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNull();
  }

  [Test]
  public async Task Generator_EmitsModuleInitializerThatRegistersViaAddWhizbangAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public record SomeEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("[System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(code!).Contains("ServiceRegistrationCallbacks.MessageTypeCatalog");
    await Assert.That(code!).Contains("AddSingleton<IMessageTypeCatalog, GeneratedMessageTypeCatalog>");
  }

  [Test]
  public async Task Generator_WithIPerspectiveWithActionsFor_EmitsPerspectiveEntryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      using Whizbang.Core.Perspectives;

      namespace MyApp;

      public record OrderShippedEvent : IEvent;
      public record OrderView;

      [PinnedId("44444444-4444-4444-4444-444444444444")]
      public class ActionsOrderPerspective : IPerspectiveWithActionsFor<OrderView, OrderShippedEvent> {
        public ApplyResult<OrderView> Apply(OrderView? current, OrderShippedEvent @event) =>
          ApplyResult<OrderView>.Update(current ?? new());
      }

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("typeof(global::MyApp.ActionsOrderPerspective)");
    await Assert.That(code!).Contains("\"perspective\"");
    await Assert.That(code!).Contains("\"44444444-4444-4444-4444-444444444444\"");
  }

  [Test]
  public async Task Generator_NestedMessageType_ClrTypeNameUsesPlusNotDotAsync() {
    // The catalog's ClrTypeName seeds wh_message_type_registry.clr_type_name (via reconcile
    // migration 040) and drives DapperEventTypeRenameTool drift detection. Every other writer of
    // a clr_type_name — PerspectiveDiscoveryGenerator, PerspectiveRunnerRegistryGenerator — uses
    // TypeNameUtilities.BuildClrTypeName, which renders nested types with '+' (CLR format). The
    // catalog must agree, or a NESTED message type is registered as "Ns.Outer.Nested" here but
    // stored/compared as "Ns.Outer+Nested" everywhere else, so drift detection and rename never
    // match. Lock the '+' (CLR) form.
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public static class OrderContracts {
        public record OrderPlacedEvent : IEvent;
      }

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");
    await Assert.That(code).IsNotNull();

    // ClrTypeName is emitted as the quoted 2nd ctor arg: new(typeof(...), "<ClrTypeName>", ...).
    // The quoted form must use '+' for the nested type — NOT the C# '.' display form.
    await Assert.That(code!).Contains("\"MyApp.OrderContracts+OrderPlacedEvent\"");
    await Assert.That(code!).DoesNotContain("\"MyApp.OrderContracts.OrderPlacedEvent\"");
  }

  [Test]
  public async Task Generator_EntryContainsNullWhenUnpinnedAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public record UnpinnedEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    // Unpinned entry must surface a null in the pinned_id position
    await Assert.That(code!).Contains("typeof(global::MyApp.UnpinnedEvent)");
    await Assert.That(code!).Contains("null");
  }

  [Test]
  public async Task Generator_StampsMarkerInterfaceFlagsOnEntriesAsync() {
    // The transport receive path derives EventFlags from these compile-time stamps by type name —
    // an incoming payload is a JsonElement there, so runtime `payload is ICollectiveEvent` checks
    // are blind and a missing stamp silently drops the flags at every service boundary.
    const string source = """

      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Messaging;
      using Whizbang.Core.Minting;

      namespace MyApp;

      public record BulkAppliedCollectiveEvent : ICollectiveEvent {
        public CollectiveScope Scope => null!;
      }

      public record BatchCompositeEvent : ICompositeEvent {
        public IEnumerable<IMessage> InnerEvents => [];
      }

      public record PeriodCompactedEvent : ICompactedEvent;

      public record PlainSourcedEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).IsNotNull();
    var lines = code!.Split('\n');
    var collectiveLine = lines.Single(l => l.Contains("typeof(global::MyApp.BulkAppliedCollectiveEvent)"));
    var compositeLine = lines.Single(l => l.Contains("typeof(global::MyApp.BatchCompositeEvent)"));
    var compactedLine = lines.Single(l => l.Contains("typeof(global::MyApp.PeriodCompactedEvent)"));
    var sourcedLine = lines.Single(l => l.Contains("typeof(global::MyApp.PlainSourcedEvent)"));

    await Assert.That(collectiveLine).Contains("IsCollective = true");
    await Assert.That(compositeLine).Contains("IsComposite = true");
    await Assert.That(compactedLine).Contains("IsCompacted = true");
    // A plain Sourced event carries none of the marker stamps (the record defaults are false).
    await Assert.That(sourcedLine.Contains("IsCollective")).IsFalse();
    await Assert.That(sourcedLine.Contains("IsComposite")).IsFalse();
    await Assert.That(sourcedLine.Contains("IsCompacted")).IsFalse();
  }
}
