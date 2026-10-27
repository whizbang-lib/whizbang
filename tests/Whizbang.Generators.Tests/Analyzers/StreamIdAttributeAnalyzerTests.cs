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
}
