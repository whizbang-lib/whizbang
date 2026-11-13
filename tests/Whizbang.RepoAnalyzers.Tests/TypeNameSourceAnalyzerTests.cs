// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// WHIZ607: a type's name is produced or resolved only by the shared naming code (#2267). Outside a type marked
/// <c>[TypeNameAuthority]</c>, reading a name off a <see cref="System.Type"/>, a <see cref="System.Reflection.TypeInfo"/>,
/// an assembly or an assembly name, resolving a type from a string, or (in a generator) reading a type, namespace or
/// assembly symbol's name is an error. WHIZ608: the only other way out is a <c>[SuppressMessage]</c> that says why; one
/// with no justification, or a <c>#pragma</c> that hides the rule, is an error.
/// </summary>
public class TypeNameSourceAnalyzerTests {
  private const string RUNTIME_USINGS = """
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Reflection;

    """;

  private const string ROSLYN_USINGS = """
    using System;
    using System.Diagnostics.CodeAnalysis;
    using Microsoft.CodeAnalysis;

    """;

  private const string AUTHORITY = """
    [AttributeUsage(AttributeTargets.Class)]
    sealed class TypeNameAuthorityAttribute : Attribute { }

    """;

  [Test]
  public async Task Descriptors_AreTheRepositoryRulesReportedAsErrorsAsync() {
    var descriptors = new TypeNameSourceAnalyzer().SupportedDiagnostics;

    await Assert.That(descriptors.Select(d => d.Id).ToList()).IsEquivalentTo(["WHIZ607", "WHIZ608"]);
    await Assert.That(descriptors.All(d => d.DefaultSeverity == DiagnosticSeverity.Error && d.IsEnabledByDefault)).IsTrue();
  }

