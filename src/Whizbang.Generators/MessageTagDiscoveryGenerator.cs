using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Source generator that discovers message types with MessageTagAttribute (or subclasses)
/// and generates a MessageTagRegistry for AOT-compatible tag discovery.
/// Also generates MessageTagHookDispatcher for custom attribute types to enable AOT-compatible hook invocation.
/// </summary>
/// <docs>core-concepts/message-tags#registry</docs>
/// <tests>Whizbang.Generators.Tests/MessageTagDiscoveryGeneratorTests.cs</tests>
[Generator]
public class MessageTagDiscoveryGenerator : IIncrementalGenerator {
  private const string MESSAGE_TAG_ATTRIBUTE = "Whizbang.Core.Attributes.MessageTagAttribute";

  // Built-in attribute types that are handled directly by MessageTagProcessor
  // Custom attributes (those not in this set) require generated dispatchers
  private static readonly HashSet<string> _builtInAttributeTypes = new() {
    "global::Whizbang.Core.Attributes.MessageTagAttribute",
    "global::Whizbang.Core.Attributes.SignalTagAttribute",
    "global::Whizbang.Core.Attributes.TelemetryTagAttribute",
    "global::Whizbang.Core.Attributes.MetricTagAttribute",
  };

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
      var includeEvent = AttributeUtilities.GetBoolValue(tagAttribute, "IncludeEvent", false);
      var extraJson = AttributeUtilities.GetStringValue(tagAttribute, "ExtraJson");

      // Skip attributes with Exclude = true (e.g., system events that shouldn't trigger tag hooks)
      var exclude = AttributeUtilities.GetBoolValue(tagAttribute, "Exclude", false);
      if (exclude) {
        continue;
      }

      var attributeFullName = tagAttribute.AttributeClass!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

