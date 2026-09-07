using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="MessageJsonContextGenerator"/> targeting branches the
/// primary test suite does not reach: an abstract-polymorphic derived type that is already a
/// top-level message, array/list dedup across properties, ineligible <c>[JsonDerivedType]</c>
/// entries, a rename-ledger entry whose current type lives in a different assembly, polymorphic
/// base types discovered purely by inheritance (nested and internal interfaces), and the
/// parameterized-constructor / non-named / non-public branches of perspective TModel/TEvent
/// discovery. Every scenario is expressed as ordinary C# source fed through the same
/// <see cref="GeneratorTestHelper"/> harness the primary suite uses — no injected faults, no
/// corrupted resources.
/// </summary>
public class MessageJsonContextGeneratorCoverageTests {
  // ========================================
  // Abstract-polymorphic derived-type dedup (MessageJsonContextGenerator.cs:2010)
  // ========================================

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_JsonDerivedTypeAlreadyDiscoveredAsMessage_SkipsDuplicateNestedRegistrationAsync() {
    // A [JsonDerivedType] can name a type that is ALSO independently a top-level event/command.
    // If the generator did not recognize that overlap it would emit a SECOND Create_ factory for
    // the same CLR type from nested-type discovery, alongside the one already emitted for it as a
    // top-level message — a CS0111 duplicate-method compile error for anyone who reuses a message
    // type as one of several shapes under a polymorphic property.
    const string source = """
using Whizbang.Core;
using System.Text.Json.Serialization;

namespace TestApp;

[JsonPolymorphic]
[JsonDerivedType(typeof(ConcreteNotice), "concrete")]
public abstract record AbstractNotice {
  public string Name { get; init; } = "";
}

public record ConcreteNotice : AbstractNotice, IEvent {
  public int Code { get; init; }
}

public record NoticeContainer : ICommand {
  public AbstractNotice Notice { get; init; } = null!;
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    // The polymorphic base must still list the shared type as a derived type...
    await Assert.That(code!).Contains("CreatePolymorphic_TestApp_AbstractNotice");
    await Assert.That(code!).Contains("typeof(global::TestApp.ConcreteNotice)");

    // ...but its Create_ factory must exist exactly once (top-level discovery), never duplicated
    // by the nested-type discovery walk.
    const string factorySignature = "private JsonTypeInfo<global::TestApp.ConcreteNotice> Create_TestApp_ConcreteNotice(";
    var occurrences = code!.Split(factorySignature).Length - 1;
    await Assert.That(occurrences).IsEqualTo(1)
        .Because("a type that is both a top-level message and a [JsonDerivedType] target must get exactly one factory, or the generated file fails to compile with CS0111");
  }

  // ========================================
  // Array-type dedup (MessageJsonContextGenerator.cs:2449)
  // ========================================

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_TwoPropertiesShareArrayElementType_DedupesArrayFactoryAsync() {
    // Two unrelated messages each expose a string[] property. Without the ContainsKey dedup guard,
    // the generator would try to emit CreateArray_System_String twice — a CS0111 duplicate-method
    // compile error the moment two messages happen to share an array element type.
    const string source = """
using Whizbang.Core;

namespace TestApp;

public record TagsAssignedEvent : IEvent {
  public string[] Tags { get; init; } = System.Array.Empty<string>();
}

public record LabelsAssignedEvent : IEvent {
  public string[] Labels { get; init; } = System.Array.Empty<string>();
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Create_TestApp_TagsAssignedEvent");
    await Assert.That(code!).Contains("Create_TestApp_LabelsAssignedEvent");

    const string arrayFactorySignature = "private JsonTypeInfo<global::System.String[]> CreateArray_System_String(";
    var occurrences = code!.Split(arrayFactorySignature).Length - 1;
    await Assert.That(occurrences).IsEqualTo(1)
        .Because("two messages sharing an array element type must reuse one CreateArray_ factory, not duplicate it");
  }

  // ========================================
  // List<T> nested-collection skip (MessageJsonContextGenerator.cs:2491)
  // ========================================

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_ListOfUnrecognizedCollectionElement_SkipsCustomListFactoryAsync() {
    // List<HashSet<int>>: the element (HashSet<int>) is itself a collection type this generator
    // does not otherwise track. System.Text.Json already handles nested collections natively, so a
    // hand-rolled List<HashSet<int>> factory would either duplicate that handling or reference a
    // HashSet<int> JsonTypeInfo this generator never builds — a broken reference. The generator
    // must recognize the element as "still a collection" and skip emitting a custom factory for it.
    const string source = """
using Whizbang.Core;
using System.Collections.Generic;

namespace TestApp;

public record LayeredNoticeSent : IEvent {
  public List<HashSet<int>> Layers { get; init; } = new();
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Create_TestApp_LayeredNoticeSent");
    // Not DoesNotContain("HashSet") -- the element type legitimately appears in the envelope
    // factory's own property assignment. What must be absent is a hand-rolled list factory for it.
    await Assert.That(code!).DoesNotContain(
        "CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.HashSet")
        .Because("System.Text.Json handles nested collections natively; a custom factory here would "
               + "reference a HashSet<int> JsonTypeInfo this generator never builds -- a broken reference");
  }

  // ========================================
  // [JsonDerivedType] attribute filtering (MessageJsonContextGenerator.cs:3217, 3222, 3227)
  // ========================================

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_IneligibleJsonDerivedTypeEntries_AreSkippedWithoutLosingValidOneAsync() {
    // A [JsonDerivedType] list can contain entries the generator cannot use: a typeof() argument
    // that isn't a named type (an array), an abstract type (needs its own discovery, can't be
    // instantiated), and a non-public type (unreachable from the generated public context). If any
    // one of those poisoned the whole attribute scan, EVERY sibling [JsonDerivedType] on the same
    // base — including perfectly valid ones — would silently vanish from the polymorphic registry,
    // and STJ would throw the first time any of them round-trips.
    const string source = """
using Whizbang.Core;
using System.Text.Json.Serialization;

namespace TestApp;

[JsonPolymorphic]
[JsonDerivedType(typeof(int[]))]
[JsonDerivedType(typeof(AbandonedPanelConfig))]
[JsonDerivedType(typeof(InternalPanelConfig))]
[JsonDerivedType(typeof(ConcretePanelConfig), "concrete")]
public abstract record AbstractPanelConfig {
  public string Name { get; init; } = "";
}

public abstract record AbandonedPanelConfig : AbstractPanelConfig;

internal record InternalPanelConfig : AbstractPanelConfig {
  public int Value { get; init; }
}

public record ConcretePanelConfig : AbstractPanelConfig {
  public int Value { get; init; }
}

public record PanelConfigHolder : ICommand {
  public AbstractPanelConfig Settings { get; init; } = null!;
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code!).Contains("Create_TestApp_ConcretePanelConfig");
    await Assert.That(code!).DoesNotContain("AbandonedPanelConfig")
        .Because("an abstract [JsonDerivedType] target can't be instantiated and must never reach the registry");
    await Assert.That(code!).DoesNotContain("InternalPanelConfig")
        .Because("a non-public [JsonDerivedType] target is unreachable from the generated public context and must never reach the registry");

    // A `typeof(...)` for a derived type appears only in the polymorphic registration, so asserting
    // on the whole source is specific enough -- and avoids slicing from the factory's CALL site,
    // which precedes its definition and would window past the registration entirely.
    await Assert.That(code!).Contains("typeof(global::TestApp.ConcretePanelConfig)")
        .Because("the one eligible sibling must still be registered despite the ineligible entries "
               + "around it -- if one bad entry dropped the whole attribute scan, every valid "
               + "sibling would vanish and STJ would throw the first time any of them round-trips");
  }

  // ========================================
  // Rename ledger: current type not in this assembly (MessageJsonContextGenerator.cs:3464)
  // ========================================

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_RenameLedgerEntryForAnotherAssembly_SkipsAliasAsync() {
    // A shared .whizbang/pinned-type-ledger.json can list pinned types from OTHER assemblies. If
    // this generator aliased a former name whose CURRENT type isn't compiled here, it would emit
    // RegisterTypeName(..., typeof(SomethingNotInThisAssembly)) — a broken reference that fails to
    // compile for every consumer of a shared, multi-assembly ledger.
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        [PinnedId("11111111-2222-3333-4444-555555555555")]
        public record OrderPlacedEvent : IEvent;
        """;
    var ledger = """
      { "version": 1, "types": [
        { "pinnedId": "99999999-8888-7777-6666-555555555555",
          "clrTypeName": "OtherAssembly.ShipmentDispatchedEvent",
          "kind": "event",
          "formerNames": ["OtherAssembly.ShipmentSentEvent"] }
      ] }
      """;

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(
        source, [("/repo/src/TestAssembly/.whizbang/pinned-type-ledger.json", ledger)]);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).DoesNotContain("ShipmentSentEvent")
        .Because("a ledger entry whose current type isn't in THIS assembly must not produce an alias here");
    await Assert.That(generated!).DoesNotContain("ShipmentDispatchedEvent");
    // The in-assembly event's own registration is unaffected by the foreign ledger entry.
    await Assert.That(generated!).Contains("typeof(global::TestApp.OrderPlacedEvent)");
  }

  // ========================================
  // Polymorphic base resolved purely by inheritance, nested / internal
  // (MessageJsonContextGenerator.cs:3635, 3643)
  // ========================================

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_NestedInterfaceAsSharedBase_StillRegistersPolymorphicFactoryAsync() {
    // A nested interface used purely as a shared tag (no [JsonPolymorphic] needed) is auto-
    // discovered as a polymorphic base by inheritance. The generator's by-name resolver looks it up
    // with '.' separators (valid in emitted C# source) rather than the '+' metadata-name separator
    // nested types actually need, so the lookup itself fails — but the base type name is still
    // emitted as source text, so the auto-discovered factory must still work rather than silently
    // disappearing because the generator "couldn't see" the type well enough to double check it.
    const string source = """
using Whizbang.Core;

namespace TestApp;

public class MarkerHost {
  public interface INestedTag { }
}

public record TaggedEvent : MarkerHost.INestedTag, IEvent {
  public string Value { get; init; } = "";
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Create_TestApp_TaggedEvent");
    await Assert.That(code!).Contains("CreatePolymorphic_TestApp_MarkerHost_INestedTag")
        .Because("a nested interface shared by message types must still become an auto-discovered polymorphic base");
    await Assert.That(code!).Contains("typeof(global::TestApp.TaggedEvent)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_InternalInterfaceAsSharedBase_ExcludedFromPolymorphicRegistryAsync() {
    // An internal marker interface implemented by a public event is a legal, ordinary C# shape.
    // If the generator registered it as a polymorphic base anyway, the generated (public) context
    // would reference an inaccessible type and fail to compile for every consumer.
    const string source = """
using Whizbang.Core;

namespace TestApp;

internal interface IAuditMarker { }

public record AuditedEvent : IAuditMarker, IEvent {
  public string Value { get; init; } = "";
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Create_TestApp_AuditedEvent");
    await Assert.That(code!).DoesNotContain("IAuditMarker")
        .Because("an internal base interface must never appear in the generated public context, or consumer code fails to compile");
  }

  // ========================================
  // Perspective TModel/TEvent discovery: parameterized constructors, non-named, non-public
  // (MessageJsonContextGenerator.cs:3071, 3075, 3096-3097, 3136, 3141, 3162-3163)
  // ========================================

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_PerspectiveModelAndEventWithPrimaryConstructor_UseParameterizedCreationAsync() {
    // Both the perspective's TModel and its TEvent are positional records with a primary
    // constructor and NO parameterless constructor. If the generator failed to detect the matching
    // constructor for either one, it would emit `new T() { ... }` object-initializer code for a
    // type that has no parameterless constructor — a compile error for the generated context.
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestApp;

public record CounterModel(int Count);

public record CounterIncrementedEvent(int Amount) : IEvent;

public class CounterPerspective : IPerspectiveFor<CounterModel, CounterIncrementedEvent> {
  public CounterModel Apply(CounterModel currentData, CounterIncrementedEvent eventData)
    => currentData with { Count = currentData.Count + eventData.Amount };
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();

    await Assert.That(code!).Contains("new global::TestApp.CounterModel(")
        .Because("a perspective TModel with only a primary constructor must be created via that constructor");
    await Assert.That(code!).DoesNotContain("new global::TestApp.CounterModel() {")
        .Because("CounterModel has no parameterless constructor; the object-initializer form would fail to compile");

    await Assert.That(code!).Contains("new global::TestApp.CounterIncrementedEvent(")
        .Because("a perspective TEvent with only a primary constructor must be created via that constructor");
    await Assert.That(code!).DoesNotContain("new global::TestApp.CounterIncrementedEvent() {")
        .Because("CounterIncrementedEvent has no parameterless constructor; the object-initializer form would fail to compile");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_GenericPerspectiveWithOpenEventTypeParameter_OmitsUnboundEventTypeAsync() {
    // A generic perspective class's own event type parameter is, at its declaration site, an
    // unbound type parameter rather than a concrete named type. The generator must recognize that
    // shape and skip it instead of trying to treat the type-parameter name itself as a real type —
    // emitting "TEvt" as a type reference in generated source would simply fail to compile.
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestApp;

public record GenericTargetModel {
  public string Value { get; init; } = "";
}

public class GenericPerspective<TEvt> : IPerspectiveFor<GenericTargetModel, TEvt>
    where TEvt : IEvent {
  public GenericTargetModel Apply(GenericTargetModel currentData, TEvt eventData) => currentData;
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("GenericTargetModel")
        .Because("the concrete TModel sibling must still be discovered even though TEvent is unbound");
    await Assert.That(code!).DoesNotContain("TEvt")
        .Because("an unbound event type parameter must never be emitted as a literal type reference");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_ArrayTypeAsPerspectiveModel_SkipsNonNamedModelTypeAsync() {
    // TModel only needs to satisfy `where TModel : class`, which an array type satisfies. The
    // generator must recognize that an array isn't a named type it can generate property/constructor
    // metadata for, and skip it instead of throwing when it tries to inspect "properties" of string[].
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestApp;

public record ArrayModelEvent : IEvent {
  public int Amount { get; init; }
}

public class ArrayModelPerspective : IPerspectiveFor<string[], ArrayModelEvent> {
  public string[] Apply(string[] currentData, ArrayModelEvent eventData) => currentData;
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Create_TestApp_ArrayModelEvent");
    await Assert.That(code!).DoesNotContain("ArrayModelPerspective")
        .Because("an array-typed TModel must be skipped, not turned into a broken message-type registration");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageJsonContextGenerator_InternalPerspectiveModelAndEvent_ExcludedFromJsonContextAsync() {
    // A public perspective class can implement IPerspectiveFor<TModel, TEvent> with internal
    // TModel/TEvent by implementing Apply explicitly (the only legal way to do so — an implicit,
    // public Apply(InternalModel, InternalEvent) would itself fail to compile with CS0053). The
    // generated (public) JSON context can never reference either internal type, so both must be
    // excluded rather than emitted as inaccessible-type references that fail to compile.
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestApp;

internal record InternalOnlyModel(int Value);

internal record InternalOnlyEvent(int Amount) : IEvent;

public record VisiblePingEvent : IEvent {
  public string Value { get; init; } = "";
}

public class InternalTypesPerspective : IPerspectiveFor<InternalOnlyModel, InternalOnlyEvent> {
  InternalOnlyModel IPerspectiveFor<InternalOnlyModel, InternalOnlyEvent>.Apply(InternalOnlyModel currentData, InternalOnlyEvent eventData)
    => currentData;
}
""";

    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("Create_TestApp_VisiblePingEvent");
    await Assert.That(code!).DoesNotContain("InternalOnlyModel")
        .Because("an internal TModel must never reach the generated public context");
    await Assert.That(code!).DoesNotContain("InternalOnlyEvent")
        .Because("an internal TEvent must never reach the generated public context");
  }
}
