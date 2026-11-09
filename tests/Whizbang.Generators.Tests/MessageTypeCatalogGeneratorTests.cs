// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
    await Assert.That(code).Contains(": IMessageTypeCatalog");

    // Entries for each discovered type
    await Assert.That(code).Contains("typeof(global::MyApp.OrderPlacedEvent)");
    await Assert.That(code).Contains("typeof(global::MyApp.UnpinnedEvent)");
    await Assert.That(code).Contains("typeof(global::MyApp.PlaceOrderCommand)");
    await Assert.That(code).Contains("typeof(global::MyApp.OrderPerspective)");

    // Pinned entries surface their pinned_id; unpinned surface null
    await Assert.That(code).Contains("\"11111111-1111-1111-1111-111111111111\"");
    await Assert.That(code).Contains("\"33333333-3333-3333-3333-333333333333\"");

    // Kinds
    await Assert.That(code).Contains("\"event\"");
    await Assert.That(code).Contains("\"command\"");
    await Assert.That(code).Contains("\"perspective\"");
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
    await Assert.That(code).Contains("FormerNames = new string[] { \"MyApp.OrderCreatedEvent\" }");
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
    await Assert.That(code).DoesNotContain("FormerNames");
  }

  /// <summary>
  /// #2074: a message that is neither a command nor an event is catalogued as a plain "message", never as an event,
  /// so the receive path never classifies it as an event or copies it into the event store.
  /// </summary>
  [Test]
  public async Task Generator_APlainMessage_IsKindMessage_NotEventAsync() {
    const string source = """

      using Whizbang.Core;

      namespace MyApp;

      public record Ping : IMessage;
      public record Pinged : IEvent;
      public record DoPing : ICommand;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs");

    await Assert.That(code).Contains("new(typeof(global::MyApp.Ping), \"MyApp.Ping\", \"message\"")
      .Because("a plain message is not an event");
    await Assert.That(code).Contains("new(typeof(global::MyApp.Pinged), \"MyApp.Pinged\", \"event\"");
    await Assert.That(code).Contains("new(typeof(global::MyApp.DoPing), \"MyApp.DoPing\", \"command\"");
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
    await Assert.That(code).DoesNotContain("typeof(global::MyApp.BaseEvent)");
    await Assert.That(code).Contains("typeof(global::MyApp.ConcreteEvent)");
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
    await Assert.That(code).Contains("[System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(code).Contains("ServiceRegistrationCallbacks.MessageTypeCatalog");
    await Assert.That(code).Contains("AddSingleton<IMessageTypeCatalog, GeneratedMessageTypeCatalog>");
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
    await Assert.That(code).Contains("typeof(global::MyApp.ActionsOrderPerspective)");
    await Assert.That(code).Contains("\"perspective\"");
    await Assert.That(code).Contains("\"44444444-4444-4444-4444-444444444444\"");
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
    await Assert.That(code).Contains("\"MyApp.OrderContracts+OrderPlacedEvent\"");
    await Assert.That(code).DoesNotContain("\"MyApp.OrderContracts.OrderPlacedEvent\"");
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
    await Assert.That(code).Contains("typeof(global::MyApp.UnpinnedEvent)");
    await Assert.That(code).Contains("null");
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

  [Test]
  public async Task Generator_StampsMaxPayloadSizeOnEntriesAsync() {
    // The payload limit reads a type's own limit from the catalog, so a type's [MaxPayloadSize]
    // must reach it at compile time, including through a base type and on commands.
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp;

      [MaxPayloadSize(20_000_000)]
      public record LargeImportedEvent : IEvent;

      [MaxPayloadSize(0)]
      public record UnlimitedEvent : IEvent;

      [MaxPayloadSize(1024)]
      public record SmallCommand : ICommand;

      public record DerivedLargeEvent : LargeImportedEvent;

      public record PlainEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var lines = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!.Split('\n');
    string line(string type) => lines.Single(l => l.Contains($"typeof(global::MyApp.{type})"));

    await Assert.That(line("LargeImportedEvent")).Contains("MaxPayloadBytes = 20000000L");
    await Assert.That(line("UnlimitedEvent")).Contains("MaxPayloadBytes = 0L");
    await Assert.That(line("SmallCommand")).Contains("MaxPayloadBytes = 1024L");
    await Assert.That(line("DerivedLargeEvent")).Contains("MaxPayloadBytes = 20000000L")
      .Because("the attribute is inherited, so a derived message keeps its base type's limit");
    await Assert.That(line("PlainEvent").Contains("MaxPayloadBytes")).IsFalse();
  }

  [Test]
  public async Task Generator_RegistersMessageDefaultRouting_WithoutReflectionAsync() {
    // The cascade reads a message type's [DefaultRouting] from the generated registration (#1395), so
    // the attribute must reach it at compile time, including through a base type.
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Dispatch;

      namespace MyApp;

      [DefaultRouting(DispatchModes.Local)]
      public record LocalOnlyEvent : IEvent;

      [DefaultRouting(DispatchModes.Outbox)]
      public record OutboxCommand : ICommand;

      public record DerivedLocalEvent : LocalOnlyEvent;

      public record PlainEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!;
    var lines = generated.Split('\n');
    string entry(string type) => lines.Single(l => l.Contains($"new(typeof(global::MyApp.{type})"));

    await Assert.That(entry("LocalOnlyEvent")).Contains("DefaultRouting = global::Whizbang.Core.Dispatch.DispatchModes.Local");
    await Assert.That(entry("OutboxCommand")).Contains("DefaultRouting = global::Whizbang.Core.Dispatch.DispatchModes.Outbox");
    await Assert.That(entry("DerivedLocalEvent")).Contains("DefaultRouting = global::Whizbang.Core.Dispatch.DispatchModes.Local")
      .Because("the attribute is inherited, so a derived message keeps its base type's routing");
    await Assert.That(entry("PlainEvent").Contains("DefaultRouting")).IsFalse();
    await Assert.That(generated).Contains(
      "global::Whizbang.Core.Dispatch.MessageDefaultRouting.Register(typeof(global::MyApp.LocalOnlyEvent), global::Whizbang.Core.Dispatch.DispatchModes.Local);");
    await Assert.That(generated).Contains(
      "global::Whizbang.Core.Dispatch.MessageDefaultRouting.Register(typeof(global::MyApp.DerivedLocalEvent), global::Whizbang.Core.Dispatch.DispatchModes.Local);");
    await Assert.That(generated.Contains("MessageDefaultRouting.Register(typeof(global::MyApp.PlainEvent)")).IsFalse();
  }

  [Test]
  public async Task Generator_StreamEndOfLife_RegistersTheDeclarationOfEachEventType_InheritedAsync() {
    // #1740: the end-of-life policy is declared on the event type that opens a stream, keyed by the CLR type name the
    // ledger records as the stream type (nested types with '+'), inherited, and only for events.
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Archival;
      using Whizbang.Core.Attributes;

      namespace MyApp;

      [StreamEndOfLife(IdleTtl = "90.00:00:00", MaxAge = "365.00:00:00", KeepTailVersions = 10, TenantByteCap = 2048, Action = StreamLifecycleAction.Purge)]
      public record OrderPlaced : IEvent;

      public record RushOrderPlaced : OrderPlaced;

      public static class Ledgers {
        [StreamEndOfLife(IdleTtl = null)]
        public record LedgerOpened : IEvent;
      }

      [StreamEndOfLife(IdleTtl = "1.00:00:00")]
      public record PlaceOrder : ICommand;

      public record CartStarted : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!;
    const string register = "global::Whizbang.Core.Archival.StreamEndOfLifeDeclarations.Register(new global::Whizbang.Core.Archival.StreamEndOfLifeDeclaration(";
    const string purge = "(global::Whizbang.Core.Archival.StreamLifecycleAction)2";

    await Assert.That(generated).Contains($"{register}\"MyApp.OrderPlaced\", \"90.00:00:00\", \"365.00:00:00\", 10L, 2048L, {purge}));");
    await Assert.That(generated).Contains($"{register}\"MyApp.RushOrderPlaced\", \"90.00:00:00\", \"365.00:00:00\", 10L, 2048L, {purge}));")
      .Because("the attribute is inherited, so the streams a derived event opens carry the same policy");
    await Assert.That(generated).Contains($"{register}\"MyApp.Ledgers+LedgerOpened\", null, null, 0L, 0L, (global::Whizbang.Core.Archival.StreamLifecycleAction)0));")
      .Because("the key is the ledger's stream type, the CLR name with '+' for a nested type");
    await Assert.That(generated.Contains($"{register}\"MyApp.PlaceOrder\"")).IsFalse().Because("a command never opens a stream");
    await Assert.That(generated.Contains($"{register}\"MyApp.CartStarted\"")).IsFalse();
  }

  [Test]
  public async Task Generator_TerminalEvent_StampsTheEntryAndRegistersTheTypeAsync() {
    // A terminal event seals its stream (WS-3 S4). The flag is stamped from the generated registration on the dispatch
    // path and from the catalog entry on the receive path, so the attribute must reach both at compile time, including
    // through a base type, and only for events.
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp;

      [TerminalEvent]
      public record AccountClosed : IEvent;

      public record AccountClosedForFraud : AccountClosed;

      [TerminalEvent]
      public record CloseAccount : ICommand;

      public record AccountOpened : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!;
    var lines = generated.Split('\n');
    string entry(string type) => lines.Single(l => l.Contains($"new(typeof(global::MyApp.{type})"));

    await Assert.That(entry("AccountClosed")).Contains("IsTerminal = true");
    await Assert.That(entry("AccountClosedForFraud")).Contains("IsTerminal = true")
      .Because("the attribute is inherited, so an event derived from a terminal event is terminal too");
    await Assert.That(entry("CloseAccount").Contains("IsTerminal")).IsFalse()
      .Because("a command is never stored on a stream, so it cannot end one");
    await Assert.That(entry("AccountOpened").Contains("IsTerminal")).IsFalse();
    await Assert.That(generated).Contains(
      "global::Whizbang.Core.Lifecycle.TerminalEvents.Register(typeof(global::MyApp.AccountClosed));");
    await Assert.That(generated).Contains(
      "global::Whizbang.Core.Lifecycle.TerminalEvents.Register(typeof(global::MyApp.AccountClosedForFraud));");
    await Assert.That(generated.Contains("TerminalEvents.Register(typeof(global::MyApp.CloseAccount)")).IsFalse();
    await Assert.That(generated.Contains("TerminalEvents.Register(typeof(global::MyApp.AccountOpened)")).IsFalse();
  }

  [Test]
  public async Task Generator_SupersedingEvent_StampsTheEntryAndRegistersTheKeyAsync() {
    // A superseding event (#1729) is flagged from the generated registration on the dispatch path and from the catalog
    // entry on the receive path, and its key is read by a generated accessor, never by reflection. The attribute is
    // inherited, a key may sit on a property or a record parameter or a base type, and only an event can supersede.
    const string source = """

      using System;
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp;

      [Superseding]
      public record EmbeddingUpdated([property: SupersedingKey] string Model, float[] Vector) : IEvent;

      [Superseding]
      public record OwnerSnapshot : IEvent {
        [SupersedingKey] public Guid Owner { get; init; }
      }

      public record DerivedOwnerSnapshot : OwnerSnapshot;

      [Superseding]
      public record ParameterKeyed([SupersedingKey] int Slot) : IEvent;

      [Superseding]
      public record RenamedKey([property: SupersedingKey, System.Text.Json.Serialization.JsonPropertyName("slot_\"no")] int Slot) : IEvent;

      [Superseding]
      public record PermissionsReplaced(string[] Grants) : IEvent;

      [Superseding]
      public record ReplaceAll : ICommand;

      public record ProfileRenamed : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!;
    var lines = generated.Split('\n');
    string entry(string type) => lines.Single(l => l.Contains($"new(typeof(global::MyApp.{type})"));
    const string REGISTER = "global::Whizbang.Core.Lifecycle.SupersedingEvents.Register(typeof(global::MyApp.";
    const string KEY_TEXT = "global::Whizbang.Core.Lifecycle.SupersedingEvents.KeyText";

    await Assert.That(entry("EmbeddingUpdated")).Contains("IsSuperseding = true");
    await Assert.That(entry("DerivedOwnerSnapshot")).Contains("IsSuperseding = true")
      .Because("the attribute is inherited, so an event derived from a superseding event supersedes too");
    await Assert.That(entry("ReplaceAll").Contains("IsSuperseding")).IsFalse()
      .Because("a command is never stored on a stream, so nothing can supersede it");
    await Assert.That(entry("ProfileRenamed").Contains("IsSuperseding")).IsFalse();
    await Assert.That(generated).Contains(
      $"{REGISTER}EmbeddingUpdated), static p => {KEY_TEXT}(((global::MyApp.EmbeddingUpdated)p).Model), \"Model\", null);")
      .Because("a key on a record's positional property is read through that property");
    await Assert.That(generated).Contains(
      $"{REGISTER}OwnerSnapshot), static p => {KEY_TEXT}(((global::MyApp.OwnerSnapshot)p).Owner), \"Owner\", null);");
    await Assert.That(generated).Contains(
      $"{REGISTER}DerivedOwnerSnapshot), static p => {KEY_TEXT}(((global::MyApp.DerivedOwnerSnapshot)p).Owner), \"Owner\", null);")
      .Because("a key declared on a base type is the derived type's key");
    await Assert.That(generated).Contains(
      $"{REGISTER}ParameterKeyed), static p => {KEY_TEXT}(((global::MyApp.ParameterKeyed)p).Slot), \"Slot\", null);")
      .Because("on a primary-constructor parameter the attribute marks the property of the same name");
    await Assert.That(generated).Contains(
      $"{REGISTER}RenamedKey), static p => {KEY_TEXT}(((global::MyApp.RenamedKey)p).Slot), \"Slot\", \"slot_\\\"no\");")
      .Because("#2123: the key's member and [JsonPropertyName] are registered, so a payload missing the key is told from a default key");
    await Assert.That(generated).Contains($"{REGISTER}PermissionsReplaced), null);")
      .Because("a superseding type with no key has one key per stream");
    await Assert.That(generated.Contains($"{REGISTER}ReplaceAll)")).IsFalse();
    await Assert.That(generated.Contains($"{REGISTER}ProfileRenamed)")).IsFalse();
  }

  [Test]
  public async Task Generator_ASupersedingGroup_IsRegisteredWithEachTypeAsync() {
    // #2124: [Superseding(Group = "...")] lets types supersede each other; the generator registers the group with each
    // type, so the write paths stamp it without reflection. The name is taken as written, never derived from a type.
    const string source = """

      using System;
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp;

      [Superseding(Group = "embedding")]
      public record EmbeddingCreated([property: SupersedingKey] string Model) : IEvent;

      [Superseding(Group = "embedding")]
      public record EmbeddingRevised([property: SupersedingKey] string Model) : IEvent;

      [Superseding(Group = "profile")]
      public record ProfileBase : IEvent;

      public record ProfileReplaced : ProfileBase;

      [Superseding]
      public record ProfileRedeclared : ProfileBase;

      [Superseding(Group = "")]
      public record EmptyGroup : IEvent;

      [Superseding(Group = "quo\"te")]
      public record QuotedGroup : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!;
    const string REGISTER = "global::Whizbang.Core.Lifecycle.SupersedingEvents.Register(typeof(global::MyApp.";
    const string KEY_TEXT = "global::Whizbang.Core.Lifecycle.SupersedingEvents.KeyText";

    await Assert.That(generated).Contains(
      $"{REGISTER}EmbeddingCreated), static p => {KEY_TEXT}(((global::MyApp.EmbeddingCreated)p).Model), \"Model\", null, \"embedding\");");
    await Assert.That(generated).Contains(
      $"{REGISTER}EmbeddingRevised), static p => {KEY_TEXT}(((global::MyApp.EmbeddingRevised)p).Model), \"Model\", null, \"embedding\");");
    await Assert.That(generated).Contains($"{REGISTER}ProfileBase), null, null, null, \"profile\");")
      .Because("a grouped type with no key registers the empty key's group");
    await Assert.That(generated).Contains($"{REGISTER}ProfileReplaced), null, null, null, \"profile\");")
      .Because("the attribute is inherited, group and all");
    await Assert.That(generated).Contains($"{REGISTER}ProfileRedeclared), null);")
      .Because("the nearest [Superseding] wins, as attribute inheritance reads it, so a redeclaration without a group has none");
    await Assert.That(generated).Contains($"{REGISTER}EmptyGroup), null);")
      .Because("an empty name is no group");
    await Assert.That(generated).Contains($"{REGISTER}QuotedGroup), null, null, null, \"quo\\\"te\");")
      .Because("a group name is arbitrary text, so it is written as an escaped literal");
  }

  [Test]
  public async Task Generator_ASupersedingTypeAnAnalyzerRefuses_IsNotRegistered_SoEveryEventIsKeptAsync() {
    // WHIZ135 (also ephemeral) and WHIZ136 (two keys) are errors, but an error can be suppressed. The generator then
    // registers nothing, so the type is never reaped as superseding: keeping events is the recoverable direction.
    const string source = """

      using System;
      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp;

      [Ephemeral, Superseding]
      public record PresenceReplaced : IEvent;

      [Superseding]
      public record TwoKeys([property: SupersedingKey] string Model, [property: SupersedingKey] Guid Owner) : IEvent;

      [Superseding]
      public record OneKey([property: SupersedingKey] string Model) : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!;
    var lines = generated.Split('\n');
    string entry(string type) => lines.Single(l => l.Contains($"new(typeof(global::MyApp.{type})"));

    await Assert.That(entry("PresenceReplaced").Contains("IsSuperseding")).IsFalse()
      .Because("an ephemeral event is reaped by the ephemeral reaper; it is never also superseding");
    await Assert.That(entry("TwoKeys").Contains("IsSuperseding")).IsFalse()
      .Because("with two keys the generator cannot tell which one the type means");
    await Assert.That(generated.Contains("SupersedingEvents.Register(typeof(global::MyApp.PresenceReplaced)")).IsFalse();
    await Assert.That(generated.Contains("SupersedingEvents.Register(typeof(global::MyApp.TwoKeys)")).IsFalse();
    await Assert.That(entry("OneKey")).Contains("IsSuperseding = true").Because("the control: a valid superseding type is registered");
  }

  [Test]
  public async Task Generator_CombinedDefaultRoutingFlags_RenderAsACastAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Dispatch;

      namespace MyApp;

      [DefaultRouting(DispatchModes.LocalDispatch | DispatchModes.Outbox | DispatchModes.EventStore)]
      public record EverywhereEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageTypeCatalog.g.cs")!;

    await Assert.That(generated).Contains("(global::Whizbang.Core.Dispatch.DispatchModes)7");
  }

  [Test]
  public async Task Generator_EveryNamedDispatchMode_RendersByNameAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Dispatch;

      namespace MyApp;

      [DefaultRouting(DispatchModes.None)]
      public record NoneEvent : IEvent;

      [DefaultRouting(DispatchModes.LocalDispatch)]
      public record LocalDispatchEvent : IEvent;

      [DefaultRouting(DispatchModes.Both)]
      public record BothEvent : IEvent;

      [DefaultRouting(DispatchModes.EventStoreOnly)]
      public record StoreOnlyEvent : IEvent;

""";

    var generated = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<MessageTypeCatalogGenerator>(source), "MessageTypeCatalog.g.cs")!;

    await Assert.That(generated).Contains("Register(typeof(global::MyApp.NoneEvent), global::Whizbang.Core.Dispatch.DispatchModes.None)");
    await Assert.That(generated).Contains("Register(typeof(global::MyApp.LocalDispatchEvent), global::Whizbang.Core.Dispatch.DispatchModes.LocalDispatch)");
    await Assert.That(generated).Contains("Register(typeof(global::MyApp.BothEvent), global::Whizbang.Core.Dispatch.DispatchModes.Both)");
    await Assert.That(generated).Contains("Register(typeof(global::MyApp.StoreOnlyEvent), global::Whizbang.Core.Dispatch.DispatchModes.EventStoreOnly)");
  }
}
