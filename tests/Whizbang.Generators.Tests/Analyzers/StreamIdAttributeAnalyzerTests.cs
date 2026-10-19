// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="StreamIdAttributeAnalyzer"/> (#1427): a non-Guid stream id is an error (WHIZ005), and a
/// second <c>[StreamId]</c>, which the generator ignores, is a warning (WHIZ006), each reported on the attribute.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/StreamIdAttributeAnalyzer.cs</code-under-test>
public class StreamIdAttributeAnalyzerTests {
  [Test]
  [Arguments("System.Guid")]
  [Arguments("System.Guid?")]
  [Arguments("OrderId")]
  public async Task AGuidLikeStreamId_IsNotReportedAsync(string type) {
    var source = $$"""
      using Whizbang.Core;
      namespace App;
      public readonly record struct OrderId(System.Guid Value);
      public record OrderPlaced : IEvent { [StreamId] public {{type}} Id { get; init; } }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id is "WHIZ005" or "WHIZ006")).IsEqualTo(0);
  }

  [Test]
  public async Task ANonGuidStreamId_ReportsWhiz005OnTheAttributeAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public record OrderPlaced : IEvent { [StreamId] public string Id { get; init; } = ""; }
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    var whiz005 = diagnostics.Single(d => d.Id == "WHIZ005");
    await Assert.That(whiz005.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("OrderPlaced.Id");
    var text = await whiz005.Location.SourceTree!.GetTextAsync();
    await Assert.That(text.ToString(whiz005.Location.SourceSpan)).IsEqualTo("StreamId");
  }

  [Test]
  public async Task ANonGuidStreamIdOnARecordParameter_ReportsWhiz005Async() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public record OrderPlaced([StreamId] int Id) : IEvent;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ005")).IsEqualTo(1);
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
}
