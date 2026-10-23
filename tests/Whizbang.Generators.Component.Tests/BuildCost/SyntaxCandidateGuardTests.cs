// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.BuildCost.Benchmarks;

namespace Whizbang.Generators.Tests.BuildCost;

/// <summary>
/// Every generator's semantic transform must run only on the nodes it cares about. A syntax predicate runs on
/// every node of every changed tree and sends each node it admits to a transform that binds symbols, so a broad
/// predicate costs a share of the whole consumer on every build, however little of Whizbang the consumer uses
/// (#1649: a predicate that admitted every member-access call made one generator 60% of a large build).
/// </summary>
/// <remarks>
/// The predicates are called directly rather than counted through the generators' tracked steps: Roslyn does
/// not record a transform result equal to the default value, so a transform that answers null for a node it
/// declines reads the same whether its predicate admitted that node or not. Each predicate is therefore a named
/// public <c>Is…Candidate(SyntaxNode)</c> method; the scan below makes that a rule, so a new pipeline cannot
/// escape the guard.
/// </remarks>
/// <docs>operations/performance/build-cost</docs>
[Category("Generators")]
[Category("BuildCost")]
public partial class SyntaxCandidateGuardTests {
  private const int CORPUS_FILES = 200;

  /// <summary>
  /// Predicates allowed to admit some ordinary code, each with the most nodes of <see cref="OrdinaryCodeCorpus"/>
  /// it may admit and why it cannot be narrowed without changing output. Keep this list small.
  /// </summary>
  private static readonly Dictionary<string, (int Max, string Reason)> _exemptions = new(StringComparer.Ordinal);

  private static readonly Assembly[] _generatorAssemblies = [
    typeof(ReceptorDiscoveryGenerator).Assembly,
    typeof(Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator).Assembly,
    typeof(Whizbang.Transports.HotChocolate.Generators.GraphQLLensTypeGenerator).Assembly,
    typeof(Whizbang.Transports.FastEndpoints.Generators.RestLensEndpointGenerator).Assembly,
  ];

  [Test]
  public async Task EverySyntaxProviderPredicate_IsANamedCandidateMethodAsync() {
    var predicates = _scanPredicates();
    var unnamed = predicates.Where(p => p.Method is null).Select(p => p.Where).ToList();

    await Assert.That(predicates.Count).IsGreaterThan(50)
        .Because("the scan must find the generators' syntax providers for this check to mean anything");
    await Assert.That(unnamed).IsEmpty()
        .Because("each CreateSyntaxProvider predicate must be a public static Is...Candidate(SyntaxNode) method, " +
                 "passed as a method group or forwarded by `static (node, _) => IsXCandidate(node)`, so the guard " +
                 "can call it: " + string.Join("; ", unnamed));
  }

  [Test]
  public async Task EveryCandidateMethod_IsPublicStaticOnItsGeneratorAsync() {
    var missing = _scanPredicates()
        .Where(p => p.Method is not null && _resolve(p.Generator, p.Method) is null)
        .Select(p => $"{p.Generator}.{p.Method}")
        .Distinct()
        .ToList();

    await Assert.That(missing).IsEmpty()
        .Because("each named predicate must be a public static bool method taking one SyntaxNode, declared on " +
                 "the [Generator] class that uses it: " + string.Join(", ", missing));
  }

