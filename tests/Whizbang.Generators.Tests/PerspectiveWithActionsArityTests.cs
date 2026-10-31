// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <c>IPerspectiveWithActionsFor</c> takes as many events as <c>IPerspectiveFor</c>, fifty (#1936), so moving an event
/// that purges or deletes from one family to the other never runs into a smaller cap.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/IPerspectiveWithActionsFor.cs</code-under-test>
[Category("SourceGenerators")]
public class PerspectiveWithActionsArityTests {
  private const string NAMESPACE = "Whizbang.Core.Perspectives.";

  [Test]
  [RequiresAssemblyFiles]
  public async Task BothFamilies_TakeUpToFiftyEvents_AndNoMoreAsync() {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("namespace Empty;");

    foreach (var family in new[] { "IPerspectiveFor", "IPerspectiveWithActionsFor" }) {
      // The metadata arity counts TModel as well as the events.
      for (var events = 1; events <= 50; events++) {
        await Assert.That(compilation.GetTypeByMetadataName($"{NAMESPACE}{family}`{events + 1}")).IsNotNull()
          .Because($"{family} declares a variant for {events} events");
      }
      await Assert.That(compilation.GetTypeByMetadataName($"{NAMESPACE}{family}`52")).IsNull()
        .Because("fifty is the cap of both families");
    }
  }

  [Test]
  [Arguments(11)]
  [Arguments(50)]
  [RequiresAssemblyFiles]
  public async Task AWithActionsPerspectiveWithManyEvents_CompilesAndRoutesEveryEventAsync(int count) {
    var events = Enumerable.Range(1, count).ToArray();
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      {{string.Join("\n", events.Select(i => $"public record Evt{i} : IEvent {{ [StreamId] public Guid Id {{ get; init; }} }}"))}}

      public record Model {
        [StreamId] public Guid Id { get; init; }
      }

      public class WidePerspective : IPerspectiveWithActionsFor<Model, {{string.Join(", ", events.Select(i => $"Evt{i}"))}}> {
      {{string.Join("\n", events.Select(i => $"  public ApplyResult<Model> Apply(Model current, Evt{i} @event) => ModelAction.Purge;"))}}
      }
      """;

    var errors = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source).GetDiagnostics()
      .Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).IsEmpty();

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "WidePerspectiveRunner.g.cs");
    await Assert.That(runner).IsNotNull();
    foreach (var i in events) {
      await Assert.That(runner).Contains($"case global::TestNamespace.Evt{i} typedEvent:");
      await Assert.That(runner).Contains($"var result_Evt{i} = perspective.Apply(currentModel!, typedEvent);");
    }
  }
}
