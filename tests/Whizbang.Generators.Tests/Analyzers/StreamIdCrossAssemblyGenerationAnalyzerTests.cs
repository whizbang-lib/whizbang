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
/// WHIZ017 is retired (#1905). It warned that a derived type's opt-out from its base's <c>[GenerateStreamId]</c> (#1893)
/// could not hold when the base was compiled in another assembly, because that assembly's extractor answered first. The
/// per-type table resolves a message by its exact type, and the derived type's own assembly registers its entry with
/// the opt-out applied, so the opt-out holds wherever the base is compiled and there is nothing left to warn about.
/// <c>StreamIdTypeTableGeneratorTests</c> pins the behavior itself.
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
  public async Task OwnStreamId_UnderAGeneratingBaseInAnotherAssembly_IsNoLongerReportedAsync() {
    const string derived = """
      using Whizbang.Core;
      namespace App;
      public record LineAdded : Contracts.OrderEventBase { [StreamId] public System.Guid LineId { get; set; } }
      """;

    var diagnostics = await _crossAssemblyDiagnosticsAsync(GENERATING_BASE, derived);

    await Assert.That(diagnostics.Any(d => d.Id == WHIZ017)).IsFalse()
      .Because("the derived type's own table entry carries its opt-out, so it holds across assemblies");
  }

  [Test]
  public async Task Whiz017_IsRetiredAndNeverReusedAsync() {
    var supported = new StreamIdAttributeAnalyzer().SupportedDiagnostics.Select(d => d.Id);

    await Assert.That(supported).DoesNotContain(WHIZ017);
    await Assert.That(DiagnosticDescriptors.RETIRED_IDS).Contains(WHIZ017);
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