      yield return new MessageTagInfo(
          TypeFullName: typeFullName,
          TypeName: typeSymbol.Name,
          Namespace: typeSymbol.ContainingNamespace?.ToDisplayString() ?? "",
          AttributeFullName: attributeFullName,
          AttributeName: tagAttribute.AttributeClass!.Name,
          Tag: tag,
          Properties: properties,
          IncludeEvent: includeEvent,
          ExtraJson: extraJson,
          TypeProperties: typeProperties
      );
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
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-generated registry of message types with tag attributes.");
    sb.AppendLine("/// Implements <see cref=\"IMessageTagRegistry\"/> for AOT-compatible tag discovery.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("/// <remarks>");
    sb.AppendLine("/// This registry is automatically registered via [ModuleInitializer] before Main() runs.");
    sb.AppendLine("/// No manual registration is required.");
    sb.AppendLine("/// </remarks>");
    sb.AppendLine($"internal sealed class {className} : IMessageTagRegistry {{");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Singleton instance of the generated registry.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine($"  internal static readonly {className} Instance = new();");
    sb.AppendLine();
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// All registered message tag entries.");
    sb.AppendLine("  /// </summary>");
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
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-registers the generated message tag registry with the assembly registry.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine($"internal static class {initializerClassName} {{");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Module initializer that registers the tag registry.");
    sb.AppendLine("  /// Called automatically before any code in the assembly runs.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine("    // Register with priority 100 (contracts assemblies are tried first)");
    sb.AppendLine($"    Whizbang.Core.Tags.MessageTagRegistry.Register({className}.Instance, priority: 100);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

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
    sb.AppendLine("using Whizbang.Core.Tags;");
    sb.AppendLine("using Whizbang.Core.Attributes;");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-generated dispatcher for custom MessageTagAttribute types.");
    sb.AppendLine("/// Implements <see cref=\"IMessageTagHookDispatcher\"/> for AOT-compatible hook invocation.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("/// <remarks>");
    sb.AppendLine("/// This dispatcher handles the following custom attribute types:");
    foreach (var attrType in customAttributeTypes) {
      sb.AppendLine($"/// <list type=\"bullet\"><item><see cref=\"{attrType.Replace("global::", "")}\"/></item></list>");
    }
    sb.AppendLine("/// </remarks>");
    sb.AppendLine($"internal sealed class {className} : IMessageTagHookDispatcher {{");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Singleton instance of the generated dispatcher.");
    sb.AppendLine("  /// </summary>");
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
    sb.AppendLine("      IReadOnlyDictionary<string, object?>? scope) {");
    sb.AppendLine();

    foreach (var attrType in customAttributeTypes) {
      sb.AppendLine($"    if (attributeType == typeof({attrType})) {{");
      sb.AppendLine($"      return new TagContext<{attrType}> {{");
      sb.AppendLine($"        Attribute = ({attrType})attribute,");
      sb.AppendLine($"        Message = message,");
      sb.AppendLine($"        MessageType = messageType,");
      sb.AppendLine($"        Payload = payload,");
      sb.AppendLine($"        Scope = scope,");
      sb.AppendLine($"      }};");
      sb.AppendLine($"    }}");
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
      sb.AppendLine($"    if (attributeType == typeof({attrType}) &&");
      sb.AppendLine($"        hookInstance is IMessageTagHook<{attrType}> hook_{_sanitizeIdentifier(attrType)} &&");
      sb.AppendLine($"        context is TagContext<{attrType}> ctx_{_sanitizeIdentifier(attrType)}) {{");
      sb.AppendLine($"      return await hook_{_sanitizeIdentifier(attrType)}.OnTaggedMessageAsync(ctx_{_sanitizeIdentifier(attrType)}, ct);");
      sb.AppendLine($"    }}");
      sb.AppendLine();
    }

    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();

    // Generate module initializer
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-registers the generated message tag hook dispatcher with the registry.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine($"internal static class {initializerClassName} {{");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Module initializer that registers the hook dispatcher.");
    sb.AppendLine("  /// Called automatically before any code in the assembly runs.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine("    // Register with priority 100 (contracts assemblies are tried first)");
    sb.AppendLine($"    MessageTagHookDispatcherRegistry.Register({className}.Instance, priority: 100);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("MessageTagHookDispatcher.g.cs", sb.ToString());
  }

  private static void _generateRegistration(StringBuilder sb, MessageTagInfo tag) {
    sb.AppendLine($"    new MessageTagRegistration {{");
    sb.AppendLine($"      MessageType = typeof({tag.TypeFullName}),");
    sb.AppendLine($"      AttributeType = typeof({tag.AttributeFullName}),");
    sb.AppendLine($"      Tag = \"{_escapeString(tag.Tag)}\",");

    if (tag.Properties is not null && tag.Properties.Length > 0) {
      sb.AppendLine($"      Properties = new[] {{ {string.Join(", ", tag.Properties.Select(p => $"\"{p}\""))} }},");
    }

    sb.AppendLine($"      IncludeEvent = {(tag.IncludeEvent ? "true" : "false")},");

    if (!string.IsNullOrEmpty(tag.ExtraJson)) {
      sb.AppendLine($"      ExtraJson = \"\"\"{_escapeString(tag.ExtraJson)}\"\"\",");
    }

    // Generate PayloadBuilder
    sb.AppendLine($"      PayloadBuilder = msg => {{");
    sb.AppendLine($"        var e = ({tag.TypeFullName})msg;");
    sb.AppendLine($"        var dict = new Dictionary<string, object?>();");

    // Extract specified properties, or all properties if none specified
    var propsToExtract = tag.Properties is not null && tag.Properties.Length > 0
        ? tag.Properties
        : tag.TypeProperties;

    foreach (var prop in propsToExtract) {
      if (tag.TypeProperties.Contains(prop)) {
        sb.AppendLine($"        dict[\"{prop}\"] = e.{prop};");
      }
    }

    // Include full event if requested
    if (tag.IncludeEvent) {
      sb.AppendLine($"        dict[\"__event\"] = e;");
    }

    // Merge extra JSON if present
    if (!string.IsNullOrEmpty(tag.ExtraJson)) {
      sb.AppendLine($"        // Merge extra JSON: {_escapeString(tag.ExtraJson)}");
      sb.AppendLine($"        var extra = JsonDocument.Parse(\"\"\"{_escapeString(tag.ExtraJson)}\"\"\");");
      sb.AppendLine($"        foreach (var prop in extra.RootElement.EnumerateObject()) {{");
      sb.AppendLine($"          dict[prop.Name] = prop.Value.Clone();");
      sb.AppendLine($"        }}");
    }

    sb.AppendLine($"        return JsonSerializer.SerializeToElement(dict);");
    sb.AppendLine($"      }},");

    // Generate AttributeFactory
    sb.AppendLine($"      AttributeFactory = () => new {tag.AttributeFullName}() {{ Tag = \"{_escapeString(tag.Tag)}\" }}");
    sb.AppendLine($"    }},");
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
    bool IncludeEvent,
    string? ExtraJson,
    string[] TypeProperties
);
