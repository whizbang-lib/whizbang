// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.TestAnalyzers;

/// <summary>
/// WHIZ609: reports a class that has a TUnit <c>[After(Test)]</c> hook and is also <see cref="IDisposable"/> or
/// <see cref="IAsyncDisposable"/>, a fixture whose teardown runs twice.
/// </summary>
/// <remarks>
/// <para>
/// TUnit runs a fixture's <c>[After(Test)]</c> hooks and then disposes the instance when it is disposable. A fixture
/// that is both tore down twice: once as the hook, again as the dispose that called it, or the hook that was itself the
/// dispose method (#1954, #2468). Idempotent teardown hid it; a teardown that is not (a second drop of a database
/// another test already reuses the name of, a second dispose of a shared data source) would not. The fix keeps the
/// hook, which TUnit runs whether the test passed or failed, as the one teardown, and drops the interface.
/// </para>
/// <para>
/// The hook and the interface count when declared or inherited. When the base class is in the same project and is
/// itself both, only the base is reported, the one place to fix. A base class from another assembly is not analyzed
/// here, so the class deriving from it is reported instead. Attributes bind by symbol, so a look-alike
/// <c>AfterAttribute</c> outside TUnit counts for nothing. Code from source generators is not analyzed.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.TestAnalyzers.Tests/SingleTeardownAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SingleTeardownAnalyzer : DiagnosticAnalyzer {
  private const string AFTER_ATTRIBUTE = "TUnit.Core.AfterAttribute";
  private const string HOOK_TYPE = "TUnit.Core.HookType";
  private const string TEST_HOOK = "Test";

  /// <summary>
  /// WHIZ609: Error. A fixture with an <c>[After(Test)]</c> hook is also disposable, so its teardown runs twice.
  /// </summary>
  /// <docs>operations/diagnostics/whiz609</docs>
  /// <tests>tests/Whizbang.TestAnalyzers.Tests/SingleTeardownAnalyzerTests.cs:AsyncDisposableFixture_WithATestTeardownHook_ReportsWhiz609Async</tests>
  public static readonly DiagnosticDescriptor TeardownRunsTwice = new(
    id: "WHIZ609",
    title: "A fixture with an [After(Test)] hook must not also be disposable",
    messageFormat: "'{0}' has an [After(Test)] hook and is {1}, so TUnit tears it down twice: it runs the hook and then "
      + "disposes the instance; keep the hook as the one teardown and drop the interface",
    category: "Whizbang.Testing",
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    description: "TUnit runs a test class's [After(Test)] hooks and then disposes the instance when it implements "
      + "IDisposable or IAsyncDisposable, so a class that has both tears down twice. Keep the [After(Test)] hook, which "
      + "runs whether the test passed or failed, as the only teardown: move anything Dispose released into it and "
      + "remove the interface. Hooks and interfaces on base classes count.",
    helpLinkUri: "https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/testing-tunit.md#a-fixture-tears-down-in-one-place-whiz609");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TeardownRunsTwice];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(_onCompilationStart);
  }

  private static void _onCompilationStart(CompilationStartAnalysisContext context) {
    var after = context.Compilation.GetTypeByMetadataName(AFTER_ATTRIBUTE);
    if (after is null) {
      return;
    }

    // HookType ships in the same TUnit.Core assembly as AfterAttribute, so it resolves whenever after does.
    var testHook = context.Compilation.GetTypeByMetadataName(HOOK_TYPE)!
      .GetMembers(TEST_HOOK).OfType<IFieldSymbol>().Single().ConstantValue;
    var disposables = ImmutableArray.Create(
      context.Compilation.GetSpecialType(SpecialType.System_IDisposable),
      context.Compilation.GetTypeByMetadataName("System.IAsyncDisposable")!);
    var probe = new Probe(after, testHook, disposables);
    context.RegisterSymbolAction(symbol => _analyzeType(symbol, probe), SymbolKind.NamedType);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "WHIZ607:A type name is read outside the shared naming code", Justification = "A test-code analyzer, which does not reference the generator naming helpers; the type is named only in the diagnostic message it reports (#2267)")]
  private static void _analyzeType(SymbolAnalysisContext context, Probe probe) {
    var type = (INamedTypeSymbol)context.Symbol;
    if (type.TypeKind != TypeKind.Class || !probe.TearsDownTwice(type)) {
      return;
    }

    // A class's base type is never null; only System.Object has none, and it is not declared in source.
    var baseType = type.BaseType!;
    if (SymbolEqualityComparer.Default.Equals(baseType.ContainingAssembly, context.Compilation.Assembly)
        && probe.TearsDownTwice(baseType)) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
      TeardownRunsTwice,
      type.Locations[0],
      type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
      string.Join(" and ", probe.DisposableInterfaces(type))));
  }

  /// <summary>The TUnit and BCL symbols of one compilation, and the two questions the rule asks of a class.</summary>
  private sealed class Probe(INamedTypeSymbol after, object? testHook, ImmutableArray<INamedTypeSymbol> disposables) {
    /// <summary>True when the class, or a base class, has an <c>[After(Test)]</c> hook and the class is disposable.</summary>
    public bool TearsDownTwice(INamedTypeSymbol type) => DisposableInterfaces(type).Any() && _hasTestTeardown(type);

    /// <summary>The disposable interfaces the class implements, declared or inherited, in a fixed order.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "WHIZ607:A type name is read outside the shared naming code", Justification = "A test-code analyzer, which does not reference the generator naming helpers; the interface is named only in the diagnostic message it reports (#2267)")]
    public IEnumerable<string> DisposableInterfaces(INamedTypeSymbol type) =>
      disposables
        .Where(disposable => type.AllInterfaces.Contains(disposable, SymbolEqualityComparer.Default))
        .Select(disposable => disposable.Name)
        .OrderBy(name => name, StringComparer.Ordinal);

    private bool _hasTestTeardown(INamedTypeSymbol type) {
      for (var current = type; current is not null; current = current.BaseType) {
        if (current.GetMembers().OfType<IMethodSymbol>().Any(method => method.GetAttributes().Any(_isTestTeardown))) {
          return true;
        }
      }
      return false;
    }

    private bool _isTestTeardown(AttributeData attribute) =>
      SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, after)
      && Equals(attribute.ConstructorArguments[0].Value, testHook);
  }
}
