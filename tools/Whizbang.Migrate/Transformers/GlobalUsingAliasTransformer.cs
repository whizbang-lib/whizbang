using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms global using aliases that reference Marten or Wolverine types.
/// </summary>
/// <remarks>
/// Handles patterns like:
/// - global using MartenIEvent = Marten.Events.IEvent;
/// - global using SomeAlias = Wolverine.SomeType;
/// </remarks>
/// <docs>migration-guide/automated-migration</docs>
public sealed class GlobalUsingAliasTransformer : ICodeTransformer {
  /// <summary>
  /// Mapping of Marten/Wolverine types to their Whizbang equivalents.
  /// Key: fully qualified Marten/Wolverine type name
  /// Value: Whizbang replacement (null means remove the alias)
  /// </summary>
  private static readonly Dictionary<string, string?> _typeReplacements = new(StringComparer.Ordinal) {
    // Marten.Events.IEvent is the event wrapper with metadata
    // Whizbang uses MessageEnvelope for this purpose
    ["Marten.Events.IEvent"] = "Whizbang.Core.Messaging.MessageEnvelope",

    // Marten.Events.IEvent<T> is the generic event wrapper
    // Remove - use MessageEnvelope<T> directly where needed
    ["Marten.Events.IEvent<T>"] = null,

    // IDocumentSession - no direct equivalent, remove alias
    ["Marten.IDocumentSession"] = null,

    // IDocumentStore - maps to IEventStore
    ["Marten.IDocumentStore"] = "Whizbang.Core.Messaging.IEventStore",

    // Wolverine types
    ["Wolverine.IMessageBus"] = "Whizbang.Core.IDispatcher",
    ["Wolverine.MessageContext"] = "Whizbang.Core.Messaging.MessageEnvelope"
  };

  /// <summary>
  /// Namespace prefixes that indicate Marten/Wolverine types.
  /// </summary>
  private static readonly string[] _targetNamespacePrefixes = {
    "Marten.",
    "Wolverine."
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

    var compilationUnit = root as CompilationUnitSyntax;
    if (compilationUnit == null) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    // Find global using aliases that reference Marten/Wolverine
    var hasTargetAliases = compilationUnit.Usings
        .Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) &&
                  u.Alias != null &&
                  _isTargetType(u.Name?.ToString()));

    if (!hasTargetAliases) {
      return Task.FromResult(new TransformationResult(
          sourceCode,
          sourceCode,
          changes,
          warnings));
    }

    // Transform the usings
    var newRoot = _transformGlobalUsings(compilationUnit, changes, warnings);
    var transformedCode = newRoot.ToFullString();

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformedCode,
        changes,
        warnings));
  }

  /// <summary>
  /// Checks if a type name is a Marten or Wolverine type.
  /// </summary>
  private static bool _isTargetType(string? typeName) {
    if (string.IsNullOrEmpty(typeName)) {
      return false;
    }

    foreach (var prefix in _targetNamespacePrefixes) {
      if (typeName.StartsWith(prefix, StringComparison.Ordinal)) {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Transforms global using aliases.
  /// </summary>
  private static CompilationUnitSyntax _transformGlobalUsings(
      CompilationUnitSyntax compilationUnit,
      List<CodeChange> changes,
      List<string> warnings) {
    var newUsings = new List<UsingDirectiveSyntax>();

    foreach (var usingDirective in compilationUnit.Usings) {
      // Check if this is a global using alias targeting Marten/Wolverine
      if (usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) &&
          usingDirective.Alias != null) {
        var targetType = usingDirective.Name?.ToString();
        var aliasName = usingDirective.Alias.Name.ToString();

        if (_isTargetType(targetType)) {
          var lineNumber = usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

          // Check if we have a replacement
          var normalizedType = _normalizeGenericType(targetType!);
          if (_typeReplacements.TryGetValue(normalizedType, out var replacement)) {
            if (replacement != null) {
              // Replace with Whizbang equivalent
              var newName = SyntaxFactory.ParseName(replacement)
                  .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                  .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList());

              var newUsing = usingDirective.WithName(newName);
              newUsings.Add(newUsing);

              changes.Add(new CodeChange(
                  lineNumber,
                  ChangeType.UsingRemoved,
                  $"Replaced 'global using {aliasName} = {targetType}' with Whizbang equivalent",
                  $"global using {aliasName} = {targetType};",
                  $"global using {aliasName} = {replacement};"));
            } else {
              // Remove the alias entirely
              changes.Add(new CodeChange(
                  lineNumber,
                  ChangeType.UsingRemoved,
                  $"Removed 'global using {aliasName} = {targetType}' (no Whizbang equivalent)",
                  $"global using {aliasName} = {targetType};",
                  ""));

              warnings.Add(
                  $"Line {lineNumber}: Removed global using alias '{aliasName}' for '{targetType}'. " +
                  "Update code that uses this alias to use Whizbang types directly.");
            }
          } else {
            // Unknown Marten/Wolverine type - add warning but keep
            newUsings.Add(usingDirective);

            warnings.Add(
                $"Line {lineNumber}: Found global using alias '{aliasName}' for unknown Marten/Wolverine type '{targetType}'. " +
                "Review and update manually.");
          }

          continue;
        }
      }

      // Keep non-target usings as-is
      newUsings.Add(usingDirective);
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  /// <summary>
  /// Normalizes generic types for lookup (e.g., IEvent&lt;SomeType&gt; -> IEvent&lt;T&gt;).
  /// </summary>
  private static string _normalizeGenericType(string typeName) {
    var genericStart = typeName.IndexOf('<');
    if (genericStart < 0) {
      return typeName;
    }

    var baseName = typeName.Substring(0, genericStart);
    return baseName + "<T>";
  }
}
