// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Microsoft.CodeAnalysis.Text;

namespace Whizbang.Generators.CodeFixes;

/// <summary>
/// Roslyn code fix that adds <c>[Indexed]</c> to the perspective field a collective predicate filters,
/// for WHIZ309.
/// </summary>
/// <remarks>
/// <para>
/// The diagnostic is reported at the predicate, but the index belongs to the field, which is usually
/// declared in another file. The analyzer carries the field's declaration as the diagnostic's
/// additional location, and the fix edits that document. A field declared in a referenced assembly
/// has no declaration in the solution, and gets no fix.
/// </para>
/// <para>
/// A positional record declares its property with a parameter, so there the attribute is written with
/// the <c>property:</c> target, which is where the generators read it.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz309</docs>
/// <tests>tests/Whizbang.Generators.Tests/CodeFixes/IndexedCodeFixProviderTests.cs</tests>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(IndexedCodeFixProvider)), Shared]
public class IndexedCodeFixProvider : CodeFixProvider {
  private const string WHIZ309 = "WHIZ309";
  private const string INDEXED_NAMESPACE = "Whizbang.Core.Perspectives";
  private const string INDEXED_FQN = "global::Whizbang.Core.Perspectives.Indexed";
  private const string TITLE = "Add [Indexed]";

  /// <inheritdoc/>
  public override ImmutableArray<string> FixableDiagnosticIds => [WHIZ309];

  /// <inheritdoc/>
  /// <remarks>
  /// None: several predicates commonly name the same field, and fixing them all at once would write
  /// the attribute once per predicate. One fix per field is the whole job.
  /// </remarks>
  public override FixAllProvider? GetFixAllProvider() => null;

  /// <inheritdoc/>
  public override Task RegisterCodeFixesAsync(CodeFixContext context) {
    var solution = context.Document.Project.Solution;

    foreach (var diagnostic in context.Diagnostics) {
      if (diagnostic.AdditionalLocations is [{ SourceTree: { } tree } declaration, ..]
          && solution.GetDocument(tree) is { } document) {
        context.RegisterCodeFix(
          CodeAction.Create(
            title: TITLE,
            createChangedSolution: ct => Task.FromResult(_addIndexed(document, tree, declaration.SourceSpan, ct)),
            equivalenceKey: nameof(IndexedCodeFixProvider)),
          diagnostic);
      }
    }

    return Task.CompletedTask;
  }

  private static Solution _addIndexed(Document document, SyntaxTree tree, TextSpan span, CancellationToken cancellationToken) {
    var root = (CompilationUnitSyntax)tree.GetRoot(cancellationToken);
    var declaration = root.FindNode(span);

    // The analyzer's location is a property's declaring syntax, which is a property declaration or,
    // in a positional record, the parameter that declares it.
    SyntaxNode annotated = declaration is ParameterSyntax parameter
      ? parameter.AddAttributeLists(_indexed(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.PropertyKeyword))))
      : ((PropertyDeclarationSyntax)declaration).AddAttributeLists(_indexed(target: null));

    var updated = root.ReplaceNode(declaration, annotated);
    if (!updated.Usings.Any(static u => u.NamespaceOrType.ToString() == INDEXED_NAMESPACE)) {
      updated = updated.AddUsings(
        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(INDEXED_NAMESPACE)).WithAdditionalAnnotations(Formatter.Annotation));
    }

    return document.WithSyntaxRoot(updated).Project.Solution;
  }

  private static AttributeListSyntax _indexed(AttributeTargetSpecifierSyntax? target) =>
    SyntaxFactory.AttributeList(
        target,
        SyntaxFactory.SingletonSeparatedList(
          SyntaxFactory.Attribute(SyntaxFactory.ParseName(INDEXED_FQN).WithAdditionalAnnotations(Simplifier.Annotation))))
      .WithAdditionalAnnotations(Formatter.Annotation);
}
