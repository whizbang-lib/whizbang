using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Migrate.Transformers;

/// <summary>
/// Transforms Newtonsoft.Json usage to System.Text.Json equivalents.
/// This is an optional migration - controlled by decision file.
/// </summary>
/// <docs>migration-guide/json-migration</docs>
public sealed class NewtonsoftToSystemTextJsonTransformer : ICodeTransformer {
  private readonly bool _enabled;
  private readonly bool _removeDeadImports;
  private readonly bool _addTodoForUnsupported;

  /// <summary>
  /// Creates a new Newtonsoft to System.Text.Json transformer.
  /// </summary>
  /// <param name="enabled">Whether to perform transformations (false = only remove dead imports).</param>
  /// <param name="removeDeadImports">Whether to remove unused Newtonsoft imports.</param>
  /// <param name="addTodoForUnsupported">Whether to add TODO comments for unsupported patterns.</param>
  public NewtonsoftToSystemTextJsonTransformer(
      bool enabled = true,
      bool removeDeadImports = true,
      bool addTodoForUnsupported = true) {
    _enabled = enabled;
    _removeDeadImports = removeDeadImports;
    _addTodoForUnsupported = addTodoForUnsupported;
  }

  /// <inheritdoc />
  public Task<TransformationResult> TransformAsync(
      string sourceCode,
      string filePath,
      CancellationToken ct = default) {
    var changes = new List<CodeChange>();
    var warnings = new List<string>();

    var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: ct);
    var root = tree.GetRoot(ct);

    if (root is not CompilationUnitSyntax compilationUnit) {
      return Task.FromResult(new TransformationResult(sourceCode, sourceCode, changes, warnings));
    }

    // Check if file uses Newtonsoft.Json (any Newtonsoft namespace)
    var hasNewtonsoftUsing = compilationUnit.Usings
        .Any(u => u.Name?.ToString().StartsWith("Newtonsoft", StringComparison.Ordinal) == true);

    if (!hasNewtonsoftUsing) {
      return Task.FromResult(new TransformationResult(sourceCode, sourceCode, changes, warnings));
    }

    // Check if Newtonsoft types are actually used
    var usesNewtonsoftTypes = _hasNewtonsoftTypeUsage(root);

    // If only import exists but no types used, remove the dead import
    if (!usesNewtonsoftTypes) {
      if (_removeDeadImports) {
        var newRoot = _removeNewtonsoftUsings(compilationUnit, changes);
        return Task.FromResult(new TransformationResult(
            sourceCode,
            newRoot.ToFullString(),
            changes,
            warnings));
      }
      return Task.FromResult(new TransformationResult(sourceCode, sourceCode, changes, warnings));
    }

    // If not enabled, just report what would be transformed
    if (!_enabled) {
      warnings.Add("File uses Newtonsoft.Json types. Enable json_migration to transform.");
      return Task.FromResult(new TransformationResult(sourceCode, sourceCode, changes, warnings));
    }

    // Perform transformations
    var transformed = _transformNewtonsoftUsage(compilationUnit, changes, warnings);

