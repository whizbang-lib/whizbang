// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.TestAnalyzers;

/// <summary>
/// WHIZ605: in a sharded test project, reports a test class that does not carry exactly one <c>Shard*</c>
/// category (or <c>Benchmark</c> and no shard), the class CI would run in no slice or in two.
/// </summary>
/// <remarks>
/// <para>
/// CI runs a sharded project as <c>--treenode-filter "[Category=ShardN]"</c> slices (<c>ai-docs/test-sharding.md</c>).
/// A class with no shard category matches no slice, so its tests stop running while every job stays green; one with
/// two runs on two runners. A <c>Benchmark</c> class runs on the scheduled benchmark job instead and carries no shard.
/// </para>
/// <para>
/// The rule applies only in a project that sets the MSBuild property <c>WhizbangShardedTests</c> to <c>true</c>,
/// which <c>tests/Directory.Build.props</c> passes to the compiler as <c>build_property.WhizbangShardedTests</c>. It
/// mirrors the runtime guard each sharded project keeps (<c>ShardCoverageGuardTests</c>): a test class is a
/// non-abstract class with a TUnit <c>[Test]</c> method, declared or inherited, and its categories are the TUnit
/// <c>[Category]</c> attributes (subclasses included) on it and its base classes, as reflection with
/// <c>inherit: true</c> reads them. Attributes bind by symbol, so a look-alike outside TUnit counts for nothing.
/// </para>
/// <para>
/// A category is read from the attribute's single constructor argument. A <c>CategoryAttribute</c> subclass that
/// passes a fixed value to its base constructor shows no argument, so it counts as no category and the class is
/// reported rather than silently passing. Code from source generators is not analyzed; the runtime guard covers it.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.TestAnalyzers.Tests/ShardCategoryAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ShardCategoryAnalyzer : DiagnosticAnalyzer {
  private const string SHARDED_PROPERTY = "build_property.WhizbangShardedTests";
  private const string TEST_ATTRIBUTE = "TUnit.Core.TestAttribute";
  private const string CATEGORY_ATTRIBUTE = "TUnit.Core.CategoryAttribute";
  private const string SHARD_PREFIX = "Shard";
  private const string BENCHMARK = "Benchmark";

  /// <summary>The class name with its containing classes (<c>Outer.InnerTests</c>), as the test tree shows it.</summary>
  private static readonly SymbolDisplayFormat _classNameFormat = new(
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

  /// <summary>
  /// WHIZ605: Error. A test class in a sharded project carries no Shard category, more than one, or one beside
  /// Benchmark.
  /// </summary>
  /// <docs>operations/diagnostics/whiz605</docs>
  /// <tests>tests/Whizbang.TestAnalyzers.Tests/ShardCategoryAnalyzerTests.cs:TestClass_WithNoShardCategory_ReportsWhiz605Async</tests>
  public static readonly DiagnosticDescriptor ShardCategoryMismatch = new(
    id: "WHIZ605",
    title: "A test class in a sharded project must carry exactly one Shard category",
    messageFormat: "'{0}' must carry exactly one Shard category, or Benchmark and no shard, because CI runs this "
      + "project as [Category=ShardN] slices; it has [{1}]{2}",
    category: "Whizbang.Testing",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "This project sets WhizbangShardedTests, so CI runs it as one slice per Shard category. A test "
      + "class with no Shard category runs in no slice and its tests silently stop running; one with two runs "
      + "twice. Give the class exactly one [Category(\"ShardN\")], placed by measured cost "
      + "(scripts/Measure-TestShards.ps1), or [Category(\"Benchmark\")] alone for a class the benchmark job runs. "
      + "Categories on base classes count.",
    helpLinkUri: "https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/test-sharding.md#the-build-rejects-a-class-without-exactly-one-shard-whiz605");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ShardCategoryMismatch];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(_onCompilationStart);
  }

  private static void _onCompilationStart(CompilationStartAnalysisContext context) {
    context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(SHARDED_PROPERTY, out var sharded);
    if (!string.Equals(sharded, "true", StringComparison.OrdinalIgnoreCase)) {
      return;
    }

    var test = context.Compilation.GetTypeByMetadataName(TEST_ATTRIBUTE);
    if (test is null) {
      return;
    }

    // CategoryAttribute ships in the same TUnit.Core assembly as TestAttribute, so it resolves whenever test does.
    var category = context.Compilation.GetTypeByMetadataName(CATEGORY_ATTRIBUTE);
    context.RegisterSymbolAction(symbol => _analyzeType(symbol, test, category), SymbolKind.NamedType);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "WHIZ606:A type name is read outside the shared naming code", Justification = "A test-code analyzer, which does not reference the generator naming helpers; the type is named only in the diagnostic message it reports (#2267)")]
  private static void _analyzeType(SymbolAnalysisContext context, INamedTypeSymbol test, INamedTypeSymbol? category) {
    var type = (INamedTypeSymbol)context.Symbol;
    if (type.TypeKind != TypeKind.Class || type.IsAbstract || !_hasTests(type, test)) {
      return;
    }

    var categories = _selfAndBases(type)
      .SelectMany(t => t.GetAttributes())
      .Where(attribute => _isCategory(attribute.AttributeClass, category))
      .Select(attribute => attribute.ConstructorArguments is [{ Value: string name }] ? name : null)
      .OfType<string>()
      .ToList();
    var shards = categories.Where(name => name.StartsWith(SHARD_PREFIX, StringComparison.Ordinal)).ToList();
    var isBenchmark = categories.Contains(BENCHMARK);
    if (shards.Count == (isBenchmark ? 0 : 1)) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
      ShardCategoryMismatch,
      type.Locations[0],
      type.ToDisplayString(_classNameFormat),
      string.Join(", ", shards),
      isBenchmark ? " and " + BENCHMARK : ""));
  }

  /// <summary>True when the type or a base class has a method carrying TUnit's <c>[Test]</c>.</summary>
  private static bool _hasTests(INamedTypeSymbol type, INamedTypeSymbol test) =>
    _selfAndBases(type)
      .SelectMany(t => t.GetMembers())
      .OfType<IMethodSymbol>()
      .Any(method => method.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, test)));

  /// <summary>True for TUnit's <c>CategoryAttribute</c> or a subclass of it.</summary>
  private static bool _isCategory(INamedTypeSymbol? attributeClass, INamedTypeSymbol? category) {
    for (var current = attributeClass; current is not null; current = current.BaseType) {
      if (SymbolEqualityComparer.Default.Equals(current, category)) {
        return true;
      }
    }
    return false;
  }

  private static IEnumerable<INamedTypeSymbol> _selfAndBases(INamedTypeSymbol type) {
    for (var current = type; current is not null; current = current.BaseType) {
      yield return current;
    }
  }
}
