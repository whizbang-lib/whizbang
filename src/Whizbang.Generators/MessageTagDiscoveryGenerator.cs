using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Source generator that discovers message types with MessageTagAttribute (or subclasses)
/// and generates a MessageTagRegistry for AOT-compatible tag discovery.
/// Also generates MessageTagHookDispatcher for custom attribute types to enable AOT-compatible hook invocation.
/// </summary>
/// <docs>fundamentals/messages/message-tags#registry</docs>
/// <tests>Whizbang.Generators.Tests/MessageTagDiscoveryGeneratorTests.cs</tests>
[Generator]
public class MessageTagDiscoveryGenerator : IIncrementalGenerator {
  private const string MESSAGE_TAG_ATTRIBUTE = "Whizbang.Core.Attributes.MessageTagAttribute";
  private const string XML_DOC_SUMMARY_OPEN = "/// <summary>";
  private const string XML_DOC_SUMMARY_CLOSE = "/// </summary>";
  private const string XML_DOC_SUMMARY_OPEN_INDENTED = "  /// <summary>";
  private const string XML_DOC_SUMMARY_CLOSE_INDENTED = "  /// </summary>";

  // Built-in attribute types that are handled directly by MessageTagProcessor
  // Custom attributes (those not in this set) require generated dispatchers
  private static readonly HashSet<string> _builtInAttributeTypes = [
    "global::Whizbang.Core.Attributes.MessageTagAttribute",
    "global::Whizbang.Core.Attributes.SignalTagAttribute",
    "global::Whizbang.Core.Attributes.TelemetryTagAttribute",
    "global::Whizbang.Core.Attributes.MetricTagAttribute",
  ];

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with [MessageTag] or derived attributes
    // FIX: Use SelectMany to flatten multiple MessageTagInfo per type
    // This allows events with multiple tag attributes to have ALL attributes registered
    var taggedTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => _extractTagInfos(ctx, ct)
    ).SelectMany(static (infos, _) => infos);

    // Combine with assembly name to generate unique class names per assembly
    var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Unknown");

    // Generate registry with unique class name per assembly
    context.RegisterSourceOutput(
        taggedTypes.Collect().Combine(assemblyName),
        static (ctx, data) => _generateRegistry(ctx, data.Left!, data.Right)
    );
  }

  /// <summary>
  /// Extracts MessageTagInfo for ALL tag attributes on a type.
  /// FIX: Previously used FirstOrDefault which only discovered the first attribute.
  /// Now uses Where to discover ALL MessageTagAttribute subclasses on each type.
  /// </summary>
  private static IEnumerable<MessageTagInfo> _extractTagInfos(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var typeDecl = (TypeDeclarationSyntax)context.Node;
    var typeSymbol = context.SemanticModel.GetDeclaredSymbol(typeDecl, ct);

    if (typeSymbol is null) {
      yield break;
    }

    // Only process public types to avoid discovering test types
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      yield break;
    }

    // FIX: Find ALL MessageTagAttribute or derived attributes (not just FirstOrDefault!)
    var tagAttributes = typeSymbol.GetAttributes()
        .Where(a => _inheritsFromMessageTagAttribute(a.AttributeClass));

    // Get type information (shared across all attributes)
    var typeFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Get property names from the type for payload extraction (shared across all attributes)
    // Uses shared utility to include inherited properties from base classes
    var typeProperties = typeSymbol.GetAllPublicPropertyNames();

    // Yield a MessageTagInfo for EACH tag attribute
    foreach (var tagAttribute in tagAttributes) {
      // Extract attribute properties using shared utilities
      var tag = AttributeUtilities.GetStringValue(tagAttribute, "Tag") ?? "";
      var properties = AttributeUtilities.GetStringArrayValue(tagAttribute, "Properties");
      var extraJson = AttributeUtilities.GetStringValue(tagAttribute, "ExtraJson");

      // Skip attributes with Exclude = true (e.g., system events that shouldn't trigger tag hooks)
      var exclude = AttributeUtilities.GetBoolValue(tagAttribute, "Exclude", false);
      if (exclude) {
        continue;
      }

      var attributeFullName = tagAttribute.AttributeClass!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

      // Capture additional named arguments declared on the attribute (e.g., `Scope`) so
      // the emitted AttributeFactory can faithfully reconstruct the attribute instance.
      // Without this, init-only properties like NotificationTagAttribute.Scope silently
      // fall back to their CLR default, which has bitten us before (bulk-job notifications
      // routing as User-scope when declared Tenant).
      var extraInitializers = _extractExtraNamedInitializers(tagAttribute);

      // Capture POSITIONAL constructor arguments past index 0 (which is always `Tag` and
      // already emitted directly). Map ctor parameter names to property names via the
      // attribute's [AttributeArgNaming] convention (default PascalCase). Without this,
      // attribute classes like NotificationTagAttribute that store positional args in
      // init-only properties (TagValue, PropertyName) lose those values when the generator
      // falls back to the parameterless ctor — and downstream hooks emit null tag values.
      var positionalInitializers = _extractPositionalArgInitializers(tagAttribute);
      if (positionalInitializers.Length > 0) {
        var combined = new string[extraInitializers.Length + positionalInitializers.Length];
        extraInitializers.CopyTo(combined, 0);
        positionalInitializers.CopyTo(combined, extraInitializers.Length);
        extraInitializers = combined;
      }

      yield return new MessageTagInfo(
          TypeFullName: typeFullName,
          TypeName: typeSymbol.Name,
          Namespace: typeSymbol.ContainingNamespace?.ToDisplayString() ?? "",
          AttributeFullName: attributeFullName,
          AttributeName: tagAttribute.AttributeClass!.Name,
          Tag: tag,
          Properties: properties,
          ExtraJson: extraJson,
          TypeProperties: typeProperties,
          ExtraInitializers: extraInitializers
      );
    }
  }

  /// <summary>
  /// Collects named arguments from an attribute declaration other than those already
  /// handled via dedicated MessageTagInfo fields (Tag / Properties / ExtraJson / Exclude).
  /// Each entry becomes an object-initializer assignment in the emitted AttributeFactory
  /// so init-only and regular properties on the attribute are preserved at runtime.
  /// </summary>
  private static string[] _extractExtraNamedInitializers(AttributeData attributeData) {
    if (attributeData.NamedArguments.IsDefaultOrEmpty) {
      return [];
    }

    var result = new List<string>(attributeData.NamedArguments.Length);
    foreach (var kvp in attributeData.NamedArguments) {
      // Skip fields already handled via dedicated MessageTagInfo slots.
      if (kvp.Key is "Tag" or "Properties" or "ExtraJson" or "Exclude") {
        continue;
      }

      var literal = _typedConstantToCSharpLiteral(kvp.Value);
      if (literal is null) {
        continue; // Unsupported value kind — skip rather than emit invalid C#.
      }

      result.Add($"{kvp.Key} = {literal}");
    }

    return [.. result];
  }

  /// <summary>
  /// Reads the <c>[AttributeArgNaming]</c> attribute on <paramref name="attributeClass"/>
  /// (or its base classes) to determine the constructor-parameter → property naming
  /// convention. Defaults to <see cref="AttributeArgNamingConvention.PascalCase"/> when
  /// no attribute is present — covers the common C# convention where parameter
  /// <c>tagValue</c> initializes property <c>TagValue</c>.
  /// </summary>
  private static AttributeArgNamingConvention _resolveNamingConvention(INamedTypeSymbol? attributeClass) {
    var current = attributeClass;
    while (current is not null) {
      var conventionAttr = current.GetAttributes().FirstOrDefault(a =>
          a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
              == "global::Whizbang.Core.Attributes.AttributeArgNamingAttribute");
      if (conventionAttr is not null && conventionAttr.ConstructorArguments.Length > 0) {
        var rawValue = conventionAttr.ConstructorArguments[0].Value;
        if (rawValue is int intValue) {
          return (AttributeArgNamingConvention)intValue;
        }
      }
      current = current.BaseType;
    }
    return AttributeArgNamingConvention.PascalCase;
  }

  /// <summary>
  /// Walks the positional constructor arguments on <paramref name="attributeData"/> past
  /// index 0 (which is always <c>Tag</c> and emitted separately). For each, maps the ctor
  /// parameter name to a property initializer using the attribute's declared naming
  /// convention. Returns an array of <c>"PropertyName = literal"</c> strings ready to be
  /// concatenated into the AttributeFactory's object initializer.
  /// </summary>
  private static string[] _extractPositionalArgInitializers(AttributeData attributeData) {
    if (attributeData.ConstructorArguments.IsDefaultOrEmpty || attributeData.AttributeConstructor is null) {
      return [];
    }

    var ctorParams = attributeData.AttributeConstructor.Parameters;
    if (ctorParams.IsDefaultOrEmpty || ctorParams.Length <= 1) {
      return [];
    }

    var convention = _resolveNamingConvention(attributeData.AttributeClass);
    var ctorArgs = attributeData.ConstructorArguments;
    var paramCount = System.Math.Min(ctorArgs.Length, ctorParams.Length);
    var result = new List<string>(paramCount - 1);

    for (var i = 1; i < paramCount; i++) {
      var paramName = ctorParams[i].Name;
      if (string.IsNullOrEmpty(paramName)) {
        continue;
      }
      var propertyName = AttributeArgNamingHelper.Convert(paramName, convention);
      var literal = _typedConstantToCSharpLiteral(ctorArgs[i]);
      if (literal is null) {
        continue; // Skip unsupported kinds rather than emit invalid C#.
      }
      result.Add($"{propertyName} = {literal}");
    }
    return [.. result];
  }

  /// <summary>
  /// Converts a Roslyn <see cref="TypedConstant"/> into a valid C# literal expression so
  /// it can be inlined into generated code. Handles the kinds that appear in attribute
  /// named arguments (primitive, string, enum, type, array). Returns null for unsupported
  /// kinds to let callers drop them safely.
  /// </summary>
  private static string? _typedConstantToCSharpLiteral(TypedConstant value) {
    if (value.IsNull) {
      return "null";
    }

    switch (value.Kind) {
      case TypedConstantKind.Primitive:
        return value.Value switch {
          string s => $"\"{_escapeString(s)}\"",
          bool b => b ? "true" : "false",
          char c => $"'{c}'",
          null => "null",
          _ => value.Value.ToString()
        };
      case TypedConstantKind.Enum:
        // Emit as ((EnumType)underlyingValue) — always compiles even for [Flags] combinations.
        var enumTypeName = value.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "int";
        return $"({enumTypeName})({value.Value})";
      case TypedConstantKind.Type:
        var t = value.Value as ITypeSymbol;
        return t is null ? null : $"typeof({t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})";
      case TypedConstantKind.Array:
        var elementType = (value.Type as IArrayTypeSymbol)?.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (elementType is null) { return null; }
        var elements = value.Values
            .Select(_typedConstantToCSharpLiteral)
            .Where(e => e is not null)
            .ToArray();
        return $"new {elementType}[] {{ {string.Join(", ", elements)} }}";
      default:
        return null;
    }
  }

  private static bool _inheritsFromMessageTagAttribute(INamedTypeSymbol? attributeClass) {
    if (attributeClass is null) {
      return false;
    }

    // Check if the attribute is MessageTagAttribute or inherits from it
    var current = attributeClass;
    while (current is not null) {
      if (current.ToDisplayString() == MESSAGE_TAG_ATTRIBUTE) {
        return true;
      }
      current = current.BaseType;
    }

    return false;
  }

  private static void _generateRegistry(
      SourceProductionContext context,
      ImmutableArray<MessageTagInfo?> tags,
      string assemblyName) {

    var validTags = tags.Where(t => t is not null).Select(t => t!).ToList();

    // Create unique class name based on assembly (sanitize for C# identifier)
    var sanitizedAssemblyName = _sanitizeIdentifier(assemblyName);
    var className = $"GeneratedMessageTagRegistry_{sanitizedAssemblyName}";
    var initializerClassName = $"MessageTagRegistryInitializer_{sanitizedAssemblyName}";

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Collections.Generic;");
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using System.Text.Json;");
    sb.AppendLine("using Whizbang.Core.Tags;");
    sb.AppendLine("using Whizbang.Core.Attributes;");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();
    sb.AppendLine("// Tag payload builders serialize an ad-hoc property bag (Dictionary<string, object?>) for");
    sb.AppendLine("// notification-facing hook payloads — the one shape a JsonSerializerContext cannot pre-compute.");
    sb.AppendLine("// The lambdas reference every extracted property statically, so the members survive trimming;");
    sb.AppendLine("// only the last-mile object-bag serialize call is unprovable to the analyzer. Same pattern every");
    sb.AppendLine("// consumer assembly already ships; suppressed so trim-analyzed assemblies compile identically.");
    sb.AppendLine("#pragma warning disable IL2026 // RequiresUnreferencedCode: JsonSerializer.SerializeToElement over the property bag");
    sb.AppendLine("#pragma warning disable IL3050 // RequiresDynamicCode: same call, AOT flavor");
    sb.AppendLine();
    sb.AppendLine(XML_DOC_SUMMARY_OPEN);
    sb.AppendLine("/// Auto-generated registry of message types with tag attributes.");
    sb.AppendLine("/// Implements <see cref=\"IMessageTagRegistry\"/> for AOT-compatible tag discovery.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE);
    sb.AppendLine("/// <remarks>");
    sb.AppendLine("/// This registry is automatically registered via [ModuleInitializer] before Main() runs.");
    sb.AppendLine("/// No manual registration is required.");
    sb.AppendLine("/// </remarks>");
    sb.AppendLine($"internal sealed class {className} : IMessageTagRegistry {{");
    sb.AppendLine(XML_DOC_SUMMARY_OPEN_INDENTED);
    sb.AppendLine("  /// Singleton instance of the generated registry.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE_INDENTED);
    sb.AppendLine($"  internal static readonly {className} Instance = new();");
    sb.AppendLine();
    sb.AppendLine(XML_DOC_SUMMARY_OPEN_INDENTED);
    sb.AppendLine("  /// All registered message tag entries.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE_INDENTED);
    sb.AppendLine("  private static readonly MessageTagRegistration[] _tags = new MessageTagRegistration[] {");

    foreach (var tag in validTags) {
      _generateRegistration(sb, tag);
    }

    sb.AppendLine("  };");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {");
    sb.AppendLine("    foreach (var tag in _tags) {");
    sb.AppendLine("      if (tag.MessageType == messageType) {");
    sb.AppendLine("        yield return tag;");
    sb.AppendLine("      }");
    sb.AppendLine("    }");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public IEnumerable<MessageTagRegistration> GetAllTags() => _tags;");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine(XML_DOC_SUMMARY_OPEN);
    sb.AppendLine("/// Auto-registers the generated message tag registry with the assembly registry.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE);
    sb.AppendLine($"internal static class {initializerClassName} {{");
    sb.AppendLine(XML_DOC_SUMMARY_OPEN_INDENTED);
    sb.AppendLine("  /// Module initializer that registers the tag registry.");
    sb.AppendLine("  /// Called automatically before any code in the assembly runs.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE_INDENTED);
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine("    // Register with priority 100 (contracts assemblies are tried first)");
    sb.AppendLine($"    Whizbang.Core.Tags.MessageTagRegistry.Register({className}.Instance, priority: 100);");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore IL3050");
    sb.AppendLine("#pragma warning restore IL2026");

    context.AddSource("MessageTagRegistry.g.cs", sb.ToString());

    // Generate dispatcher for custom attribute types (non-built-in)
    _generateDispatcher(context, validTags, sanitizedAssemblyName);
  }

  /// <summary>
  /// Generates a MessageTagHookDispatcher for custom (non-built-in) attribute types.
  /// This enables AOT-compatible hook invocation without reflection.
  /// </summary>
  private static void _generateDispatcher(
      SourceProductionContext context,
      List<MessageTagInfo> tags,
      string sanitizedAssemblyName) {

    // Collect unique custom attribute types (non-built-in)
    var customAttributeTypes = tags
        .Select(t => t.AttributeFullName)
        .Where(a => !_builtInAttributeTypes.Contains(a))
        .Distinct()
        .OrderBy(a => a) // Deterministic ordering for consistent output
        .ToList();

    // Only generate dispatcher if there are custom attributes
    if (customAttributeTypes.Count == 0) {
      return;
    }

    var className = $"GeneratedMessageTagHookDispatcher_{sanitizedAssemblyName}";
    var initializerClassName = $"MessageTagHookDispatcherInitializer_{sanitizedAssemblyName}";

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Collections.Generic;");
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using System.Text.Json;");
    sb.AppendLine("using System.Threading;");
    sb.AppendLine("using System.Threading.Tasks;");
    sb.AppendLine("using Whizbang.Core.Messaging;");
    sb.AppendLine("using Whizbang.Core.Security;");
    sb.AppendLine("using Whizbang.Core.Tags;");
    sb.AppendLine("using Whizbang.Core.Attributes;");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();
    sb.AppendLine(XML_DOC_SUMMARY_OPEN);
    sb.AppendLine("/// Auto-generated dispatcher for custom MessageTagAttribute types.");
    sb.AppendLine("/// Implements <see cref=\"IMessageTagHookDispatcher\"/> for AOT-compatible hook invocation.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE);
    sb.AppendLine("/// <remarks>");
    sb.AppendLine("/// This dispatcher handles the following custom attribute types:");
    foreach (var attrType in customAttributeTypes) {
      sb.AppendLine($"/// <list type=\"bullet\"><item><see cref=\"{attrType.Replace("global::", "")}\"/></item></list>");
    }
    sb.AppendLine("/// </remarks>");
    sb.AppendLine($"internal sealed class {className} : IMessageTagHookDispatcher {{");
    sb.AppendLine(XML_DOC_SUMMARY_OPEN_INDENTED);
    sb.AppendLine("  /// Singleton instance of the generated dispatcher.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE_INDENTED);
    sb.AppendLine($"  internal static readonly {className} Instance = new();");
    sb.AppendLine();

    // Generate TryCreateContext method
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public object? TryCreateContext(");
    sb.AppendLine("      Type attributeType,");
    sb.AppendLine("      MessageTagAttribute attribute,");
    sb.AppendLine("      object message,");
    sb.AppendLine("      Type messageType,");
    sb.AppendLine("      JsonElement payload,");
    sb.AppendLine("      IScopeContext? scope,");
    sb.AppendLine("      LifecycleStage stage) {");
    sb.AppendLine();

    foreach (var attrType in customAttributeTypes) {
      sb.AppendLine($"    if (attributeType == typeof({attrType})) {{");
      sb.AppendLine($"      return new TagContext<{attrType}> {{");
      sb.AppendLine($"        Attribute = ({attrType})attribute,");
      sb.AppendLine("        Message = message,");
      sb.AppendLine("        MessageType = messageType,");
      sb.AppendLine("        Payload = payload,");
      sb.AppendLine("        Scope = scope,");
      sb.AppendLine("        Stage = stage,");
      sb.AppendLine("      };");
      sb.AppendLine("    }");
      sb.AppendLine();
    }

    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine();

    // Generate TryDispatchAsync method
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public async ValueTask<JsonElement?> TryDispatchAsync(");
    sb.AppendLine("      object hookInstance,");
    sb.AppendLine("      object context,");
    sb.AppendLine("      Type attributeType,");
    sb.AppendLine("      CancellationToken ct) {");
    sb.AppendLine();

    foreach (var attrType in customAttributeTypes) {
      var id = _sanitizeIdentifier(attrType);
      sb.AppendLine($"    if (attributeType == typeof({attrType}) &&");
      sb.AppendLine($"        hookInstance is IMessageTagHook<{attrType}> hook_{id} &&");
      sb.AppendLine($"        context is TagContext<{attrType}> ctx_{id}) {{");
      sb.AppendLine("      // Establish ambient scope from TagContext so hooks can access ScopeContextAccessor.CurrentContext");
      sb.AppendLine($"      if (ctx_{id}.Scope is not null) {{");
      sb.AppendLine($"        ScopeContextAccessor.CurrentContext = ctx_{id}.Scope;");
      sb.AppendLine("      }");
      sb.AppendLine($"      return await hook_{id}.OnTaggedMessageAsync(ctx_{id}, ct);");
      sb.AppendLine("    }");
      sb.AppendLine();
    }

    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();

    // Generate module initializer
    sb.AppendLine(XML_DOC_SUMMARY_OPEN);
    sb.AppendLine("/// Auto-registers the generated message tag hook dispatcher with the registry.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE);
    sb.AppendLine($"internal static class {initializerClassName} {{");
    sb.AppendLine(XML_DOC_SUMMARY_OPEN_INDENTED);
    sb.AppendLine("  /// Module initializer that registers the hook dispatcher.");
    sb.AppendLine("  /// Called automatically before any code in the assembly runs.");
    sb.AppendLine(XML_DOC_SUMMARY_CLOSE_INDENTED);
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine("    // Register with priority 100 (contracts assemblies are tried first)");
    sb.AppendLine($"    MessageTagHookDispatcherRegistry.Register({className}.Instance, priority: 100);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("MessageTagHookDispatcher.g.cs", sb.ToString());
  }

  private static void _generateRegistration(StringBuilder sb, MessageTagInfo tag) {
    sb.AppendLine("    new MessageTagRegistration {");
    sb.AppendLine($"      MessageType = typeof({tag.TypeFullName}),");
    sb.AppendLine($"      AttributeType = typeof({tag.AttributeFullName}),");
    sb.AppendLine($"      Tag = \"{_escapeString(tag.Tag)}\",");

    if (tag.Properties is { Length: > 0 }) {
      sb.AppendLine($"      Properties = new[] {{ {string.Join(", ", tag.Properties.Select(p => $"\"{p}\""))} }},");
    }

    if (!string.IsNullOrEmpty(tag.ExtraJson)) {
      sb.AppendLine($"      ExtraJson = \"\"\"{_escapeString(tag.ExtraJson)}\"\"\",");
    }

    // Generate PayloadBuilder
    sb.AppendLine("      PayloadBuilder = msg => {");
    sb.AppendLine($"        var e = ({tag.TypeFullName})msg;");
    sb.AppendLine("        var dict = new Dictionary<string, object?>();");

    // Extract exactly the Properties declared on the attribute. When Properties is null
    // (not specified), fall back to all public properties on the type; when it's an
    // explicit empty array the caller opted out of field extraction entirely.
    var propsToExtract = tag.Properties ?? tag.TypeProperties;

    // S3267: Loop has side effects (appending to StringBuilder) — LINQ not appropriate
#pragma warning disable S3267
    foreach (var prop in propsToExtract) {
      if (tag.TypeProperties.Contains(prop)) {
        sb.AppendLine($"        dict[\"{prop}\"] = e.{prop};");
      }
    }
#pragma warning restore S3267

    // Merge extra JSON if present
    if (!string.IsNullOrEmpty(tag.ExtraJson)) {
      sb.AppendLine($"        // Merge extra JSON: {_escapeString(tag.ExtraJson)}");
      sb.AppendLine($"        var extra = JsonDocument.Parse(\"\"\"{_escapeString(tag.ExtraJson)}\"\"\");");
      sb.AppendLine("        foreach (var prop in extra.RootElement.EnumerateObject()) {");
      sb.AppendLine("          dict[prop.Name] = prop.Value.Clone();");
      sb.AppendLine("        }");
    }

    sb.AppendLine("        return JsonSerializer.SerializeToElement(dict);");
    sb.AppendLine("      },");

    // Generate AttributeFactory — include any extra named arguments declared on the
    // attribute (e.g., Scope = NotificationScope.Tenant, custom flags) so the reconstructed
    // attribute matches what was declared on the event. Without this, init-only properties
    // silently default and downstream consumers misroute.
    var initializerEntries = new List<string>(1 + tag.ExtraInitializers.Length) {
      $"Tag = \"{_escapeString(tag.Tag)}\""
    };
    initializerEntries.AddRange(tag.ExtraInitializers);
    var initializers = string.Join(", ", initializerEntries);
    sb.AppendLine($"      AttributeFactory = () => new {tag.AttributeFullName}() {{ {initializers} }}");
    sb.AppendLine("    },");
  }

  private static string _escapeString(string? s) {
    if (s is null) {
      return "";
    }

    return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }

  private static string _sanitizeIdentifier(string name) {
    // Replace dots and hyphens with underscores, remove other invalid chars
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (char.IsLetterOrDigit(c) || c == '_') {
        sb.Append(c);
      } else if (c == '.' || c == '-') {
        sb.Append('_');
      }
    }
    return sb.ToString();
  }
}

/// <summary>
/// Value type record for caching discovered message tag information.
/// </summary>
internal sealed record MessageTagInfo(
    string TypeFullName,
    string TypeName,
    string Namespace,
    string AttributeFullName,
    string AttributeName,
    string Tag,
    string[]? Properties,
    string? ExtraJson,
    string[] TypeProperties,
    string[] ExtraInitializers
);