  // ===== runtime: reported =====

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("typeof(C).Name")]
  [Arguments("typeof(C).FullName")]
  [Arguments("typeof(C).Namespace")]
  [Arguments("typeof(C).AssemblyQualifiedName")]
  [Arguments("typeof(C).ToString()")]
  [Arguments("new C().GetType().Name")]
  [Arguments("typeof(C).GetTypeInfo().Name")]
  [Arguments("typeof(C).Assembly.FullName")]
  [Arguments("typeof(C).Assembly.GetName().Name")]
  [Arguments("Type.GetType(\"C\")?.Name ?? \"\"")]
  [Arguments("typeof(C).Assembly.GetType(\"C\")?.Namespace ?? \"\"")]
  public async Task Runtime_ANameReadOffATypeOrAssembly_IsReportedAsync(string expression) {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + $$"""
      class C {
        static string M() => {{expression}};
      }
      """);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ607")).IsGreaterThanOrEqualTo(1)
      .Because($"'{expression}' renders a type name outside the shared naming code");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runtime_TypeGetTypeAndAssemblyGetType_AreEachReportedAsync() {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + """
      class C {
        static object M() => (object?)Type.GetType("C", throwOnError: false) ?? typeof(C).Assembly.GetType("C")!;
      }
      """);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ607")).IsEqualTo(2);
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("TypeNameFormatter");
  }

  // ===== runtime: not reported =====

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runtime_AMembersNameOnANonTypeReceiver_IsNotReportedAsync() {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + """
      class Named { public string Name => "n"; }
      class C {
        static string M(MethodInfo method, PropertyInfo property, Named named) => method.Name + property.Name + named.Name;
        static bool Same(Type a, Type b) => a == b;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runtime_InsideANamingAuthority_IsNotReportedAsync() {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + AUTHORITY + """
      [TypeNameAuthority]
      static class Formatter {
        static string Format(Type type) => type.FullName + ", " + type.Assembly.GetName().Name;
        static class Nested { static string N(Type type) => type.Name; }
      }
      """);

    await Assert.That(diagnostics).IsEmpty().Because("the shared naming code, and anything nested in it, is where names come from");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runtime_ASuppressionThatSaysWhy_IsHonoredAsync() {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + """
      class C {
        [SuppressMessage("Naming", "WHIZ607:A type name is read outside the shared naming code", Justification = "the exception message names the type for a reader")]
        static string M() => typeof(C).Name;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  // ===== WHIZ608: suppressions =====

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("[SuppressMessage(\"Naming\", \"WHIZ607\")]")]
  [Arguments("[SuppressMessage(\"Naming\", \"WHIZ607\", Justification = \"\")]")]
  [Arguments("[SuppressMessage(\"Naming\", \"WHIZ607:anything\", Justification = \"   \")]")]
  public async Task Suppression_WithoutAJustification_IsReportedAsync(string attribute) {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + $$"""
      class C {
        {{attribute}}
        static string M() => typeof(C).Name;
      }
      """);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ608")).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Suppression_OfAnotherRule_WithoutAJustification_IsNotThisRulesConcernAsync() {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + """
      class C {
        [SuppressMessage("Style", "IDE0001")]
        static int M() => 1;
      }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Pragma_ThatHidesTheRule_IsReportedAsync() {
    var diagnostics = await _analyzeAsync(RUNTIME_USINGS + """
      class C {
      #pragma warning disable WHIZ607
        static string M() => typeof(C).Name;
      #pragma warning restore WHIZ607
      #pragma warning disable CS0168
      }
      """);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ608")).IsEqualTo(1)
      .Because("a pragma carries no justification; the restore and an unrelated pragma are fine");
  }

  // ===== generators =====

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("type.Name")]
  [Arguments("type.MetadataName")]
  [Arguments("type.ToDisplayString()")]
  [Arguments("type.ContainingNamespace.Name")]
  [Arguments("type.ContainingAssembly.Name")]
  [Arguments("string.Concat(type.ToDisplayParts())")]
  [Arguments("ns.Name")]
  [Arguments("asm.Identity.Name")]
  public async Task Generator_ANameReadOffATypeNamespaceOrAssemblySymbol_IsReportedAsync(string expression) {
    var diagnostics = await _analyzeAsync(ROSLYN_USINGS + $$"""
      class G {
        static string M(INamedTypeSymbol type, INamespaceSymbol ns, IAssemblySymbol asm) => {{expression}};
      }
      """, roslyn: true);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ607")).IsGreaterThanOrEqualTo(1)
      .Because($"'{expression}' renders a type name outside TypeNameUtilities");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AMemberSymbolsName_IsNotReportedAsync() {
    var diagnostics = await _analyzeAsync(ROSLYN_USINGS + """
      class G {
        static string M(IMethodSymbol method, IPropertySymbol property, IParameterSymbol parameter) =>
          method.Name + property.Name + parameter.Name + method.ToDisplayString();
      }
      """, roslyn: true);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_InsideANamingAuthority_IsNotReportedAsync() {
    var diagnostics = await _analyzeAsync(ROSLYN_USINGS + AUTHORITY + """
      [TypeNameAuthority]
      static class TypeNameUtilities {
        static string M(ITypeSymbol type) => type.ToDisplayString() + type.Name;
      }
      """, roslyn: true);

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ACompilationWithoutReflectionOrRoslynTypes_StaysQuietAsync() {
    // The runtime half needs System.Type and the generator half Microsoft.CodeAnalysis.ISymbol; a compilation that
    // references neither (a bare netstandard facade has Type, so this checks the Roslyn half stays off) reports nothing.
    var diagnostics = await _analyzeAsync("""
      class C { static int M() => 1; }
      """);

    await Assert.That(diagnostics).IsEmpty();
  }

  private static async Task<IReadOnlyList<Diagnostic>> _analyzeAsync(string source, bool roslyn = false) {
    var compilationErrors = RepoAnalyzerTestHelper.CreateCompilation(source, withRoslynReference: roslyn)
      .GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString())
      .ToList();
    if (compilationErrors.Count > 0) {
      throw new InvalidOperationException("The snippet does not compile: " + string.Join("; ", compilationErrors));
    }
    var diagnostics = await RepoAnalyzerTestHelper.GetDiagnosticsAsync<TypeNameSourceAnalyzer>(source, withRoslynReference: roslyn);
    return [.. diagnostics.OrderBy(d => d.Location.SourceSpan.Start)];
  }
}
