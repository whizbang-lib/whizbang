using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for GuidInterceptorGenerator targeting the suppress-attribute name walk
/// in <c>_hasSuppressAttribute</c> and the file-name sanitization used to build interceptor method
/// names. Complements GuidInterceptorGeneratorTests.cs.
/// </summary>
/// <remarks>
/// Four lines are not covered here, each because the guard is unreachable from a valid compilation:
/// <list type="bullet">
/// <item>Line 105 (<c>containingType is null</c> in <c>_extractGuidCallInfo</c>) — the predicate only
/// matches an <c>InvocationExpressionSyntax</c> whose expression is a <c>MemberAccessExpressionSyntax</c>;
/// any such invocation that resolves to an <see cref="IMethodSymbol"/> always has a non-null
/// <c>ContainingType</c> in the C# symbol model. API-guaranteed, not a reachable branch.</item>
/// <item>Line 280 (<c>_isActiveDisablePragma</c>) and line 318 (<c>_isMatchingRestore</c>) both guard
/// <c>trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax</c>, but both call sites
/// pre-filter their trivia with <c>t.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia)</c> — Roslyn
/// guarantees that trivia of that kind structures into exactly that syntax type. Dead by
/// construction: the input is already validated before either method sees it.</item>
/// <item>Line 417 (the <c>_ =&gt;</c> default arm of the <c>originalCall</c> switch in
/// <c>_generateInterceptors</c>) — every <see cref="Whizbang.Generators.GuidInterceptionInfo"/> that
/// reaches this switch was produced by <c>_resolveGuidVersionAndSource</c>, which only ever succeeds
/// for exactly six (containing type, method name) pairs: System.Guid.NewGuid/CreateVersion7, and the
/// four entries in <c>_thirdPartyMethods</c> (Marten NewGuid, UUIDNext NewDatabaseFriendly/NewSequential,
/// Medo NewUuid7). All six are handled by the switch's other arms, so the default arm can never run —
/// dead by construction, tracing back to the producer that restricts the input.</item>
/// </list>
/// </remarks>
public class GuidInterceptorGeneratorCoverageTests {
  private static readonly Dictionary<string, string> _interceptionEnabledOptions = new() {
    ["build_property.WhizbangGuidInterceptionEnabled"] = "true"
  };

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_UnrelatedAttributeOnMethod_DoesNotSuppressInterceptionAsync() {
    // A method decorated with some other attribute must not be mistaken for one carrying
    // [SuppressGuidInterception]; otherwise an unrelated attribute would silently disable tracking
    // for every Guid created in that method.
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              [Obsolete]
              public Guid CreateId() {
                return Guid.NewGuid();
              }
            }
            """;

    var result = GeneratorTestHelper.RunGenerator<GuidInterceptorGenerator>(source, _interceptionEnabledOptions);

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("InterceptsLocation")
      .Because("an unrelated attribute on the method must not be treated as a suppression");
  }

  /// <summary>
  /// Runs GuidInterceptorGenerator against source parsed with an explicit file path (rather than
  /// the shared helper's unnamed syntax tree), so the file-name sanitization in
  /// <c>_sanitizeFileName</c> has a non-trivial, non-identifier-safe name to sanitize.
  /// </summary>
  private static GeneratorDriverRunResult _runWithFilePath(string source, string filePath) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source, path: filePath);

    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    var references = new List<MetadataReference> {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll"))
    };

    var compilation = CSharpCompilation.Create(
        assemblyName: "TestAssembly",
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var generator = new GuidInterceptorGenerator();
    var driver = CSharpGeneratorDriver.Create(
        generators: [generator.AsSourceGenerator()],
        optionsProvider: new _enabledOptionsProvider());

    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
    return driver.GetRunResult();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_FileNameWithSpecialCharacters_SanitizesInterceptorMethodNameAsync() {
    // Interceptor method names are C# identifiers synthesized from the source file's name; a path
    // with characters illegal in an identifier (spaces, dashes, extra dots) must be sanitized, or
    // the generated interceptor file fails to compile for every consumer of the library.
    const string source = """
            using System;

            namespace TestApp;

            public class MyService {
              public Guid CreateId() {
                return Guid.NewGuid();
              }
            }
            """;

    var result = _runWithFilePath(source, "/repo/My Order-Service.File.cs");

    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "GuidInterceptors.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("Intercept_My_Order_Service_File_")
      .Because("non-alphanumeric characters in the file name must become underscores in the generated identifier");
  }

  private sealed class _enabledOptionsProvider : AnalyzerConfigOptionsProvider {
    public override AnalyzerConfigOptions GlobalOptions => new _enabledOptions();
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new _enabledOptions();
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new _enabledOptions();
  }

  private sealed class _enabledOptions : AnalyzerConfigOptions {
    public override bool TryGetValue(string key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) {
      if (key == "build_property.WhizbangGuidInterceptionEnabled") {
        value = "true";
        return true;
      }
      value = null;
      return false;
    }
  }
}
