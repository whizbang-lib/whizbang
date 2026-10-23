// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Whizbang.BuildCost.Benchmarks;

namespace Whizbang.Generators.Tests.BuildCost;

/// <summary>
/// An edit that changes nothing a generator reads must not regenerate anything. A generator that regenerates on
/// every edit costs the consumer on every keystroke in the IDE and on every incremental build, and makes every
/// project that depends on the output recompile. Runs every generator in the package together, as a consumer's
/// build does, over <see cref="ConsumerCorpus"/>, then applies one edit and checks each generator's outputs.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("Generators")]
[Category("BuildCost")]
public class GeneratorCachingTests {
  private const int FEATURES = 5;

  /// <summary>The edits no generator should react to: (name, path, new source).</summary>
  public static IEnumerable<Func<(string Name, string Path, string Source)>> UnrelatedEdits() {
    yield return () => ("an ordinary method body", ConsumerCorpus.UNRELATED_FILE, ConsumerCorpus.Unrelated(2));
    yield return () => ("a comment", ConsumerCorpus.UNRELATED_FILE, ConsumerCorpus.Unrelated(1) + "\n// a comment\n");
    yield return () => ("a receptor's method body", ConsumerCorpus.FIRST_RECEPTOR_FILE, ConsumerCorpus.FirstReceptor("\"made\""));
  }

  [Test]
  [MethodDataSource(nameof(UnrelatedEdits))]
  [RequiresAssemblyFiles]
  public async Task UnrelatedEdit_RegeneratesNothingAsync(string name, string path, string source) {
    var files = ConsumerCorpus.Build(FEATURES);
    var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, parse, f.Path)).ToArray();
    var compilation = CSharpCompilation.Create("Consumer", trees, BuildCostReferences.All(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    GeneratorDriver driver = CSharpGeneratorDriver.Create(
        BuildCostReferences.Generators(),
        parseOptions: parse,
        driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
    driver = driver.RunGenerators(compilation);

    var old = trees.Single(t => t.FilePath == path);
    var edited = compilation.ReplaceSyntaxTree(old, CSharpSyntaxTree.ParseText(source, parse, path));
    driver = driver.RunGenerators(edited);

    var regenerated = driver.GetRunResult().Results
        .SelectMany(r => r.TrackedOutputSteps.SelectMany(s => s.Value)
            .SelectMany(step => step.Outputs)
            .Where(o => o.Reason is IncrementalStepRunReason.New or IncrementalStepRunReason.Modified)
            .Select(_ => r.Generator.GetGeneratorType().Name))
        .Distinct()
        .Order()
        .ToArray();

    await Assert.That(regenerated).IsEmpty()
        .Because($"after an edit to {name}, these generators regenerated output: {string.Join(", ", regenerated)}");
  }
}

/// <summary>References and generators for the build-cost tests: everything a consumer of the core package has.</summary>
internal static class BuildCostReferences {
  [RequiresAssemblyFiles]
  public static ImmutableArray<MetadataReference> All() {
    var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
    return [
      .. platform.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
      MetadataReference.CreateFromFile(typeof(Whizbang.Core.IEvent).Assembly.Location),
    ];
  }

  /// <summary>Every generator the core generators package ships, as a consumer's build loads them.</summary>
  public static ISourceGenerator[] Generators() => [
    .. typeof(ReceptorDiscoveryGenerator).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(t)
            && t.GetCustomAttribute<GeneratorAttribute>() is not null)
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .Select(t => ((IIncrementalGenerator)Activator.CreateInstance(t)!).AsSourceGenerator()),
  ];
}