  [Test]
  public async Task EveryCandidateMethod_AdmitsNoOrdinaryCodeAsync() {
    var nodes = await _corpusNodesAsync();
    var calls = nodes.OfType<InvocationExpressionSyntax>().Count();
    var methods = _scanPredicates()
        .Where(p => p.Method is not null)
        .Select(p => (Name: $"{p.Generator}.{p.Method}", Method: _resolve(p.Generator, p.Method!)))
        .Where(p => p.Method is not null)
        .DistinctBy(p => p.Name)
        .ToList();

    var offenders = new List<string>();
    foreach (var (name, method) in methods) {
      var admitted = nodes.Count(n => (bool)method!.Invoke(null, [n])!);
      var max = _exemptions.TryGetValue(name, out var exemption) ? exemption.Max : 0;
      if (admitted > max) {
        offenders.Add($"{name} admitted {admitted} (allowed {max})");
      }
    }

    await Assert.That(calls).IsGreaterThan(CORPUS_FILES * 20)
        .Because("the corpus must be full of ordinary calls for this check to mean anything");
    await Assert.That(methods.Count).IsGreaterThan(30);
    await Assert.That(offenders).IsEmpty()
        .Because($"over {CORPUS_FILES} files of code no generator cares about ({nodes.Count} nodes, {calls} calls), " +
                 "these predicates sent nodes to their semantic transform: " + string.Join("; ", offenders));
  }

  [Test]
  public async Task EveryExemption_NamesAPredicateThatExistsAsync() {
    var names = _scanPredicates().Where(p => p.Method is not null).Select(p => $"{p.Generator}.{p.Method}").ToHashSet();
    var stale = _exemptions.Keys.Where(k => !names.Contains(k)).ToList();

    await Assert.That(stale).IsEmpty().Because("an exemption for a predicate that no longer exists hides nothing and misleads");
  }

  private static async Task<List<SyntaxNode>> _corpusNodesAsync() {
    var nodes = new List<SyntaxNode>();
    foreach (var (path, source) in OrdinaryCodeCorpus.Build(CORPUS_FILES)) {
      var root = await CSharpSyntaxTree.ParseText(source, path: path).GetRootAsync();
      nodes.AddRange(root.DescendantNodesAndSelf());
    }
    return nodes;
  }

  private static MethodInfo? _resolve(string generator, string method) {
    var type = _generatorAssemblies.SelectMany(a => a.GetTypes())
        .FirstOrDefault(t => t.Name == generator && t.GetCustomAttribute<GeneratorAttribute>() is not null);
    var candidate = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static, [typeof(SyntaxNode)]);
    return candidate?.ReturnType == typeof(bool) ? candidate : null;
  }

  private sealed record Predicate(string Where, string Generator, string? Method);

  private static List<Predicate> _scanPredicates() {
    var root = _findRepoRoot();
    var found = new List<Predicate>();
    foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "src"), "*Generators*")) {
      foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
        if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)) {
          continue;
        }
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
        foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()) {
          if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateSyntaxProvider" }) {
            continue;
          }
          var argument = call.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "predicate")
              ?? call.ArgumentList.Arguments.FirstOrDefault();
          var generator = call.FirstAncestorOrSelf<ClassDeclarationSyntax>()?.Identifier.ValueText ?? "?";
          var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
          var where = $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}";
          found.Add(new Predicate(where, generator, _candidateName(argument?.Expression)));
        }
      }
    }
    return found;
  }

  // The candidate method a predicate names: a method group, or a lambda that only forwards its node to one.
  private static string? _candidateName(ExpressionSyntax? predicate) {
    var name = predicate switch {
      IdentifierNameSyntax id => id.Identifier.ValueText,
      LambdaExpressionSyntax {
        ExpressionBody: InvocationExpressionSyntax { Expression: IdentifierNameSyntax target, ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax arg }] },
      } lambda when arg.Identifier.ValueText == _firstParameter(lambda) => target.Identifier.ValueText,
      _ => null,
    };
    return name is not null && _candidateMethodName().IsMatch(name) ? name : null;
  }

  private static string? _firstParameter(LambdaExpressionSyntax lambda) => lambda switch {
    SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
    ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.FirstOrDefault()?.Identifier.ValueText,
    _ => null,
  };

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Generators"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the repository root from AppContext.BaseDirectory");
  }

  [GeneratedRegex("^Is[A-Za-z0-9]*Candidate$")]
  private static partial Regex _candidateMethodName();
}
