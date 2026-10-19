// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// Tests for <see cref="TopicFilterTargetAnalyzer"/> (#1427): <c>[TopicFilter]</c> on a type that is not a
/// command is ignored, so it is reported (WHIZ025) on the attribute.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/TopicFilterTargetAnalyzer.cs</code-under-test>
public class TopicFilterTargetAnalyzerTests {
  [Test]
  [Arguments("[TopicFilter(\"orders\")]")]
  [Arguments("[TopicFilter<Area>(Area.Orders)]")]
  public async Task ATopicFilterOnAnEvent_ReportsWhiz025Async(string attribute) {
    var source = $$"""
      using Whizbang.Core;
      namespace App;
      public enum Area { Orders }
      {{attribute}}
      public record OrderPlaced : IEvent;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TopicFilterTargetAnalyzer>(source);

    var whiz025 = diagnostics.Single(d => d.Id == "WHIZ025");
    await Assert.That(whiz025.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("OrderPlaced");
  }

  [Test]
  public async Task ATopicFilterOnACommand_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      [TopicFilter("orders")]
      public record PlaceOrder : ICommand;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TopicFilterTargetAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ025")).IsEqualTo(0);
  }

  [Test]
  public async Task ATypeWithNoTopicFilter_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      public record OrderPlaced : IEvent;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TopicFilterTargetAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ025")).IsEqualTo(0);
  }

  [Test]
  public async Task AnEventWithOnlyAnUnrelatedAttribute_IsNotReportedAsync() {
    // The attribute's whole base chain is walked and none of it is a topic filter
    const string source = """
      using Whizbang.Core;
      namespace App;
      [System.Obsolete("retired")]
      public record OrderPlaced : IEvent;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TopicFilterTargetAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ025")).IsEqualTo(0);
  }

  [Test]
  public async Task ATopicFilterAfterAnUnrelatedAttribute_IsReportedOnTheTopicFilterAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      [System.Obsolete("retired")]
      [TopicFilter("orders")]
      public record OrderPlaced : IEvent;
      """;
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<TopicFilterTargetAnalyzer>(source);

    var whiz025 = diagnostics.Single(d => d.Id == "WHIZ025");
    var reportedAt = (await whiz025.Location.SourceTree!.GetTextAsync()).ToString(whiz025.Location.SourceSpan);
    await Assert.That(reportedAt).IsEqualTo("TopicFilter(\"orders\")")
      .Because("the unrelated attribute is passed over and the diagnostic is reported where the topic filter is written");
  }
}
