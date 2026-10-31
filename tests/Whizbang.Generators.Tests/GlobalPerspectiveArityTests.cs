// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <c>IGlobalPerspectiveFor</c> takes as many events as the single-stream families, fifty (#1936), so a global
/// perspective never meets a smaller cap than the others.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/IGlobalPerspectiveFor.cs</code-under-test>
[Category("SourceGenerators")]
public class GlobalPerspectiveArityTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task TheGlobalFamily_TakesUpToFiftyEvents_AndNoMoreAsync() {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("namespace Empty;");

    // The metadata arity counts TModel and TPartitionKey as well as the events.
    for (var events = 1; events <= 50; events++) {
      await Assert.That(compilation.GetTypeByMetadataName($"Whizbang.Core.Perspectives.IGlobalPerspectiveFor`{events + 2}")).IsNotNull()
        .Because($"IGlobalPerspectiveFor declares a variant for {events} events");
    }
    await Assert.That(compilation.GetTypeByMetadataName("Whizbang.Core.Perspectives.IGlobalPerspectiveFor`53")).IsNull()
      .Because("fifty is the cap of every family");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AGlobalPerspectiveWithFiftyEvents_CompilesAndRoutesEveryEventAsync() {
    var events = Enumerable.Range(1, 50).ToArray();
    var eventList = string.Join(", ", events.Select(static i => $"Evt{i}"));
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      {{string.Join("\n", events.Select(static i => $"public record Evt{i} : IEvent {{ [StreamId] public Guid Id {{ get; init; }} }}"))}}

      public record Model {
        [StreamId] public Guid Id { get; init; }
      }

      public class WideGlobal : IGlobalPerspectiveFor<Model, Guid, {{eventList}}> {
      {{string.Join("\n", events.Select(static i => $"  public Guid GetPartitionKey(Evt{i} @event) => @event.Id;"))}}
      {{string.Join("\n", events.Select(static i => $"  public Model Apply(Model current, Evt{i} @event) => current;"))}}
      }
      """;

    var errors = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source).GetDiagnostics()
      .Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).IsEmpty();

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "WideGlobalRunner.g.cs");
    var iface = $"global::Whizbang.Core.Perspectives.IGlobalPerspectiveFor<global::TestNamespace.Model, global::System.Guid, {string.Join(", ", events.Select(static e => $"global::TestNamespace.Evt{e}"))}>";
    foreach (var i in events) {
      await Assert.That(runner).Contains($"case global::TestNamespace.Evt{i} typedEvent:");
    }
    await Assert.That(runner).Contains($"(({iface})perspective).Apply(currentModel!, typedEvent)");
  }
}
