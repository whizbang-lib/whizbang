using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;
using Whizbang.Generators.CodeFixes;

namespace Whizbang.Generators.Tests.CodeFixes;

/// <summary>
/// Tests for PinnedIdCodeFixProvider — inserts [PinnedId("&lt;guid&gt;")] on WHIZ110/WHIZ111.
/// </summary>
[Category("CodeFixes")]
public class PinnedIdCodeFixProviderTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task CodeFix_FixableIds_IncludesWhiz110And111Async() {
    var provider = new PinnedIdCodeFixProvider();

    await Assert.That(provider.FixableDiagnosticIds).Contains("WHIZ110");
    await Assert.That(provider.FixableDiagnosticIds).Contains("WHIZ111");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CodeFix_GetFixAllProvider_ReturnsBatchFixerAsync() {
    var provider = new PinnedIdCodeFixProvider();

    var fixAll = provider.GetFixAllProvider();

    await Assert.That(fixAll).IsEqualTo(WellKnownFixAllProviders.BatchFixer);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CodeFix_OnWhiz110_InsertsPinnedIdAttributeAsync() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public record OrderPlacedEvent : IEvent;
        """;

    var fixedSource = await _applyCodeFixAsync(source);

    await Assert.That(fixedSource).Contains("[PinnedId(\"");
    await Assert.That(fixedSource).Contains("using Whizbang.Core.Attributes;");
    await Assert.That(fixedSource).Contains("public record OrderPlacedEvent");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CodeFix_OnWhiz111_InsertsPinnedIdOnPerspectiveAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Perspectives;
        namespace TestApp;
        public record OrderView;
        public record OrderPlacedEvent : IEvent;
        public class OrderPerspective : IPerspectiveFor<OrderView, OrderPlacedEvent> {
          public OrderView Apply(OrderView? current, OrderPlacedEvent @event) => current ?? new();
        }
        """;

    var fixedSource = await _applyCodeFixAsync(source, diagnosticId: "WHIZ111");

    await Assert.That(fixedSource).Contains("[PinnedId(\"");
    // Perspective should have the attribute
    var lines = fixedSource.Split('\n');
    var perspectiveLineIndex = Array.FindIndex(lines, l => l.Contains("public class OrderPerspective", StringComparison.Ordinal));
    await Assert.That(perspectiveLineIndex).IsGreaterThan(0);
    await Assert.That(lines[perspectiveLineIndex - 1]).Contains("[PinnedId(");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CodeFix_InsertsGuidInValidFormatAsync() {
    const string source = """
        using Whizbang.Core;
        namespace TestApp;
        public record OrderPlacedEvent : IEvent;
        """;

    var fixedSource = await _applyCodeFixAsync(source);

    // Pull out the GUID value and confirm Guid.Parse succeeds
    var start = fixedSource.IndexOf("[PinnedId(\"", StringComparison.Ordinal);
    await Assert.That(start).IsGreaterThan(-1);
    var valueStart = start + "[PinnedId(\"".Length;
    var valueEnd = fixedSource.IndexOf('"', valueStart);
    var guidString = fixedSource[valueStart..valueEnd];

    await Assert.That(Guid.TryParse(guidString, out _)).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CodeFix_DoesNotDuplicateUsingDirectiveAsync() {
    const string source = """
        using Whizbang.Core;
        using Whizbang.Core.Attributes;
        namespace TestApp;
        public record OrderPlacedEvent : IEvent;
        """;

    var fixedSource = await _applyCodeFixAsync(source);

    // Count occurrences of "using Whizbang.Core.Attributes;"
    var count = 0;
    var idx = 0;
    const string needle = "using Whizbang.Core.Attributes;";
    while ((idx = fixedSource.IndexOf(needle, idx, StringComparison.Ordinal)) != -1) {
      count++;
      idx += needle.Length;
    }
    await Assert.That(count).IsEqualTo(1);
  }

  [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Document APIs need ad-hoc workspace setup.")]
  private static async Task<string> _applyCodeFixAsync(string source, string? diagnosticId = null) {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);

    var references = new List<MetadataReference>();
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.dll")));
    references.Add(MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "netstandard.dll")));
    try {
      var coreAssembly = System.Reflection.Assembly.Load("Whizbang.Core");
      references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
    } catch { /* fallback below */ }

    var compilation = CSharpCompilation.Create(
      assemblyName: "TestAssembly",
      syntaxTrees: [syntaxTree],
      references: references,
      options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var analyzer = new PinnedIdAnalyzer();
    var compilationWithAnalyzers = compilation.WithAnalyzers([analyzer]);
    var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    var targetDiagnostic = diagnosticId is null
      ? diagnostics.First(d => d.Id is "WHIZ110" or "WHIZ111")
      : diagnostics.First(d => d.Id == diagnosticId);

    // Build an ad-hoc workspace + document so the code fix provider can operate.
    using var workspace = new AdhocWorkspace();
    var project = workspace.AddProject("TestProject", LanguageNames.CSharp)
      .WithMetadataReferences(references)
      .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var document = project.AddDocument("Test.cs", SourceText.From(source));

    var provider = new PinnedIdCodeFixProvider();
    var actions = new List<CodeAction>();
    var fixContext = new CodeFixContext(
      document,
      targetDiagnostic,
      (action, diagnostics) => actions.Add(action),
      CancellationToken.None);

    await provider.RegisterCodeFixesAsync(fixContext);
    await Assert.That(actions).IsNotEmpty();

    var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
    var applyOp = operations.OfType<ApplyChangesOperation>().First();
    var changedDocument = applyOp.ChangedSolution.GetDocument(document.Id)!;
    var changedText = await changedDocument.GetTextAsync();
    return changedText.ToString();
  }
}
