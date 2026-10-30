// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ017 (#1904): a type that declares its own <c>[StreamId]</c> and no <c>[GenerateStreamId]</c> opts out of its
/// base's generation policy (#1893), but only when the base is compiled in the same assembly. When a base in another
/// assembly declares <c>[GenerateStreamId]</c>, that assembly's extractor answers first and the base's policy still
/// applies, so the opt-out cannot hold and the analyzer says so.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Analyzers/StreamIdAttributeAnalyzer.cs</code-under-test>
public class StreamIdCrossAssemblyGenerationAnalyzerTests {
  private const string WHIZ017 = "WHIZ017";

  private const string GENERATING_BASE = """
    using Whizbang.Core;
    namespace Contracts;
    [GenerateStreamId]
    public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; set; } }
    """;

  [Test]
  public async Task OwnStreamId_UnderAGeneratingBaseInAnotherAssembly_ReportsWhiz017Async() {
    const string derived = """
      using Whizbang.Core;
      namespace App;
      public record LineAdded : Contracts.OrderEventBase { [StreamId] public System.Guid LineId { get; set; } }
      """;

    var diagnostics = await _crossAssemblyDiagnosticsAsync(GENERATING_BASE, derived);

    var whiz017 = diagnostics.Single(d => d.Id == WHIZ017);
    var message = whiz017.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("LineAdded");
    await Assert.That(message).Contains("OrderEventBase");
    await Assert.That(message).Contains("will still apply because the base is in another assembly")
      .Because("the derived type's opt-out from the base's generation cannot hold across assemblies");
    await Assert.That(message).Contains("[GenerateStreamId]");
  }

  [Test]
  public async Task AnOverrideStreamId_UnderAGeneratingBaseInAnotherAssembly_ReportsWhiz017Async() {
    const string derived = """
      using Whizbang.Core;
      namespace App;
      public record LineAdded : Contracts.OrderEventBase { [StreamId(Override = true)] public System.Guid LineId { get; set; } }
      """;

    var diagnostics = await _crossAssemblyDiagnosticsAsync(GENERATING_BASE, derived);

    await Assert.That(diagnostics.Count(d => d.Id == WHIZ017)).IsEqualTo(1);
  }

  [Test]
  public async Task OwnStreamId_UnderABaseWhoseStreamIdPropertyGeneratesInAnotherAssembly_ReportsWhiz017Async() {
    const string generatingProperty = """
      using Whizbang.Core;
      namespace Contracts;
      public abstract record OrderEventBase : IEvent { [StreamId] [GenerateStreamId] public System.Guid OrderId { get; set; } }
      """;
    const string derived = """
      using Whizbang.Core;
      namespace App;
      public record LineAdded : Contracts.OrderEventBase { [StreamId(Override = true)] public System.Guid LineId { get; set; } }
      """;

    var diagnostics = await _crossAssemblyDiagnosticsAsync(generatingProperty, derived);

    await Assert.That(diagnostics.Count(d => d.Id == WHIZ017)).IsEqualTo(1);
  }

  [Test]
  public async Task OwnStreamId_UnderAGeneratingBaseInTheSameAssembly_IsNotReportedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace App;
      [GenerateStreamId]
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; set; } }
      public record LineAdded : OrderEventBase { [StreamId(Override = true)] public System.Guid LineId { get; set; } }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<StreamIdAttributeAnalyzer>(source);

    await Assert.That(diagnostics.Count(d => d.Id == WHIZ017)).IsEqualTo(0)
      .Because("in one assembly the generator emits the derived type's opt-out, so it holds");
  }

  [Test]
  public async Task ADerivedTypeThatDeclaresGenerateStreamId_IsNotReportedAsync() {
    const string derived = """
      using Whizbang.Core;
      namespace App;
      [GenerateStreamId]
      public record LineAdded : Contracts.OrderEventBase { [StreamId(Override = true)] public System.Guid LineId { get; set; } }
      """;

    var diagnostics = await _crossAssemblyDiagnosticsAsync(GENERATING_BASE, derived);

    await Assert.That(diagnostics.Count(d => d.Id == WHIZ017)).IsEqualTo(0)
      .Because("a derived type that generates too has no opt-out to lose");
  }

  [Test]
  public async Task ADerivedTypeWithNoStreamIdOfItsOwn_IsNotReportedAsync() {
    const string derived = """
      using Whizbang.Core;
      namespace App;
      public record OrderShipped : Contracts.OrderEventBase { public string Carrier { get; set; } = ""; }
      """;

    var diagnostics = await _crossAssemblyDiagnosticsAsync(GENERATING_BASE, derived);

    await Assert.That(diagnostics.Count(d => d.Id == WHIZ017)).IsEqualTo(0)
      .Because("without a [StreamId] of its own the type inherits the base's generation by design");
  }

  [Test]
  public async Task OwnStreamId_UnderABaseInAnotherAssemblyThatDoesNotGenerate_IsNotReportedAsync() {
    const string plainBase = """
      using Whizbang.Core;
      namespace Contracts;
      public abstract record OrderEventBase : IEvent { [StreamId] public System.Guid OrderId { get; set; } }
      """;
    const string derived = """
      using Whizbang.Core;
      namespace App;
      public record LineAdded : Contracts.OrderEventBase { [StreamId(Override = true)] public System.Guid LineId { get; set; } }
      """;

    var diagnostics = await _crossAssemblyDiagnosticsAsync(plainBase, derived);

    await Assert.That(diagnostics.Count(d => d.Id == WHIZ017)).IsEqualTo(0);
  }

  [Test]
  public async Task TheAnalyzer_DeclaresWhiz017AsAWarningAsync() {
    var descriptor = new StreamIdAttributeAnalyzer().SupportedDiagnostics.Single(d => d.Id == WHIZ017);

    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }

  /// <summary>Compiles <paramref name="baseSource"/> as its own assembly and analyzes <paramref name="derivedSource"/> against it.</summary>
  [RequiresAssemblyFiles]
  private static async Task<ImmutableArray<Diagnostic>> _crossAssemblyDiagnosticsAsync(string baseSource, string derivedSource) {
    var contracts = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(baseSource).WithAssemblyName("Contracts");
    var service = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(derivedSource)
      .WithAssemblyName("Service")
      .AddReferences(contracts.ToMetadataReference());
    return await service.WithAnalyzers([new StreamIdAttributeAnalyzer()]).GetAnalyzerDiagnosticsAsync();
  }
}
