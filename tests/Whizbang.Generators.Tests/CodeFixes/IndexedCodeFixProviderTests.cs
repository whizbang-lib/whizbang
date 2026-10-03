using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using TUnit.Assertions.Extensions;
using Whizbang.Generators.Analyzers;
using Whizbang.Generators.CodeFixes;

namespace Whizbang.Generators.Tests.CodeFixes;

/// <summary>
/// The WHIZ309 code fix: <c>[Indexed]</c> on the field a collective predicate filters, written where
/// the field is declared rather than where the predicate is.
/// </summary>
/// <docs>operations/diagnostics/whiz309</docs>
[Category("CodeFixes")]
public class IndexedCodeFixProviderTests {
  private const string MODEL_FILE = "Models.cs";
  private const string HANDLER_FILE = "Handlers.cs";

  private const string HANDLER = """
    using System;
    using System.Linq.Expressions;
    using Whizbang.Core.Lenses;
    using Whizbang.Core.Messaging;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public sealed record OverlayRemoved : CollectiveEventBase {
      public Guid OverlayId { get; init; }
    }

    public sealed record Spec<TModel>(
        Expression<Action<ICollectiveSetters<TModel>>> Setters,
        Expression<Func<PerspectiveRow<TModel>, bool>>? Where = null) : ICollectiveSpec<TModel> where TModel : class;

    public sealed class OverlayPerspective {
      [CollectiveApplyFor]
      public ICollectiveSpec<OverlayModel> Remove(OverlayRemoved e) =>
        new Spec<OverlayModel>(s => s.SetProperty(o => o.Name, "removed"), r => r.Data.OverlayId == e.OverlayId);
    }
    """;

  [Test]
  public async Task TheFixableIdIsWhiz309Async() {
    await Assert.That(new IndexedCodeFixProvider().FixableDiagnosticIds).IsEquivalentTo(["WHIZ309"]);
  }

  /// <summary>Several predicates often name one field, so fixing them all at once would repeat the attribute.</summary>
  [Test]
  public async Task ThereIsNoFixAllAsync() {
    await Assert.That(new IndexedCodeFixProvider().GetFixAllProvider()).IsNull();
  }

  /// <summary>The fix lands on the property declaration, in the model's own file, and adds the using it needs.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OnAPropertyDeclaration_AddsIndexedInTheModelsFileAsync() {
    const string model = """
      using System;

      namespace TestApp;

      public record OverlayModel {
        public Guid OverlayId { get; init; }
        public string Name { get; init; } = "";
      }
      """;

    var (fixedModel, fixedHandler) = await _applyAsync(model);

    await Assert.That(fixedModel).Contains("[Indexed]");
    await Assert.That(fixedModel).Contains("using Whizbang.Core.Perspectives;");
    var lines = fixedModel.Split('\n');
    var declaration = Array.FindIndex(lines, l => l.Contains("public Guid OverlayId", StringComparison.Ordinal));
    await Assert.That(lines[declaration - 1].Trim()).IsEqualTo("[Indexed]");
    await Assert.That(fixedHandler).IsEqualTo(HANDLER)
      .Because("the index belongs to the field, so the predicate's file is left as it was");
  }

  /// <summary>A positional record's property is declared by its parameter, so the attribute targets the property.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task OnAPositionalParameter_AddsIndexedTargetingThePropertyAsync() {
    const string model = """
      using System;
      using Whizbang.Core.Perspectives;

      namespace TestApp;

      public record OverlayModel(Guid OverlayId, string Name);
      """;

    var (fixedModel, _) = await _applyAsync(model);

    await Assert.That(fixedModel).Contains("[property: Indexed] Guid OverlayId");
    await Assert.That(fixedModel.Split("using Whizbang.Core.Perspectives;").Length).IsEqualTo(2)
      .Because("a using that is already there is not added twice");
  }

  /// <summary>
  /// A diagnostic without the field's declaration, which is what a model from a package produces,
  /// offers no fix: there is no source to write the attribute into.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task WithoutTheDeclaration_OffersNoFixAsync() {
    using var workspace = new AdhocWorkspace();
    var (document, diagnostic) = await _diagnosedAsync(workspace, """
      using System;
      namespace TestApp;
      public record OverlayModel {
        public Guid OverlayId { get; init; }
        public string Name { get; init; } = "";
      }
      """);
    var bare = Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location, "OverlayModel", "OverlayId");
    var foreign = Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location,
      [Location.Create(CSharpSyntaxTree.ParseText("class Elsewhere { int X { get; } }"), new TextSpan(18, 15))],
      "OverlayModel", "OverlayId");

    await Assert.That(await _actionsAsync(document, bare)).IsEmpty();
    await Assert.That(await _actionsAsync(document, foreign)).IsEmpty()
      .Because("a declaration outside the solution cannot be edited");
  }

  private static async Task<(string Model, string Handler)> _applyAsync(string model) {
    using var workspace = new AdhocWorkspace();
    var (document, diagnostic) = await _diagnosedAsync(workspace, model);
    var actions = await _actionsAsync(document, diagnostic);
    await Assert.That(actions).Count().IsEqualTo(1);
    await Assert.That(actions[0].Title).IsEqualTo("Add [Indexed]");

    var operations = await actions[0].GetOperationsAsync(CancellationToken.None);
    var solution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
    var project = solution.Projects.Single();
    var fixedModel = (await project.Documents.Single(d => d.Name == MODEL_FILE).GetTextAsync()).ToString();
    var fixedHandler = (await project.Documents.Single(d => d.Name == HANDLER_FILE).GetTextAsync()).ToString();
    return (fixedModel, fixedHandler);
  }

  private static async Task<List<CodeAction>> _actionsAsync(Document document, Diagnostic diagnostic) {
    var actions = new List<CodeAction>();
    var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
    await new IndexedCodeFixProvider().RegisterCodeFixesAsync(context);
    return actions;
  }

  /// <summary>
  /// A workspace holding the model and the handler as two documents, and the WHIZ309 the analyzer
  /// reports in it. The compilation comes from the workspace, so the diagnostic's locations are the
  /// workspace's own trees, as they are in an editor.
  /// </summary>
  private static async Task<(Document Handler, Diagnostic Diagnostic)> _diagnosedAsync(AdhocWorkspace workspace, string model) {
    var references = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("").References;
    var project = workspace.AddProject("TestProject", LanguageNames.CSharp)
      .WithMetadataReferences(references)
      .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    project = project.AddDocument(MODEL_FILE, SourceText.From(model)).Project;
    var handler = project.AddDocument(HANDLER_FILE, SourceText.From(HANDLER));

    var compilation = (await handler.Project.GetCompilationAsync())!;
    var diagnostics = await compilation
      .WithAnalyzers([new CollectivePredicateIndexAnalyzer()])
      .GetAnalyzerDiagnosticsAsync();

    return (handler, diagnostics.Single(d => d.Id == "WHIZ309"));
  }
}
