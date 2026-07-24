using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Whizbang.Generators.CodeFixes;

/// <summary>
/// Roslyn code fix that adds a <c>[PinnedId("&lt;new-guid&gt;")]</c> attribute to a concrete
/// IMessage or IPerspectiveFor&lt;&gt; type flagged by WHIZ110 / WHIZ111.
/// </summary>
/// <remarks>
/// <para>
/// Generates a fresh <see cref="System.Guid"/> at fix time. Once accepted and committed, the
/// pinned identity is frozen forever — re-running the code fix on a re-flagged type would
/// generate a different GUID, so apply once and commit.
/// </para>
/// <para>
/// Supports "Fix all in solution" via Roslyn's standard batch fixer.
/// </para>
/// </remarks>
/// <docs>core-concepts/pinned-identity</docs>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PinnedIdCodeFixProvider)), Shared]
public class PinnedIdCodeFixProvider : CodeFixProvider {
  private const string WHIZ110 = "WHIZ110";
  private const string WHIZ111 = "WHIZ111";
  private const string PINNED_ID_ATTRIBUTE_NAMESPACE = "Whizbang.Core.Attributes";
  private const string PINNED_ID_ATTRIBUTE_FQN = "global::Whizbang.Core.Attributes.PinnedId";

  /// <inheritdoc/>
  public override ImmutableArray<string> FixableDiagnosticIds => [WHIZ110, WHIZ111];

  /// <inheritdoc/>
  public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

  /// <inheritdoc/>
  public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
    var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
    if (root is null) {
      return;
    }

    foreach (var diagnostic in context.Diagnostics) {
      var diagnosticSpan = diagnostic.Location.SourceSpan;
      var node = root.FindNode(diagnosticSpan);
      var typeDeclaration = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
      if (typeDeclaration is null) {
        continue;
      }

      context.RegisterCodeFix(
        CodeAction.Create(
          title: "Add [PinnedId(\"<new-guid>\")]",
          createChangedDocument: ct => _addPinnedIdAsync(context.Document, typeDeclaration, ct),
          equivalenceKey: nameof(PinnedIdCodeFixProvider)),
        diagnostic);
    }
  }

  private static async Task<Document> _addPinnedIdAsync(
      Document document,
      TypeDeclarationSyntax typeDeclaration,
      CancellationToken cancellationToken) {

    var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
    if (root is null) {
      return document;
    }

    var guid = System.Guid.NewGuid().ToString();

    // Use the fully-qualified name + Simplifier.Annotation so Roslyn collapses it to "PinnedId"
    // after the using directive is added. Avoids a transient "unresolved PinnedId" state that
    // can make VS Code reject the change with "Sorry, your request failed".
    var attributeName = SyntaxFactory.ParseName(PINNED_ID_ATTRIBUTE_FQN)
      .WithAdditionalAnnotations(Simplifier.Annotation);

    var attribute = SyntaxFactory.Attribute(
      attributeName,
      SyntaxFactory.AttributeArgumentList(
        SyntaxFactory.SingletonSeparatedList(
          SyntaxFactory.AttributeArgument(
            SyntaxFactory.LiteralExpression(
              SyntaxKind.StringLiteralExpression,
              SyntaxFactory.Literal(guid))))));

    var attributeList = SyntaxFactory.AttributeList(
      SyntaxFactory.SingletonSeparatedList(attribute))
      .WithAdditionalAnnotations(Formatter.Annotation);

    var newTypeDeclaration = typeDeclaration.AddAttributeLists(attributeList);
    var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);

    if (newRoot is CompilationUnitSyntax compilationUnit && !_hasUsing(compilationUnit, PINNED_ID_ATTRIBUTE_NAMESPACE)) {
      var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(PINNED_ID_ATTRIBUTE_NAMESPACE))
        .WithAdditionalAnnotations(Formatter.Annotation);
      newRoot = compilationUnit.AddUsings(usingDirective);
    }

    return document.WithSyntaxRoot(newRoot);
  }

  private static bool _hasUsing(CompilationUnitSyntax compilationUnit, string namespaceName) {
    return compilationUnit.Usings.Any(u =>
      u.Name is not null && u.Name.ToString() == namespaceName);
  }
}
