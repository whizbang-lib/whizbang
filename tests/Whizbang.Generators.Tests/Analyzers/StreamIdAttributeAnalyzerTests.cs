// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="StreamIdAttributeAnalyzer"/> (#1427): a second <c>[StreamId]</c>, which the generator
/// ignores, is a warning (WHIZ006) on that attribute. Any property type may be a stream id.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/StreamIdAttributeAnalyzer.cs</code-under-test>
public class StreamIdAttributeAnalyzerTests {
  [Test]
  [Arguments("System.Guid")]
  [Arguments("string")]
  [Arguments("OrderId")]
  public async Task ASingleStreamIdOfAnyType_IsNotReportedAsync(string type) {
    var source = $$"""
      using Whizbang.Core;
      namespace App;
      public readonly record struct OrderId(System.Guid Value);
      public record OrderPlaced : IEvent { [StreamId] public {{type}} Id { get; init; } = default!; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(0);
  }

  [Test]
  public async Task TwoStreamIds_ReportWhiz006OnTheSecondNamingTheOneUsedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public record OrderPlaced : IEvent {
        [StreamId] public System.Guid OrderId { get; init; }
        [StreamId] public System.Guid CustomerId { get; init; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    await Assert.That(whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'OrderId' will be used");
    await Assert.That(whiz006.Location.SourceSpan.Start).IsGreaterThan(source.IndexOf("CustomerId", StringComparison.Ordinal) - 30)
      .Because("the warning sits on the second attribute, the one that is ignored");
  }

  [Test]
  public async Task AStreamIdOnBothAParameterAndItsProperty_CountsOnceAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public record OrderPlaced([property: StreamId] System.Guid Id) : IEvent;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(0);
  }

  [Test]
  public async Task ADerivedStreamIdOverAnInheritedOne_ReportsWhiz006NamingTheDerivedOneAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; init; } }
      public record LineAdded : OrderEventBase { [StreamId] public System.Guid LineId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    var message = whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Type LineAdded");
    await Assert.That(message).Contains("'LineId' will be used")
      .Because("the generator searches the most derived type first, so the inherited OrderId is ignored");
    await Assert.That(whiz006.Location.SourceSpan.Start).IsGreaterThan(source.IndexOf("LineId", StringComparison.Ordinal) - 30)
      .Because("the warning sits on the derived attribute, the declaration that shadows the inherited one");
  }

  [Test]
  public async Task ADerivedParameterStreamIdUnderAnInheritedProperty_ReportsWhiz006NamingTheInheritedOneAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; init; } }
      public record LineAdded([StreamId] System.Guid LineId) : OrderEventBase;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    await Assert.That(whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'OrderId' will be used")
      .Because("a [StreamId] property anywhere in the base chain is taken before a constructor parameter");
    await Assert.That(whiz006.Location.SourceSpan.Start).IsGreaterThan(source.IndexOf("LineId", StringComparison.Ordinal) - 30);
  }

  [Test]
  public async Task AStreamIdInheritedTwoLevelsUp_IsSeenAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record EventRoot : IEvent { [StreamId] public System.Guid TenantId { get; init; } }
      public abstract record OrderEventBase : EventRoot;
      public record LineAdded : OrderEventBase { [StreamId] public System.Guid LineId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(1);
  }

  [Test]
  public async Task AnOverrideThatRedeclaresTheInheritedStreamId_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract class OrderEventBase : IEvent { [StreamId] public virtual System.Guid OrderId { get; init; } }
      public class LineAdded : OrderEventBase { [StreamId] public override System.Guid OrderId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(0)
      .Because("the same member redeclared is one stream id, not two");
  }

  [Test]
  public async Task AnInheritedStreamIdAlone_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; init; } }
      public record LineAdded : OrderEventBase { public System.Guid LineId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(0);
  }

  [Test]
  public async Task TwoParameterStreamIds_ReportWhiz006NamingTheFirstAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public record OrderPlaced([StreamId] System.Guid OrderId, [StreamId] System.Guid CustomerId) : IEvent;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    await Assert.That(whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'OrderId' will be used");
  }

  [Test]
  public async Task TwoStreamIdsOnAnInterface_ReportWhiz006Async() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public interface IOrderEvent {
        [StreamId] System.Guid OrderId { get; }
        [StreamId] System.Guid CustomerId { get; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(1)
      .Because("An interface has no base type to search, and its own two still count.");
  }
  // #1833: [StreamId(Override = true)] says a derived type's stream id hides the inherited one on purpose.

  [Test]
  public async Task ADerivedOverridePropertyOverAnInheritedOne_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract class CommandBase : ICommand { [StreamId] public virtual System.Guid StreamId { get; set; } }
      public class AcknowledgeAlert : CommandBase { [StreamId(Override = true)] public System.Guid AckId { get; set; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ006" or "WHIZ016")).IsEqualTo(0)
      .Because("an override hides the inherited stream id on purpose, and there is one to hide");
  }

  [Test]
  public async Task ADerivedOverrideParameterOverAnInheritedProperty_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; init; } }
      public record LineAdded([StreamId(Override = true)] System.Guid LineId) : OrderEventBase;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ006" or "WHIZ016")).IsEqualTo(0)
      .Because("an override parameter is the stream id, ahead of the inherited property");
  }

  [Test]
  public async Task AnOverrideTwoLevelsBelowTheInheritedStreamId_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record EventRoot : IEvent { [StreamId] public System.Guid TenantId { get; init; } }
      public abstract record OrderEventBase : EventRoot;
      public record LineAdded : OrderEventBase { [StreamId(Override = true)] public System.Guid LineId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ006" or "WHIZ016")).IsEqualTo(0);
  }

  [Test]
  public async Task AnOverrideThatRedeclaresTheInheritedProperty_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract class OrderEventBase : IEvent { [StreamId] public virtual System.Guid OrderId { get; init; } }
      public class LineAdded : OrderEventBase { [StreamId(Override = true)] public override System.Guid OrderId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ006" or "WHIZ016")).IsEqualTo(0);
  }

  [Test]
  public async Task AnOverrideOverABaseParameterStreamId_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase([StreamId] System.Guid OrderId) : IEvent;
      public record LineAdded([StreamId(Override = true)] System.Guid LineId) : OrderEventBase(LineId);
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ006" or "WHIZ016")).IsEqualTo(0)
      .Because("a base record's [StreamId] parameter is a stream id the override replaces");
  }

  [Test]
  public async Task AnExplicitOverrideFalse_IsAPlainStreamIdAndStillReportsWhiz006Async() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; init; } }
      public record LineAdded : OrderEventBase { [StreamId(Override = false)] public System.Guid LineId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(1);
    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ016")).IsEqualTo(0);
  }

  [Test]
  public async Task APlainStreamIdNextToAnOverrideOnOneType_ReportsWhiz006OnThePlainOneNamingTheOverrideAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; init; } }
      public record LineAdded : OrderEventBase {
        [StreamId] public System.Guid CustomerId { get; init; }
        [StreamId(Override = true)] public System.Guid LineId { get; init; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    await Assert.That(whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'LineId' will be used")
      .Because("an override is the stream id even when a plain one is declared before it");
    await Assert.That(whiz006.Location.SourceSpan.Start).IsLessThan(source.IndexOf("CustomerId", StringComparison.Ordinal))
      .Because("the warning sits on the plain attribute, the one that is ignored");
  }

  [Test]
  public async Task TwoOverridesOnOneType_ReportWhiz006OnTheSecondAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; init; } }
      public record LineAdded : OrderEventBase {
        [StreamId(Override = true)] public System.Guid LineId { get; init; }
        [StreamId(Override = true)] public System.Guid CustomerId { get; init; }
      }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    await Assert.That(whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("'LineId' will be used");
    await Assert.That(whiz006.Location.SourceSpan.Start).IsGreaterThan(source.IndexOf("LineId", StringComparison.Ordinal));
  }

  [Test]
  public async Task AnOverrideOnAnIntermediateRecord_IsUsedAheadOfALeafParameterAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record EventRoot : IEvent { [StreamId] public System.Guid TenantId { get; init; } }
      public abstract record OrderEventBase([StreamId(Override = true)] System.Guid OrderId) : EventRoot;
      public record LineAdded([StreamId] System.Guid LineId) : OrderEventBase(LineId);
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    var message = whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Type LineAdded");
    await Assert.That(message).Contains("'OrderId' will be used")
      .Because("the intermediate record's override is the inherited stream id, ahead of the leaf's plain parameter");
  }

  [Test]
  public async Task APlainStreamIdHidingAnInheritedOverride_ReportsWhiz006Async() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record EventRoot : IEvent { [StreamId] public System.Guid TenantId { get; init; } }
      public abstract record OrderEventBase : EventRoot { [StreamId(Override = true)] public System.Guid OrderId { get; init; } }
      public record LineAdded : OrderEventBase { [StreamId] public System.Guid LineId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz006 = diagnostics.Single(d => d.Id == "WHIZ006");
    var message = whiz006.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Type LineAdded");
    await Assert.That(message).Contains("'LineId' will be used")
      .Because("a hide without the option is still reported, even when what it hides is an override");
  }

  [Test]
  public async Task AnOverrideWithNothingToOverride_ReportsWhiz016OnTheAttributeAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public record OrderPlaced : IEvent { [StreamId(Override = true)] public System.Guid OrderId { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz016 = diagnostics.Single(d => d.Id == "WHIZ016");
    var message = whiz016.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(whiz016.Severity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    await Assert.That(message).Contains("OrderPlaced");
    await Assert.That(message).Contains("'OrderId'");
    await Assert.That(whiz016.Location.SourceSpan.Start).IsLessThan(source.IndexOf("OrderId", StringComparison.Ordinal))
      .Because("the warning sits on the override attribute");
    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ006")).IsEqualTo(0);
  }

  [Test]
  public async Task AnOverrideParameterWithNothingToOverride_ReportsWhiz016Async() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public abstract record EventRoot : IEvent { public System.Guid TenantId { get; init; } }
      public record OrderPlaced([StreamId(Override = true)] System.Guid OrderId) : EventRoot;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ016")).IsEqualTo(1)
      .Because("a base type without a [StreamId] gives the override nothing to override");
  }

  [Test]
  public async Task TheAnalyzer_DeclaresWhiz016AsAWarningAsync() {
    var descriptor = new StreamIdAttributeAnalyzer().SupportedDiagnostics.Single(d => d.Id == "WHIZ016");

    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }
}
