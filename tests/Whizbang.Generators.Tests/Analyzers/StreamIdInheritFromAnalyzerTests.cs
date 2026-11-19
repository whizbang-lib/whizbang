// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="StreamIdInheritFromAnalyzer"/> (#2072): <c>[StreamId(InheritFrom = ...)]</c> names the strong-id
/// types whose stream a cascaded message may inherit. Listing a type that is not a strong id is WHIZ018, the option on
/// a stream id that is not a strong id is WHIZ019, listing the stream id's own type is WHIZ034, and a strong-id
/// <c>[StreamId]</c> on a message that also implements <c>IHasStreamId</c>, which the dispatcher writes instead, is
/// WHIZ035.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/StreamIdInheritFromAnalyzer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Utilities/StrongIdStreamIds.cs</code-under-test>
public class StreamIdInheritFromAnalyzerTests {
  private const string IDS = """
    using System;
    using Whizbang.Core;
    namespace App;
    [WhizbangId] public readonly partial struct OrderId;
    [WhizbangId] public readonly partial struct InvoiceId;
    """;

  private static string _message(CultureInfo culture, Diagnostic diagnostic) => diagnostic.GetMessage(culture);

  [Test]
  public async Task ListedStrongIds_AreNotReportedAsync() {
    const string source = IDS + """
      public class CreateInvoice : ICommand {
        [StreamId(InheritFrom = [typeof(OrderId)])] public InvoiceId InvoiceId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task AListedTypeThatIsNotAStrongId_ReportsWhiz018Async() {
    const string source = IDS + """
      public class CreateInvoice : ICommand {
        [StreamId(InheritFrom = [typeof(OrderId), typeof(Guid)])] public InvoiceId InvoiceId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    var whiz018 = diagnostics.Single();
    await Assert.That(whiz018.Id).IsEqualTo("WHIZ018");
    await Assert.That(whiz018.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(_message(CultureInfo.InvariantCulture, whiz018)).Contains("'Guid'");
    await Assert.That(_message(CultureInfo.InvariantCulture, whiz018)).Contains("CreateInvoice.InvoiceId");
    await Assert.That(whiz018.Location.SourceSpan.Start).IsEqualTo(source.IndexOf("StreamId(InheritFrom", StringComparison.Ordinal))
      .Because("the error sits on the [StreamId] that lists the type");
  }

  [Test]
  public async Task InheritFromOnAStreamIdThatIsNotAStrongId_ReportsWhiz019Async() {
    const string source = IDS + """
      public class InvoiceNoted : IEvent {
        [StreamId(InheritFrom = [typeof(OrderId)])] public Guid InvoiceId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    var whiz019 = diagnostics.Single();
    await Assert.That(whiz019.Id).IsEqualTo("WHIZ019");
    await Assert.That(whiz019.Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(_message(CultureInfo.InvariantCulture, whiz019)).Contains("InvoiceNoted.InvoiceId");
  }

  [Test]
  public async Task AnEmptyInheritFromOnAGuidStreamId_IsNotReportedAsync() {
    // Only a list that names something says the stream id takes another id type's stream
    const string source = IDS + """
      public class InvoiceNoted : IEvent {
        [StreamId(InheritFrom = [])] public Guid InvoiceId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task ListingTheStreamIdsOwnType_ReportsWhiz034Async() {
    const string source = IDS + """
      public class CreateInvoice : ICommand {
        [StreamId(InheritFrom = [typeof(InvoiceId)])] public InvoiceId InvoiceId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    var whiz034 = diagnostics.Single();
    await Assert.That(whiz034.Id).IsEqualTo("WHIZ034");
    await Assert.That(whiz034.Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(_message(CultureInfo.InvariantCulture, whiz034)).Contains("'InvoiceId'");
  }

  [Test]
  public async Task InheritFromOnARecordParameter_IsCheckedAsync() {
    const string source = IDS + """
      public record CreateInvoice([property: StreamId(InheritFrom = [typeof(string)])] InvoiceId InvoiceId) : ICommand;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    await Assert.That(diagnostics.Single().Id).IsEqualTo("WHIZ018");
  }

  [Test]
  public async Task AStreamIdWhoseTypeIsDiscoveredFromTheProperty_IsAStrongIdAsync() {
    // [WhizbangId] on the property makes ShipmentKey a strong id the strong-id generator emits
    const string source = IDS + """
      public class ShipmentBooked : IEvent {
        [StreamId(InheritFrom = [typeof(OrderId)])] [WhizbangId] public ShipmentKey Key { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(_withIdsGenerated(source));

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task AStrongIdFromAReferencedAssembly_IsAStrongIdThroughItsInterfaceAsync() {
    const string source = IDS + """
      public readonly struct HandId : IWhizbangId {
        public Guid ToGuid() => Guid.Empty;
        public bool IsTimeOrdered => false;
        public bool SubMillisecondPrecision => false;
        public DateTimeOffset Timestamp => DateTimeOffset.MinValue;
        public bool Equals(IWhizbangId? other) => false;
        public int CompareTo(IWhizbangId? other) => 0;
      }
      public class CreateInvoice : ICommand {
        [StreamId(InheritFrom = [typeof(HandId)])] public InvoiceId InvoiceId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task IHasStreamIdWithAStrongIdStreamId_ReportsWhiz035Async() {
    const string source = IDS + """
      public class CreateInvoice : ICommand, IHasStreamId {
        [StreamId] public InvoiceId InvoiceId { get; set; }
        public Guid StreamId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    var whiz035 = diagnostics.Single();
    await Assert.That(whiz035.Id).IsEqualTo("WHIZ035");
    await Assert.That(whiz035.Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(_message(CultureInfo.InvariantCulture, whiz035)).Contains("CreateInvoice");
    await Assert.That(whiz035.Location.SourceSpan.Start).IsEqualTo(source.IndexOf("StreamId] public InvoiceId", StringComparison.Ordinal))
      .Because("the warning sits on the strong-id [StreamId] the dispatcher does not write");
  }

  [Test]
  public async Task IHasStreamIdOnADerivedTypeOverAStrongIdBase_ReportsWhiz035OnTheTypeAsync() {
    const string source = IDS + """
      public abstract class InvoiceCommand : ICommand { [StreamId] public InvoiceId InvoiceId { get; set; } }
      public class CreateInvoice : InvoiceCommand, IHasStreamId { public Guid StreamId { get; set; } }
      public class VoidInvoice : CreateInvoice;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    var whiz035 = diagnostics.Single();
    await Assert.That(whiz035.Id).IsEqualTo("WHIZ035");
    await Assert.That(_message(CultureInfo.InvariantCulture, whiz035)).Contains("CreateInvoice");
    await Assert.That(whiz035.Location.SourceSpan.Start).IsEqualTo(source.IndexOf("CreateInvoice :", StringComparison.Ordinal))
      .Because("the type that brings the two together is reported once, not every type below it");
  }

  [Test]
  public async Task IHasStreamIdWithAGuidStreamId_IsNotReportedAsync() {
    const string source = IDS + """
      public class CreateInvoice : ICommand, IHasStreamId {
        [StreamId] public Guid StreamId { get; set; }
      }
      public class NoStream : ICommand, IHasStreamId {
        public Guid StreamId { get; set; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdInheritFromAnalyzer>(source);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  public async Task TheAnalyzer_DeclaresItsDiagnosticsAtTheirSeveritiesAsync() {
    var supported = new StreamIdInheritFromAnalyzer().SupportedDiagnostics
      .ToDictionary(d => d.Id, d => d.DefaultSeverity);

    await Assert.That(supported).IsEquivalentTo(new Dictionary<string, DiagnosticSeverity> {
      ["WHIZ018"] = DiagnosticSeverity.Error,
      ["WHIZ019"] = DiagnosticSeverity.Error,
      ["WHIZ034"] = DiagnosticSeverity.Warning,
      ["WHIZ035"] = DiagnosticSeverity.Warning,
    });
  }

  /// <summary>The compilation after the strong-id generator has run, as the analyzer sees it in a build.</summary>
  [RequiresAssemblyFiles]
  private static Compilation _withIdsGenerated(string source) =>
    GeneratorTestHelper.RunGeneratorsWithFullReferences([new WhizbangIdGenerator()], source).Output;
}
