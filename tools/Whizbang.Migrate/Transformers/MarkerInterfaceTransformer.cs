using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Wolverine marker interfaces (IEvent, ICommand) to Whizbang equivalents.
/// This handles files that only use marker interfaces without other Wolverine patterns.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class MarkerInterfaceTransformer : ICodeTransformer {
  /// <summary>
  /// Wolverine marker interfaces that indicate the file needs transformation.
  /// </summary>
  private static readonly HashSet<string> _wolverineMarkerInterfaces = new(StringComparer.Ordinal) {
    "IEvent",
    "ICommand",
    "IMessage"
  };

  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    // Check if file has types that inherit from Wolverine marker interfaces
    if (!_hasWolverineMarkerInterfaceUsage(root)) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    // Check if file has using Wolverine
    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    var hasWolverineUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString() == "Wolverine");

    if (!hasWolverineUsing) {
      // No Wolverine using, nothing to transform
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    // Transform using directive
    var newRoot = _transformUsings(compilationUnit, changes);

    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  /// <summary>
  /// Checks if the file has types that inherit from Wolverine marker interfaces.
  /// </summary>
  private static bool _hasWolverineMarkerInterfaceUsage(SyntaxNode root) {
    // Check class declarations
    var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
    foreach (var classDecl in classDeclarations) {
      if (_inheritsFromMarkerInterface(classDecl.BaseList)) {
        return true;
      }
    }

    // Check record declarations
    var recordDeclarations = root.DescendantNodes().OfType<RecordDeclarationSyntax>();
    foreach (var recordDecl in recordDeclarations) {
      if (_inheritsFromMarkerInterface(recordDecl.BaseList)) {
        return true;
      }
    }

    // Check interface declarations
    var interfaceDeclarations = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>();
    foreach (var interfaceDecl in interfaceDeclarations) {
      if (_inheritsFromMarkerInterface(interfaceDecl.BaseList)) {
        return true;
      }
    }

    // Check struct declarations
    var structDeclarations = root.DescendantNodes().OfType<StructDeclarationSyntax>();
    foreach (var structDecl in structDeclarations) {
      if (_inheritsFromMarkerInterface(structDecl.BaseList)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Checks if the base list contains any Wolverine marker interfaces.
  /// </summary>
  private static bool _inheritsFromMarkerInterface(BaseListSyntax? baseList) {
    if (baseList == null) {
      return false;
    }

    foreach (var baseType in baseList.Types) {
      var typeName = _getBaseTypeName(baseType.Type);
      if (_wolverineMarkerInterfaces.Contains(typeName)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Extracts the base type name, handling generic types.
  /// </summary>
  private static string _getBaseTypeName(TypeSyntax type) {
    return type switch {
      IdentifierNameSyntax id => id.Identifier.Text,
      GenericNameSyntax generic => generic.Identifier.Text,
      QualifiedNameSyntax qualified => _getBaseTypeName(qualified.Right),
      _ => type.ToString()
    };
  }

  /// <summary>
  /// Transforms using Wolverine to using Whizbang.Core.
  /// </summary>
  private static CompilationUnitSyntax _transformUsings(
      CompilationUnitSyntax compilationUnit,
      List<CodeChange> changes) {
    var newUsings = new List<UsingDirectiveSyntax>();

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();

      if (name == "Wolverine") {
        // Replace with Whizbang.Core - preserve original formatting
        var whizbangUsing = usingDirective
            .WithName(SyntaxFactory.ParseName("Whizbang.Core")
                .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
        newUsings.Add(whizbangUsing);

        changes.Add(new CodeChange(
            usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.UsingRemoved,
            "Replaced 'using Wolverine' with 'using Whizbang.Core' (marker interface migration)",
            "using Wolverine;",
            "using Whizbang.Core;"));
      } else {
        newUsings.Add(usingDirective);
      }
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }
}
