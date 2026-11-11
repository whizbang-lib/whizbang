// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #2072, gap 3: the stream a cascaded message inherits from the message that caused it, when the message's
/// <c>[StreamId]</c> is a strong id. The generated extractor reports each message type's strong-id type
/// (<c>GetStreamIdType</c>), and its setter takes an inherited stream only from a plain Guid source, a source of the
/// same strong id, or a type <c>[StreamId(InheritFrom = ...)]</c> lists, and only through the id's <c>From</c>. Each test
/// compiles contracts and a probe with the stream-id and strong-id generators, runs the probe, and reads what the
/// generated extractor did.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/StreamIdGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Utilities/StrongIdStreamIds.cs</code-under-test>
[Category("SourceGenerators")]
public class StrongIdStreamInheritanceGeneratorTests {
  private const string EXTRACTORS = "StreamIdExtractors.g.cs";

  /// <summary>A version 7 Guid, which every strong id's <c>From</c> takes.</summary>
  private static readonly Guid _v7 = Guid.Parse("01890a5d-ac96-774b-bcce-b302099a8057");

  /// <summary>A version 4 Guid, which a strong id that takes only UUIDv7 refuses.</summary>
  private static readonly Guid _v4 = Guid.Parse("6f9619ff-8b86-4011-b42d-00c04fc964ff");

  private const string IDS = """
    namespace App.Contracts {
      [WhizbangId]
      public readonly partial struct OrderId;

      [WhizbangId]
      public readonly partial struct InvoiceId;

      [WhizbangId]
      public readonly partial struct ShipmentId;
    }
    """;

