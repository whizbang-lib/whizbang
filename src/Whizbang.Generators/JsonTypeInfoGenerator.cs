using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Source generator that creates a JsonSerializerContext with manual JsonTypeInfo objects
/// for AOT-compatible serialization. Discovers message types (ICommand, IEvent) and generates
/// WhizbangJsonContext with generic helper methods to minimize code repetition.
/// </summary>
[Generator]
public class JsonTypeInfoGenerator : IIncrementalGenerator {
  private const string I_COMMAND = "Whizbang.Core.ICommand";
  private const string I_EVENT = "Whizbang.Core.IEvent";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover message types (commands and events)
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 } ||
                                       node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractMessageTypeInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Generate WhizbangJsonContext from collected message types
    context.RegisterSourceOutput(
        messageTypes.Collect(),
        static (ctx, messages) => GenerateWhizbangJsonContext(ctx, messages!)
    );
  }

  /// <summary>
  /// Extracts message type information from syntax node using semantic analysis.
  /// Returns null if the node is not a message type (ICommand or IEvent).
  /// </summary>
  private static JsonMessageTypeInfo? ExtractMessageTypeInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    // Predicate guarantees node is RecordDeclarationSyntax or ClassDeclarationSyntax (both inherit from TypeDeclarationSyntax)
    // Defensive guard: throws if Roslyn returns null (indicates compiler bug)
    // See RoslynGuards.cs for rationale - no branch created, eliminates coverage gap
    var typeSymbol = RoslynGuards.GetTypeSymbolFromNode(context.Node, context.SemanticModel, ct);

    // Skip non-public types - generated code can't access them
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    // Check if implements ICommand or IEvent
    bool isCommand = typeSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{I_COMMAND}");

    bool isEvent = typeSymbol.AllInterfaces.Any(i =>
        i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{I_EVENT}");

    if (!isCommand && !isEvent) {
      return null;
    }

    var fullyQualifiedName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var simpleName = typeSymbol.Name;

    // Extract property information for JSON serialization
    var properties = typeSymbol.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
        .Select(p => new PropertyInfo(
            Name: p.Name,
            Type: p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        ))
        .ToArray();

    return new JsonMessageTypeInfo(
        FullyQualifiedName: fullyQualifiedName,
        SimpleName: simpleName,
        IsCommand: isCommand,
        IsEvent: isEvent,
        Properties: properties
    );
  }

  /// <summary>
  /// Generates WhizbangJsonContext.g.cs with JsonTypeInfo objects for all discovered message types
  /// and Whizbang core types (MessageId, CorrelationId, etc.).
  /// </summary>
  private static void GenerateWhizbangJsonContext(
      SourceProductionContext context,
      ImmutableArray<JsonMessageTypeInfo> messages) {

    // Report diagnostics for discovered message types
    foreach (var message in messages) {
      var messageKind = message.IsCommand ? "command" : "event";
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          message.SimpleName,
          messageKind
      ));
    }

    // Load template
    var assembly = typeof(JsonTypeInfoGenerator).Assembly;
    var template = TemplateUtilities.GetEmbeddedTemplate(assembly, "WhizbangJsonContextTemplate.cs");

    // Replace HEADER region with timestamp
    template = TemplateUtilities.ReplaceHeaderRegion(assembly, template);

    // Generate and replace each region
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_FIELDS", GenerateLazyFields(assembly, messages));
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_PROPERTIES", GenerateLazyProperties(assembly, messages));
    template = TemplateUtilities.ReplaceRegion(template, "GET_DISCOVERED_TYPE_INFO", GenerateGetTypeInfo(assembly, messages));
    template = TemplateUtilities.ReplaceRegion(template, "HELPER_METHODS", GenerateHelperMethods(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "CORE_TYPE_FACTORIES", GenerateCoreTypeFactories(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_TYPE_FACTORIES", GenerateMessageTypeFactories(assembly, messages));

    context.AddSource("WhizbangJsonContext.g.cs", template);
  }

  private static string GenerateLazyFields(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    // Core Whizbang types
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.MessageId>? _MessageId;");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.CorrelationId>? _CorrelationId;");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.Observability.MessageHop>? _MessageHop;");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.Observability.SecurityContext>? _SecurityContext;");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.Policies.PolicyDecisionTrail>? _PolicyDecisionTrail;");
    sb.AppendLine("private JsonTypeInfo<global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageHop>>? _ListMessageHop;");
    sb.AppendLine();

    // Discovered message types (ICommand, IEvent) - need JsonTypeInfo with property metadata
    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<{message.FullyQualifiedName}>? _{message.SimpleName};");
    }
    sb.AppendLine();

    // Message envelope types
    var snippet = TemplateUtilities.ExtractSnippet(assembly, "JsonTypeInfoSnippets.cs", "MESSAGE_ENVELOPE_LAZY_FIELD");
    foreach (var message in messages) {
      var field = snippet
          .Replace("__PAYLOAD_TYPE__", message.FullyQualifiedName)
          .Replace("__PAYLOAD_NAME__", message.SimpleName);
      sb.AppendLine(field.TrimEnd());
    }

    return sb.ToString();
  }

  private static string GenerateLazyProperties(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    // Core Whizbang types
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.MessageId> MessageId => _MessageId ??= Create_MessageId(Options);");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.CorrelationId> CorrelationId => _CorrelationId ??= Create_CorrelationId(Options);");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.Observability.MessageHop> MessageHop => _MessageHop ??= (JsonTypeInfo<global::Whizbang.Core.Observability.MessageHop>)Options.GetTypeInfo(typeof(global::Whizbang.Core.Observability.MessageHop));");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.Observability.SecurityContext> SecurityContext => _SecurityContext ??= (JsonTypeInfo<global::Whizbang.Core.Observability.SecurityContext>)Options.GetTypeInfo(typeof(global::Whizbang.Core.Observability.SecurityContext));");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.Policies.PolicyDecisionTrail> PolicyDecisionTrail => _PolicyDecisionTrail ??= (JsonTypeInfo<global::Whizbang.Core.Policies.PolicyDecisionTrail>)Options.GetTypeInfo(typeof(global::Whizbang.Core.Policies.PolicyDecisionTrail));");
    sb.AppendLine("private JsonTypeInfo<global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageHop>> ListMessageHop => _ListMessageHop ??= (JsonTypeInfo<global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageHop>>)Options.GetTypeInfo(typeof(global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageHop>));");
    sb.AppendLine();

    // Discovered message types (ICommand, IEvent) - generate complete JsonTypeInfo with property metadata
    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<{message.FullyQualifiedName}> {message.SimpleName} => _{message.SimpleName} ??= Create_{message.SimpleName}(Options);");
    }
    sb.AppendLine();

    // Message envelope types
    var snippet = TemplateUtilities.ExtractSnippet(assembly, "JsonTypeInfoSnippets.cs", "MESSAGE_ENVELOPE_LAZY_PROPERTY");
    foreach (var message in messages) {
      var property = snippet
          .Replace("__PAYLOAD_TYPE__", message.FullyQualifiedName)
          .Replace("__PAYLOAD_NAME__", message.SimpleName);
      sb.AppendLine(property.TrimEnd());
    }

    return sb.ToString();
  }

  private static string GenerateGetTypeInfo(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    sb.AppendLine("public override JsonTypeInfo? GetTypeInfo(Type type) {");

    // Core types
    sb.AppendLine("  // Core Whizbang types");
    sb.AppendLine("  if (type == typeof(global::Whizbang.Core.ValueObjects.MessageId)) return MessageId;");
    sb.AppendLine("  if (type == typeof(global::Whizbang.Core.ValueObjects.CorrelationId)) return CorrelationId;");
    sb.AppendLine("  if (type == typeof(global::Whizbang.Core.Observability.MessageHop)) return MessageHop;");
    sb.AppendLine("  if (type == typeof(global::Whizbang.Core.Observability.SecurityContext)) return SecurityContext;");
    sb.AppendLine("  if (type == typeof(global::Whizbang.Core.Policies.PolicyDecisionTrail)) return PolicyDecisionTrail;");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::Whizbang.Core.Observability.MessageHop>)) return ListMessageHop;");
    sb.AppendLine();

    // Discovered message types
    sb.AppendLine("  // Discovered message types (ICommand, IEvent)");
    foreach (var message in messages) {
      sb.AppendLine($"  if (type == typeof({message.FullyQualifiedName})) return {message.SimpleName};");
    }
    sb.AppendLine();

    // Message envelope types
    sb.AppendLine("  // Message envelope types");
    var snippet = TemplateUtilities.ExtractSnippet(assembly, "JsonTypeInfoSnippets.cs", "MESSAGE_ENVELOPE_GET_TYPE_INFO_CASE");
    foreach (var message in messages) {
      var caseStatement = snippet
          .Replace("__PAYLOAD_TYPE__", message.FullyQualifiedName)
          .Replace("__PAYLOAD_NAME__", message.SimpleName);
      sb.AppendLine("  " + caseStatement.TrimEnd());
    }

    sb.AppendLine();

    // Primitives
    sb.AppendLine("  // Primitive types");
    sb.AppendLine("  if (type == typeof(string)) return JsonMetadataServices.CreateValueInfo<string>(Options, JsonMetadataServices.StringConverter);");
    sb.AppendLine("  if (type == typeof(int)) return JsonMetadataServices.CreateValueInfo<int>(Options, JsonMetadataServices.Int32Converter);");
    sb.AppendLine("  if (type == typeof(long)) return JsonMetadataServices.CreateValueInfo<long>(Options, JsonMetadataServices.Int64Converter);");
    sb.AppendLine("  if (type == typeof(bool)) return JsonMetadataServices.CreateValueInfo<bool>(Options, JsonMetadataServices.BooleanConverter);");
    sb.AppendLine("  if (type == typeof(double)) return JsonMetadataServices.CreateValueInfo<double>(Options, JsonMetadataServices.DoubleConverter);");
    sb.AppendLine("  if (type == typeof(decimal)) return JsonMetadataServices.CreateValueInfo<decimal>(Options, JsonMetadataServices.DecimalConverter);");
    sb.AppendLine("  if (type == typeof(global::System.DateTime)) return JsonMetadataServices.CreateValueInfo<global::System.DateTime>(Options, JsonMetadataServices.DateTimeConverter);");
    sb.AppendLine("  if (type == typeof(global::System.Guid)) return JsonMetadataServices.CreateValueInfo<global::System.Guid>(Options, JsonMetadataServices.GuidConverter);");
    sb.AppendLine();

    sb.AppendLine("  return null;");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private static string GenerateHelperMethods(Assembly assembly) {
    var sb = new System.Text.StringBuilder();

    var createMessageEnvelope = TemplateUtilities.ExtractSnippet(assembly, "JsonTypeInfoSnippets.cs", "GENERIC_CREATE_MESSAGE_ENVELOPE");
    var createProperty = TemplateUtilities.ExtractSnippet(assembly, "JsonTypeInfoSnippets.cs", "CREATE_PROPERTY_HELPER");
    var createConstructorParam = TemplateUtilities.ExtractSnippet(assembly, "JsonTypeInfoSnippets.cs", "CREATE_CONSTRUCTOR_PARAMETER_HELPER");

    sb.AppendLine(createMessageEnvelope);
    sb.AppendLine();
    sb.AppendLine(createProperty);
    sb.AppendLine();
    sb.AppendLine(createConstructorParam);

    return sb.ToString();
  }

  private static string GenerateCoreTypeFactories(Assembly assembly) {
    var sb = new System.Text.StringBuilder();

    // MessageId factory - use custom AOT-compatible converter
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.MessageId> Create_MessageId(JsonSerializerOptions options) {");
    sb.AppendLine("  // MessageId is a Vogen ValueObject<Guid> - use custom converter for AOT compatibility");
    sb.AppendLine("  var converter = new global::Whizbang.Core.ValueObjects.MessageIdJsonConverter();");
    sb.AppendLine("  var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<global::Whizbang.Core.ValueObjects.MessageId>(options, converter);");
    sb.AppendLine("  jsonTypeInfo.OriginatingResolver = this;");
    sb.AppendLine("  return jsonTypeInfo;");
    sb.AppendLine("}");
    sb.AppendLine();

    // CorrelationId factory - use custom AOT-compatible converter
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.CorrelationId> Create_CorrelationId(JsonSerializerOptions options) {");
    sb.AppendLine("  // CorrelationId is a Vogen ValueObject<Guid> - use custom converter for AOT compatibility");
    sb.AppendLine("  var converter = new global::Whizbang.Core.ValueObjects.CorrelationIdJsonConverter();");
    sb.AppendLine("  var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<global::Whizbang.Core.ValueObjects.CorrelationId>(options, converter);");
    sb.AppendLine("  jsonTypeInfo.OriginatingResolver = this;");
    sb.AppendLine("  return jsonTypeInfo;");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private static string GenerateMessageTypeFactories(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<{message.FullyQualifiedName}> Create_{message.SimpleName}(JsonSerializerOptions options) {{");

      // Generate properties array
      sb.AppendLine($"  var properties = new JsonPropertyInfo[{message.Properties.Length}];");
      sb.AppendLine();

      for (int i = 0; i < message.Properties.Length; i++) {
        var prop = message.Properties[i];
        sb.AppendLine($"  properties[{i}] = CreateProperty<{prop.Type}>(");
        sb.AppendLine($"      options,");
        sb.AppendLine($"      \"{prop.Name}\",");
        sb.AppendLine($"      obj => (({message.FullyQualifiedName})obj).{prop.Name},");
        sb.AppendLine($"      (JsonTypeInfo<{prop.Type}>)options.GetTypeInfo(typeof({prop.Type})));");
        sb.AppendLine();
      }

      // Create JsonObjectInfoValues
      sb.AppendLine($"  var objectInfo = new JsonObjectInfoValues<{message.FullyQualifiedName}> {{");
      sb.AppendLine($"      ObjectCreator = null,");
      sb.AppendLine($"      PropertyMetadataInitializer = _ => properties");
      sb.AppendLine($"  }};");
      sb.AppendLine();

      // Create JsonTypeInfo
      sb.AppendLine($"  var jsonTypeInfo = JsonMetadataServices.CreateObjectInfo(options, objectInfo);");
      sb.AppendLine($"  jsonTypeInfo.OriginatingResolver = this;");
      sb.AppendLine($"  return jsonTypeInfo;");
      sb.AppendLine($"}}");
      sb.AppendLine();
    }

    return sb.ToString();
  }
}
