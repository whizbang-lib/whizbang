using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;
using Whizbang.Generators.CodeFixes;

namespace Whizbang.Generators.Tests.CodeFixes;

/// <summary>
/// Coverage-focused tests for <see cref="PinnedIdCodeFixProvider"/> targeting the
/// "diagnostic location doesn't resolve to a type declaration" guard, which the primary test
/// suite never reaches because every diagnostic it feeds the provider is a live WHIZ110/WHIZ111
/// straight off the analyzer (always anchored on a type's own location).
/// </summary>
[Category("CodeFixes")]
public class PinnedIdCodeFixProviderCoverageTests {
  // A diagnostic can outlive the edit that invalidated it: the operator deletes or renames the
  // flagged type (or a "Fix All" runs against a stale diagnostic snapshot) between the time
  // WHIZ110 was computed and the time the fix is applied. If the provider assumed every
  // diagnostic location sits inside a type declaration, it would throw instead of quietly
  // declining to offer a fix for that one diagnostic.
  [Test]
  [RequiresAssemblyFiles]
  [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Document APIs need ad-hoc workspace setup.")]
  public async Task CodeFix_DiagnosticLocationOutsideTypeDeclaration_RegistersNoActionAsync() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public record OrderPlacedEvent : IEvent;
        """;

    // A location that is real (inside the parsed source) but not inside any type declaration --
    // the namespace name itself.
    var syntaxTree = CSharpSyntaxTree.ParseText(source);
    var root = await syntaxTree.GetRootAsync();
    var namespaceDeclaration = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().First();
    var location = namespaceDeclaration.Name.GetLocation();
    var diagnostic = Diagnostic.Create(DiagnosticDescriptors.MessageMissingPinnedId, location, "OrderPlacedEvent");

    var references = new List<MetadataReference>();
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "netstandard.dll")));
    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch { /* fallback: proceed without it, this test never binds semantics */ }

    using var workspace = new AdhocWorkspace();
    var project = workspace.AddProject("TestProject", LanguageNames.CSharp)
      .WithMetadataReferences(references)
      .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var document = project.AddDocument("Test.cs", SourceText.From(source));

    var provider = new PinnedIdCodeFixProvider();
    var actions = new List<CodeAction>();
    var fixContext = new CodeFixContext(
      document,
      diagnostic,
      (action, diagnostics) => actions.Add(action),
      CancellationToken.None);

    // Act
    await provider.RegisterCodeFixesAsync(fixContext);

    // Assert - no crash, and no fix offered for a diagnostic that no longer points at a type.
    await Assert.That(actions).IsEmpty();
  }
}