  /// <summary>
  /// Compiles <paramref name="contracts"/> and <paramref name="probe"/> with the stream-id and strong-id generators,
  /// loads the result and returns what <c>App.Probe.Run()</c> returned.
  /// </summary>
  [RequiresAssemblyFiles]
  private static string _runProbe(string assemblyName, string contracts, string probe) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      {{contracts}}
      namespace App {
        public static class Probe {
          public static string Run() {
            IStreamIdExtractor extractor = new global::{{assemblyName}}.Generated.GeneratedStreamIdExtractor();
            {{probe}}
          }
        }
      }
      """;
    var (_, output) = GeneratorTestHelper.RunGeneratorsWithFullReferences(
      [new StreamIdGenerator(), new WhizbangIdGenerator()], source, assemblyName);
    using var image = new MemoryStream();
    var emitted = output.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException(
        "The probe does not compile: "
        + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    var assembly = System.Reflection.Assembly.Load(image.ToArray());
    var run = assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("Run")!;
    return (string)run.Invoke(null, null)!;
  }

  /// <summary>
  /// The probe body: set <paramref name="value"/> on a new <paramref name="type"/> as inherited from a source whose
  /// stream-id type is <paramref name="sourceType"/> (a C# expression), and report the result and the property.
  /// </summary>
  private static string _inheritProbe(string type, string property, Guid value, string sourceType) => $$"""
    var message = new {{type}}();
    var wrote = extractor.SetStreamId(message, new Guid("{{value}}"), {{sourceType}});
    return $"set={wrote};own={message.{{property}}.ToGuid()}";
    """;

  private static string _written(Guid value) => $"set=True;own={value}";

  private static string _refused() => $"set=False;own={Guid.Empty}";

  private const string INVOICE_CREATED = """
    namespace App.Contracts {
      public class InvoiceCreated : IEvent { [StreamId] public InvoiceId InvoiceId { get; set; } }
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task ASourceOfTheSameStrongId_IsWrittenAsync() {
    var observed = _runProbe("InheritSameId", IDS + INVOICE_CREATED,
      _inheritProbe("App.Contracts.InvoiceCreated", "InvoiceId", _v7, "typeof(App.Contracts.InvoiceId)"));

    await Assert.That(observed).IsEqualTo(_written(_v7))
      .Because("a source of the same strong id is the same kind of stream");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task APlainGuidSource_IsWrittenAsync() {
    var observed = _runProbe("InheritPlainGuid", IDS + INVOICE_CREATED,
      _inheritProbe("App.Contracts.InvoiceCreated", "InvoiceId", _v7, "null"));

    await Assert.That(observed).IsEqualTo(_written(_v7))
      .Because("a source whose stream id is not a strong id reports no type and is always accepted");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ASourceOfAnotherStrongId_IsRefusedAsync() {
    var observed = _runProbe("InheritOtherId", IDS + INVOICE_CREATED,
      _inheritProbe("App.Contracts.InvoiceCreated", "InvoiceId", _v7, "typeof(App.Contracts.OrderId)"));

    await Assert.That(observed).IsEqualTo(_refused())
      .Because("an OrderId stream is not an InvoiceId stream unless the message says it may be");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ASourceTheAttributeLists_IsWrittenAsync() {
    const string contracts = """
      namespace App.Contracts {
        public class CreateInvoice : ICommand {
          [StreamId(InheritFrom = [typeof(OrderId), typeof(ShipmentId)])]
          public InvoiceId InvoiceId { get; set; }
        }
      }
      """;
    var probe = $$"""
      var fromOrder = new App.Contracts.CreateInvoice();
      var fromShipment = new App.Contracts.CreateInvoice();
      var other = new App.Contracts.CreateInvoice();
      var a = extractor.SetStreamId(fromOrder, new Guid("{{_v7}}"), typeof(App.Contracts.OrderId));
      var b = extractor.SetStreamId(fromShipment, new Guid("{{_v7}}"), typeof(App.Contracts.ShipmentId));
      var c = extractor.SetStreamId(other, new Guid("{{_v7}}"), typeof(Uri));
      return $"{a}:{fromOrder.InvoiceId.ToGuid()};{b}:{fromShipment.InvoiceId.ToGuid()};{c}:{other.InvoiceId.ToGuid()}";
      """;

    var observed = _runProbe("InheritListed", IDS + contracts, probe);

    await Assert.That(observed).IsEqualTo($"True:{_v7};True:{_v7};False:{Guid.Empty}")
      .Because("each listed type is accepted, and a type the list does not name is still refused");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AGuidTheStrongIdsFromRefuses_IsRefusedAsync() {
    var observed = _runProbe("InheritNonV7", IDS + INVOICE_CREATED,
      _inheritProbe("App.Contracts.InvoiceCreated", "InvoiceId", _v4, "null"));

    await Assert.That(observed).IsEqualTo(_refused())
      .Because("the value goes through From, which takes only UUIDv7 here, and a refusal must not throw during dispatch");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ThePlainSetter_TakesTheValueAsAPlainGuidAsync() {
    var probe = $$"""
      var message = new App.Contracts.InvoiceCreated();
      var refused = new App.Contracts.InvoiceCreated();
      var wrote = extractor.SetStreamId(message, new Guid("{{_v7}}"));
      var notWritten = global::PlainSetter.Generated.StreamIdExtractors.SetStreamId(refused, new Guid("{{_v4}}"));
      return $"{wrote}:{message.InvoiceId.ToGuid()};{notWritten}:{refused.InvoiceId.ToGuid()}";
      """;

    var observed = _runProbe("PlainSetter", IDS + INVOICE_CREATED, probe);

    await Assert.That(observed).IsEqualTo($"True:{_v7};False:{Guid.Empty}")
      .Because("the setter without a source type writes a strong id through From, as for a plain Guid source");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AGuidStreamId_AcceptsASourceOfAnyTypeAsync() {
    const string contracts = """
      namespace App.Contracts {
        public class InvoiceNoted : IEvent { [StreamId] public Guid InvoiceId { get; set; } }
      }
      """;
    var probe = $$"""
      var message = new App.Contracts.InvoiceNoted();
      var wrote = extractor.SetStreamId(message, new Guid("{{_v4}}"), typeof(App.Contracts.OrderId));
      return $"set={wrote};own={message.InvoiceId}";
      """;

    var observed = _runProbe("InheritIntoGuid", IDS + contracts, probe);

    await Assert.That(observed).IsEqualTo(_written(_v4))
      .Because("a Guid stream id carries no id type to protect, so it inherits as it always has");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task GetStreamIdType_ReportsTheStrongIdOfEachMessageTypeAsync() {
    const string contracts = """
      namespace App.Contracts {
        public class PlaceOrder : ICommand { [StreamId] public OrderId OrderId { get; set; } }
        public class OrderPlaced : IEvent { [StreamId] public OrderId OrderId { get; init; } }
        public class OrderNoted : IEvent { [StreamId] public Guid OrderId { get; set; } }
        public class InvoiceBase : IEvent { [StreamId] public InvoiceId InvoiceId { get; set; } }
        public class InvoiceRaised : InvoiceBase;
        public class InvoiceRekeyed : InvoiceBase { [StreamId(Override = true)] public Guid Key { get; set; } }
      }
      """;
    const string probe = """
      string Name(Type t) => extractor.GetStreamIdType(t)?.Name ?? "null";
      return string.Join(",", Name(typeof(App.Contracts.PlaceOrder)), Name(typeof(App.Contracts.OrderPlaced)),
        Name(typeof(App.Contracts.OrderNoted)), Name(typeof(App.Contracts.InvoiceRaised)),
        Name(typeof(App.Contracts.InvoiceRekeyed)), Name(typeof(string)));
      """;

    var observed = _runProbe("StreamIdTypes", IDS + contracts, probe);

    await Assert.That(observed).IsEqualTo("OrderId,OrderId,null,InvoiceId,null,null")
      .Because("a strong id is reported whether or not it is settable; a Guid stream id, a derived type's own Guid stream id and an unknown type report none");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AStrongIdDiscoveredFromItsProperty_IsReportedAndWrittenAsync() {
    // The strong-id generator emits ShipmentKey from the property's [WhizbangId], in the namespace the attribute names,
    // so the stream-id generator never sees the type and names it from that rule
    const string contracts = """
      namespace App.Contracts {
        using App.Keys;

        public class ShipmentBooked : IEvent {
          [StreamId] [WhizbangId("App.Keys")]
          public ShipmentKey Key { get; set; }
        }
      }
      """;
    var probe = $$"""
      var message = new App.Contracts.ShipmentBooked();
      var wrote = extractor.SetStreamId(message, new Guid("{{_v7}}"), typeof(global::App.Keys.ShipmentKey));
      return $"{extractor.GetStreamIdType(typeof(App.Contracts.ShipmentBooked))?.FullName};{wrote};{message.Key.ToGuid()}";
      """;

    var observed = _runProbe("PropertyDiscoveredId", contracts, probe);

    await Assert.That(observed).IsEqualTo($"App.Keys.ShipmentKey;True;{_v7}");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AListedStrongIdDiscoveredFromAProperty_IsAcceptedAsync() {
    // ShipmentKey exists only once the strong-id generator has run, so the stream-id generator cannot name it and
    // matches the listed type by name
    const string contracts = """
      namespace App.Contracts {
        public class ShipmentBooked : IEvent {
          [StreamId] [WhizbangId]
          public ShipmentKey Key { get; set; }
        }

        public class InvoiceShipment : ICommand {
          [StreamId(InheritFrom = [typeof(ShipmentKey)])]
          public InvoiceId InvoiceId { get; set; }
        }
      }
      """;
    var probe = $$"""
      var message = new App.Contracts.InvoiceShipment();
      var source = extractor.GetStreamIdType(typeof(App.Contracts.ShipmentBooked));
      var wrote = extractor.SetStreamId(message, new Guid("{{_v7}}"), source);
      return $"set={wrote};own={message.InvoiceId.ToGuid()}";
      """;

    var observed = _runProbe("ListedPropertyDiscoveredId", IDS + contracts, probe);

    await Assert.That(observed).IsEqualTo(_written(_v7));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AStrongIdFromAReferencedAssembly_IsDetectedByItsInterfaceAsync() {
    // A strong id compiled into another assembly carries no [WhizbangId] the consumer can rely on, only IWhizbangId and
    // the From factory the strong-id generator emitted there
    var idsImage = _compileIds("ReferencedIds", """
      using Whizbang.Core;
      namespace Shared.Ids {
        [WhizbangId]
        public readonly partial struct LedgerId;
      }
      """);
    const string contracts = """
      using System;
      using Whizbang.Core;
      namespace App.Contracts {
        public class LedgerOpened : IEvent { [StreamId] public Shared.Ids.LedgerId LedgerId { get; set; } }
      }
      namespace App {
        public static class Probe {
          public static string Run() {
            IStreamIdExtractor extractor = new global::ReferencingIds.Generated.GeneratedStreamIdExtractor();
            var message = new App.Contracts.LedgerOpened();
            var wrote = extractor.SetStreamId(message, new Guid("__V7__"), null);
            return $"{extractor.GetStreamIdType(typeof(App.Contracts.LedgerOpened))?.Name};{wrote};{message.LedgerId.ToGuid()}";
          }
        }
      }
      """;
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(
      new StreamIdGenerator(), contracts.Replace("__V7__", _v7.ToString()), "ReferencingIds",
      [MetadataReference.CreateFromImage(idsImage)]);
    await using var image = new MemoryStream();
    var emitted = output.Emit(image);
    await Assert.That(emitted.Success).IsTrue()
      .Because(string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

    var context = new System.Runtime.Loader.AssemblyLoadContext("ReferencedIds", isCollectible: true);
    try {
      context.LoadFromStream(new MemoryStream(idsImage));
      var assembly = context.LoadFromStream(new MemoryStream(image.ToArray()));
      var observed = (string)assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null)!;

      await Assert.That(observed).IsEqualTo($"LedgerId;True;{_v7}");
    } finally {
      context.Unload();
    }
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AStrongIdWithNoFromFactory_IsReportedButNeverWrittenAsync() {
    // A hand-written IWhizbangId has no From the setter could call, so the setter has no case for it rather than code
    // that does not compile
    const string contracts = """
      namespace App.Contracts {
        public readonly struct HandId : IWhizbangId {
          public Guid ToGuid() => Guid.Empty;
          public bool IsTimeOrdered => false;
          public bool SubMillisecondPrecision => false;
          public DateTimeOffset Timestamp => DateTimeOffset.MinValue;
          public bool Equals(IWhizbangId? other) => false;
          public int CompareTo(IWhizbangId? other) => 0;
        }

        public class HandMade : IEvent { [StreamId] public HandId Id { get; set; } }
      }
      """;
    var probe = $$"""
      var wrote = extractor.SetStreamId(new App.Contracts.HandMade(), new Guid("{{_v7}}"), null);
      return $"{extractor.GetStreamIdType(typeof(App.Contracts.HandMade))?.Name};{wrote}";
      """;

    var observed = _runProbe("HandWrittenId", contracts, probe);

    await Assert.That(observed).IsEqualTo("HandId;False");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task GenerateStreamIdOnAnInitOnlyStrongId_ReportsWhiz013Async() {
    const string source = """
      using System;
      using Whizbang.Core;

      namespace App.Contracts;

      [WhizbangId]
      public readonly partial struct ThingId;

      [GenerateStreamId]
      public class CreateThing : ICommand { [StreamId] public ThingId ThingId { get; init; } }

      [GenerateStreamId]
      public class ThingCreated : IEvent { [StreamId] public ThingId ThingId { get; init; } }

      [GenerateStreamId]
      public class ThingNoted : IEvent { [StreamId] public Guid ThingId { get; init; } }

      [GenerateStreamId]
      public class ThingLabelled : IEvent { [StreamId] public string Label { get; init; } = ""; }
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);
    var reported = result.Diagnostics.Where(d => d.Id == "WHIZ013")
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Split('\'')[1]).Order().ToList();

    await Assert.That(string.Join(",", reported)).IsEqualTo("CreateThing,ThingCreated,ThingNoted")
      .Because("the generated setter cannot write an init-only strong id or Guid, so the minted id would be lost; a string stream id is never written");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CommandsOfEveryStreamIdKind_AreSetAndReportedByTheirOwnKindAsync() {
    // A strong id known only by IWhizbangId is written when it declares a static From(Guid); the From overloads that
    // are not that one (an instance method, another arity, another parameter type) do not count, and come first so
    // each is looked at
    const string contracts = """
      namespace App.Contracts {
        public readonly struct LedgerKey : IWhizbangId {
          private readonly Guid _value;
          private LedgerKey(Guid value) => _value = value;
          public static LedgerKey From(int seed) => default;
          public static LedgerKey From(Guid value, Guid other) => default;
          public LedgerKey From(string text) => this;
          public static LedgerKey From(Guid value) => new(value);
          public Guid ToGuid() => _value;
          public bool IsTimeOrdered => false;
          public bool SubMillisecondPrecision => false;
          public DateTimeOffset Timestamp => DateTimeOffset.MinValue;
          public bool Equals(IWhizbangId? other) => false;
          public int CompareTo(IWhizbangId? other) => 0;
        }

        public class OpenLedger : ICommand { [StreamId] public LedgerKey Key { get; set; } }
        public class NoteLedger : ICommand { [StreamId] public Guid Key { get; set; } }
        public class SealLedger : ICommand { [StreamId] public Guid Key { get; init; } }
        public class LabelLedger : ICommand { [StreamId] public string Key { get; set; } = ""; }
        public sealed class LedgerBundle : Whizbang.Core.Minting.CompositeEventBase;
      }
      """;
    var probe = $$"""
      var open = new App.Contracts.OpenLedger();
      var note = new App.Contracts.NoteLedger();
      var seal = new App.Contracts.SealLedger();
      var label = new App.Contracts.LabelLedger();
      var id = new Guid("{{_v7}}");
      var written = string.Join(",", extractor.SetStreamId(open, id, null), extractor.SetStreamId(note, id, typeof(App.Contracts.LedgerKey)),
        extractor.SetStreamId(seal, id, null), extractor.SetStreamId(label, id, null));
      string Name(Type t) => extractor.GetStreamIdType(t)?.Name ?? "null";
      var types = string.Join(",", Name(typeof(App.Contracts.OpenLedger)), Name(typeof(App.Contracts.NoteLedger)),
        Name(typeof(App.Contracts.LedgerBundle)));
      return $"{written};{open.Key.ToGuid() == id};{note.Key == id};{types}";
      """;

    var observed = _runProbe("EveryKind", contracts, probe);

    await Assert.That(observed).IsEqualTo("True,True,False,False;True;True;LedgerKey,null,null")
      .Because("a strong id with a From(Guid) and a settable Guid are written; an init-only Guid and a string are not");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task StrongIdsDiscoveredFromTheirDeclarations_AreNamedAsTheStrongIdGeneratorNamesThemAsync() {
    // Each id is emitted by the strong-id generator from the [WhizbangId] on a property or record parameter, in the
    // namespace that attribute names, or the declaring type's namespace when it names none or a blank one
    const string contracts = """
      public class GlobalBooked : IEvent { [StreamId] [WhizbangId] public GlobalKey Key { get; set; } }

      namespace App.Contracts {
        using App.Named;

        public readonly partial struct DockId;

        public class NamedBooked : IEvent { [StreamId] [WhizbangId(Namespace = "App.Named")] public NamedKey Key { get; set; } }
        public class BlankBooked : IEvent { [StreamId] [WhizbangId("")] public BlankKey Key { get; set; } }
        public class DockBooked : IEvent { [StreamId] [WhizbangId] public DockId Dock { get; set; } }
        public record ParcelShipped([StreamId] [WhizbangId] ParcelKey Key) : IEvent;
      }
      """;
    var probe = $$"""
      string Name(Type t) => extractor.GetStreamIdType(t)?.FullName ?? "null";
      var dock = new App.Contracts.DockBooked();
      var wrote = extractor.SetStreamId(dock, new Guid("{{_v7}}"), null);
      return string.Join(",", Name(typeof(GlobalBooked)), Name(typeof(App.Contracts.NamedBooked)),
        Name(typeof(App.Contracts.BlankBooked)), Name(typeof(App.Contracts.DockBooked)),
        Name(typeof(App.Contracts.ParcelShipped)), wrote && dock.Dock.ToGuid() == new Guid("{{_v7}}"));
      """;

    var observed = _runProbe("DiscoveredIds", contracts, probe);

    await Assert.That(observed).IsEqualTo(
      "GlobalKey,App.Named.NamedKey,App.Contracts.BlankKey,App.Contracts.DockId,App.Contracts.ParcelKey,True");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnAssemblyWithNoStrongIdStreamIds_GetsNoStreamIdTypeCasesAsync() {
    const string source = """
      using System;
      using Whizbang.Core;

      namespace App.Contracts;

      public class OrderNoted : IEvent { [StreamId] public Guid OrderId { get; set; } }
      """;

    var result = GeneratorTestHelper.RunGenerator<StreamIdGenerator>(source);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, EXTRACTORS);

    await Assert.That(code).DoesNotContain("IsAssignableFrom")
      .Because("with no strong-id stream id in the assembly every type answers null, which is the fallback");
  }

  /// <summary>Compiles a strong-id assembly with the strong-id generator and returns its image.</summary>
  [RequiresAssemblyFiles]
  private static byte[] _compileIds(string assemblyName, string source) {
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source, assemblyName);
    using var image = new MemoryStream();
    var emitted = output.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException(
        "The ids do not compile: "
        + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    return image.ToArray();
  }
}