    return Task.FromResult(new TransformationResult(
        sourceCode,
        transformed.ToFullString(),
        changes,
        warnings));
  }

  /// <summary>
  /// Checks if any Newtonsoft.Json types are actually used (not just imported).
  /// </summary>
  private static bool _hasNewtonsoftTypeUsage(SyntaxNode root) {
    // Check for JsonProperty attributes
    var attributes = root.DescendantNodes().OfType<AttributeSyntax>();
    foreach (var attr in attributes) {
      var name = _getAttributeName(attr);
      if (name is "JsonProperty" or "JsonPropertyAttribute" or
          "JsonIgnore" or "JsonIgnoreAttribute" or
          "JsonConverter" or "JsonConverterAttribute" or
          "JsonObject" or "JsonObjectAttribute" or
          "JsonArray" or "JsonArrayAttribute" or
          "JsonExtensionData" or "JsonExtensionDataAttribute" or
          "JsonConstructor" or "JsonConstructorAttribute") {
        return true;
      }
    }

    // Check for JsonConvert usage
    var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
    foreach (var invocation in invocations) {
      var expr = invocation.Expression.ToString();
      if (expr.Contains("JsonConvert.") ||
          expr.Contains("JToken.") ||
          expr.Contains("JObject.") ||
          expr.Contains("JArray.") ||
          expr.Contains("JSchemaGenerator")) {
        return true;
      }
    }

    // Check for type declarations using Newtonsoft types
    var identifiers = root.DescendantNodes().OfType<IdentifierNameSyntax>();
    foreach (var id in identifiers) {
      var name = id.Identifier.Text;
      if (name is "JObject" or "JArray" or "JToken" or "JValue" or
          "JsonSerializer" or "JsonReader" or "JsonWriter" or
          "JSchemaGenerator" or "JSchema") {
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Gets the name of an attribute (without "Attribute" suffix).
  /// </summary>
  private static string _getAttributeName(AttributeSyntax attr) {
    return attr.Name switch {
      IdentifierNameSyntax id => id.Identifier.Text,
      QualifiedNameSyntax qualified => _getAttributeName(qualified),
      _ => attr.Name.ToString()
    };
  }

  private static string _getAttributeName(QualifiedNameSyntax qualified) {
    return qualified.Right switch {
      IdentifierNameSyntax id => id.Identifier.Text,
      _ => qualified.Right.ToString()
    };
  }

  /// <summary>
  /// Removes Newtonsoft.Json using directives.
  /// </summary>
  private static CompilationUnitSyntax _removeNewtonsoftUsings(
      CompilationUnitSyntax compilationUnit,
      List<CodeChange> changes) {
    var newUsings = new List<UsingDirectiveSyntax>();

    foreach (var usingDirective in compilationUnit.Usings) {
      var name = usingDirective.Name?.ToString();
      if (name?.StartsWith("Newtonsoft.Json", StringComparison.Ordinal) == true) {
        changes.Add(new CodeChange(
            usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            ChangeType.UsingRemoved,
            $"Removed unused 'using {name}'",
            $"using {name};",
            ""));
      } else {
        newUsings.Add(usingDirective);
      }
    }

    return compilationUnit.WithUsings(SyntaxFactory.List(newUsings));
  }

  /// <summary>
  /// Transforms Newtonsoft.Json usage to System.Text.Json.
  /// </summary>
  private CompilationUnitSyntax _transformNewtonsoftUsage(
      CompilationUnitSyntax compilationUnit,
      List<CodeChange> changes,
      List<string> warnings) {
    // First, transform the using directives
    var (newUsings, addedStjUsing) = _transformUsings(compilationUnit.Usings, changes, warnings);

    // Then transform the code
    var rewriter = new NewtonsoftRewriter(changes, warnings, _addTodoForUnsupported);
    var newRoot = (CompilationUnitSyntax)rewriter.Visit(compilationUnit);

    // Apply transformed usings
    newRoot = newRoot.WithUsings(SyntaxFactory.List(newUsings));

    // Add System.Text.Json.Serialization using if needed and not already present
    if (rewriter.NeedsSerializationUsing && !addedStjUsing) {
      var stjUsing = SyntaxFactory.UsingDirective(
          SyntaxFactory.ParseName("System.Text.Json.Serialization"))
          .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

      var usings = newRoot.Usings.ToList();
      // Find a good insertion point (after System.* usings)
      var insertIndex = usings.FindLastIndex(u =>
          u.Name?.ToString().StartsWith("System", StringComparison.Ordinal) == true);
      if (insertIndex >= 0) {
        usings.Insert(insertIndex + 1, stjUsing);
      } else {
        usings.Insert(0, stjUsing);
      }
      newRoot = newRoot.WithUsings(SyntaxFactory.List(usings));

      changes.Add(new CodeChange(
          1,
          ChangeType.UsingAdded,
          "Added 'using System.Text.Json.Serialization' for STJ attributes",
          "",
          "using System.Text.Json.Serialization;"));
    }

    return newRoot;
  }

  /// <summary>
  /// Transforms using directives from Newtonsoft to System.Text.Json.
  /// </summary>
  private static (List<UsingDirectiveSyntax> usings, bool addedStj) _transformUsings(
      SyntaxList<UsingDirectiveSyntax> originalUsings,
      List<CodeChange> changes,
      List<string> warnings) {
    var newUsings = new List<UsingDirectiveSyntax>();
    var addedStjSerialization = false;

    foreach (var usingDirective in originalUsings) {
      var name = usingDirective.Name?.ToString();

      switch (name) {
        case "Newtonsoft.Json":
          // Replace with System.Text.Json.Serialization
          if (!addedStjSerialization) {
            var stjUsing = usingDirective
                .WithName(SyntaxFactory.ParseName("System.Text.Json.Serialization")
                    .WithLeadingTrivia(usingDirective.Name?.GetLeadingTrivia() ?? SyntaxFactory.TriviaList())
                    .WithTrailingTrivia(usingDirective.Name?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList()));
            newUsings.Add(stjUsing);
            addedStjSerialization = true;

            changes.Add(new CodeChange(
                usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                ChangeType.UsingReplaced,
                "Replaced 'using Newtonsoft.Json' with 'using System.Text.Json.Serialization'",
                "using Newtonsoft.Json;",
                "using System.Text.Json.Serialization;"));
          }
          break;

        case "Newtonsoft.Json.Linq":
          // JObject/JArray/JToken - needs manual review
          warnings.Add($"JObject/JArray/JToken from Newtonsoft.Json.Linq requires manual migration to JsonDocument/JsonElement");
          // Remove the using
          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              "Removed 'using Newtonsoft.Json.Linq' - requires manual migration to System.Text.Json",
              "using Newtonsoft.Json.Linq;",
              "// TODO: Migrate JObject/JArray to JsonDocument/JsonElement"));
          break;

        case "Newtonsoft.Json.Schema":
        case "Newtonsoft.Json.Schema.Generation":
          // JSON Schema generation - no direct STJ equivalent
          warnings.Add("JSON Schema generation (Newtonsoft.Json.Schema) has no System.Text.Json equivalent. Consider NJsonSchema package.");
          // Keep the using but warn
          newUsings.Add(usingDirective);
          break;

        case "Newtonsoft.Json.Converters":
          // Converters - some may have STJ equivalents, needs review
          warnings.Add("Newtonsoft.Json.Converters may need manual migration to System.Text.Json.Serialization converters");
          changes.Add(new CodeChange(
              usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
              ChangeType.UsingRemoved,
              "Removed 'using Newtonsoft.Json.Converters' - migrate converters manually",
              "using Newtonsoft.Json.Converters;",
              ""));
          break;

        default:
          if (name?.StartsWith("Newtonsoft.Json", StringComparison.Ordinal) == true) {
            // Other Newtonsoft namespaces - remove with warning
            warnings.Add($"Removed unknown Newtonsoft namespace: {name}");
            changes.Add(new CodeChange(
                usingDirective.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                ChangeType.UsingRemoved,
                $"Removed 'using {name}'",
                $"using {name};",
                ""));
          } else {
            newUsings.Add(usingDirective);
          }
          break;
      }
    }

    return (newUsings, addedStjSerialization);
  }

  /// <summary>
  /// Syntax rewriter that transforms Newtonsoft patterns to System.Text.Json.
  /// </summary>
  private sealed class NewtonsoftRewriter : CSharpSyntaxRewriter {
    private readonly List<CodeChange> _changes;
    private readonly List<string> _warnings;
    private readonly bool _addTodoForUnsupported;

    public bool NeedsSerializationUsing { get; private set; }

    public NewtonsoftRewriter(
        List<CodeChange> changes,
        List<string> warnings,
        bool addTodoForUnsupported) {
      _changes = changes;
      _warnings = warnings;
      _addTodoForUnsupported = addTodoForUnsupported;
    }

    public override SyntaxNode? VisitAttribute(AttributeSyntax node) {
      var name = _getAttributeName(node);

      switch (name) {
        case "JsonProperty":
        case "JsonPropertyAttribute": {
            // Check if it's just Required = Required.Always
            var arguments = node.ArgumentList?.Arguments.ToList() ?? [];

            if (arguments.Count == 1) {
              var arg = arguments[0];
              var argText = arg.ToString();

              // [JsonProperty(Required = Required.Always)] → [JsonRequired]
              if (argText.Contains("Required") && argText.Contains("Always")) {
                NeedsSerializationUsing = true;
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                _changes.Add(new CodeChange(
                    line,
                    ChangeType.AttributeReplaced,
                    "Replaced [JsonProperty(Required = Required.Always)] with [JsonRequired]",
                    node.ToString(),
                    "[JsonRequired]"));

                return SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("JsonRequired"))
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());
              }

              // [JsonProperty("name")] → [JsonPropertyName("name")]
              if (arg.NameEquals == null && arg.Expression is LiteralExpressionSyntax literal) {
                NeedsSerializationUsing = true;
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var newAttr = $"[JsonPropertyName({literal})]";
                _changes.Add(new CodeChange(
                    line,
                    ChangeType.AttributeReplaced,
                    $"Replaced [JsonProperty(\"{literal}\")] with [JsonPropertyName(\"{literal}\")]",
                    node.ToString(),
                    newAttr));

                return SyntaxFactory.Attribute(
                    SyntaxFactory.IdentifierName("JsonPropertyName"),
                    SyntaxFactory.AttributeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.AttributeArgument(literal))))
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia());
              }
            }

            // Complex JsonProperty - needs manual review
            if (_addTodoForUnsupported) {
              _warnings.Add($"Complex [JsonProperty] at line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} needs manual migration");
            }
            break;
          }

        case "JsonIgnore":
        case "JsonIgnoreAttribute":
          // [JsonIgnore] is the same in both
          NeedsSerializationUsing = true;
          return node;

        case "JsonConverter":
        case "JsonConverterAttribute":
          // [JsonConverter(typeof(...))] - similar syntax but different converters
          _warnings.Add($"[JsonConverter] at line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} needs manual migration - converter types differ");
          return node;

        case "JsonExtensionData":
        case "JsonExtensionDataAttribute":
          // [JsonExtensionData] is the same in both
          NeedsSerializationUsing = true;
          return node;

        case "JsonConstructor":
        case "JsonConstructorAttribute":
          // [JsonConstructor] is the same in both
          NeedsSerializationUsing = true;
          return node;
      }

      return base.VisitAttribute(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node) {
      var expr = node.Expression.ToString();

      // JsonConvert.SerializeObject → JsonSerializer.Serialize
      if (expr == "JsonConvert.SerializeObject") {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _changes.Add(new CodeChange(
            line,
            ChangeType.MethodReplaced,
            "Replaced JsonConvert.SerializeObject with JsonSerializer.Serialize",
            node.ToString(),
            $"JsonSerializer.Serialize({node.ArgumentList})"));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("JsonSerializer"),
                SyntaxFactory.IdentifierName("Serialize")),
            node.ArgumentList)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());
      }

      // JsonConvert.DeserializeObject<T> → JsonSerializer.Deserialize<T>
      if (expr.StartsWith("JsonConvert.DeserializeObject", StringComparison.Ordinal)) {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Handle generic version
        if (node.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name is GenericNameSyntax genericName) {
          var newExpr = SyntaxFactory.InvocationExpression(
              SyntaxFactory.MemberAccessExpression(
                  SyntaxKind.SimpleMemberAccessExpression,
                  SyntaxFactory.IdentifierName("JsonSerializer"),
                  SyntaxFactory.GenericName("Deserialize")
                      .WithTypeArgumentList(genericName.TypeArgumentList)),
              node.ArgumentList);

          _changes.Add(new CodeChange(
              line,
              ChangeType.MethodReplaced,
              "Replaced JsonConvert.DeserializeObject<T> with JsonSerializer.Deserialize<T>",
              node.ToString(),
              newExpr.ToString()));

          return newExpr
              .WithLeadingTrivia(node.GetLeadingTrivia())
              .WithTrailingTrivia(node.GetTrailingTrivia());
        }
      }

      // JObject/JArray/JToken methods - warn for manual migration
      if (expr.Contains("JObject.") || expr.Contains("JArray.") || expr.Contains("JToken.")) {
        _warnings.Add($"JObject/JArray/JToken usage at line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1} requires manual migration to JsonDocument/JsonElement");
      }

      return base.VisitInvocationExpression(node);
    }
  }
}
