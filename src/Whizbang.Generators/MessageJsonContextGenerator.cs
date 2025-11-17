using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Source generator that discovers message types (ICommand, IEvent) and generates
/// WhizbangJsonContext with JsonTypeInfo for AOT-compatible serialization.
/// This context handles message types discovered in the current assembly.
/// Use with WhizbangInfrastructureJsonContext for complete Whizbang serialization support.
/// </summary>
[Generator]
public class MessageJsonContextGenerator : IIncrementalGenerator {
  private const string I_COMMAND = "Whizbang.Core.ICommand";
  private const string I_EVENT = "Whizbang.Core.IEvent";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover message types (commands and events)
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 } ||
                                       node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => ExtractMessageTypeInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine compilation with messages to get assembly name
    var messagesWithCompilation = messageTypes.Collect().Combine(context.CompilationProvider);

    // Generate WhizbangJsonContext from collected message types
    context.RegisterSourceOutput(
        messagesWithCompilation,
        static (ctx, data) => GenerateWhizbangJsonContext(ctx, data.Left!, data.Right)
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
            Type: p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IsInitOnly: p.SetMethod?.IsInitOnly ?? false
        ))
        .ToArray();

    // Detect if type has a parameterized constructor matching all properties
    // This is true for records with primary constructors like: record MyRecord(string Prop1, int Prop2)
    // This is false for records with required properties like: record MyRecord { public required string Prop1 { get; init; } }
    bool hasParameterizedConstructor = typeSymbol.Constructors.Any(c =>
        c.DeclaredAccessibility == Accessibility.Public &&
        c.Parameters.Length == properties.Length &&
        c.Parameters.All(p => properties.Any(prop =>
            prop.Name.Equals(p.Name, System.StringComparison.OrdinalIgnoreCase))));

    return new JsonMessageTypeInfo(
        FullyQualifiedName: fullyQualifiedName,
        SimpleName: simpleName,
        IsCommand: isCommand,
        IsEvent: isEvent,
        Properties: properties,
        HasParameterizedConstructor: hasParameterizedConstructor
    );
  }

  /// <summary>
  /// Generates WhizbangJsonContext.g.cs with JsonTypeInfo objects for all discovered message types
  /// and Whizbang core types (MessageId, CorrelationId, etc.).
  /// </summary>
  private static void GenerateWhizbangJsonContext(
      SourceProductionContext context,
      ImmutableArray<JsonMessageTypeInfo> messages,
      Compilation compilation) {

    // Report that generator is running
    context.ReportDiagnostic(Diagnostic.Create(
        new DiagnosticDescriptor(
            "WHIZ099",
            "MessageJsonContextGenerator Running",
            "MessageJsonContextGenerator invoked for assembly '{0}' with {1} message type(s)",
            "Whizbang.SourceGeneration",
            DiagnosticSeverity.Info,
            true),
        Location.None,
        compilation.AssemblyName ?? "Unknown",
        messages.Length
    ));

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

    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template
    var assembly = typeof(MessageJsonContextGenerator).Assembly;
    var template = TemplateUtilities.GetEmbeddedTemplate(assembly, "WhizbangJsonContextTemplate.cs");

    // Replace NAMESPACE region with assembly-specific namespace
    template = TemplateUtilities.ReplaceRegion(template, "NAMESPACE", $"namespace {namespaceName};");

    // Replace HEADER region with timestamp
    template = TemplateUtilities.ReplaceHeaderRegion(assembly, template);

    // Generate and replace each region
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_FIELDS", GenerateLazyFields(assembly, messages));
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_PROPERTIES", GenerateLazyProperties(assembly, messages));
    template = TemplateUtilities.ReplaceRegion(template, "ASSEMBLY_AWARE_HELPER", GenerateAssemblyAwareHelper(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "GET_DISCOVERED_TYPE_INFO", GenerateGetTypeInfo(assembly, messages));
    template = TemplateUtilities.ReplaceRegion(template, "HELPER_METHODS", GenerateHelperMethods(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "CORE_TYPE_FACTORIES", GenerateCoreTypeFactories(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_TYPE_FACTORIES", GenerateMessageTypeFactories(assembly, messages));
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_ENVELOPE_FACTORIES", GenerateMessageEnvelopeFactories(assembly, messages));

    context.AddSource("WhizbangJsonContext.g.cs", template);
  }

  private static string GenerateLazyFields(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    // Load snippets
    var valueObjectFieldSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "LAZY_FIELD_VALUE_OBJECT");

    var messageFieldSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "LAZY_FIELD_MESSAGE");

    var envelopeFieldSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "LAZY_FIELD_MESSAGE_ENVELOPE");

    // Core Whizbang types that require custom converters (Vogen value objects)
    sb.AppendLine(valueObjectFieldSnippet.Replace("__TYPE_NAME__", "MessageId"));
    sb.AppendLine(valueObjectFieldSnippet.Replace("__TYPE_NAME__", "CorrelationId"));
    sb.AppendLine();

    // Discovered message types - need JsonTypeInfo for AOT
    foreach (var message in messages) {
      var field = messageFieldSnippet
          .Replace("__FULLY_QUALIFIED_NAME__", message.FullyQualifiedName)
          .Replace("__SIMPLE_NAME__", message.SimpleName);
      sb.AppendLine(field);
    }
    sb.AppendLine();

    // MessageEnvelope<T> for each discovered message type
    foreach (var message in messages) {
      var field = envelopeFieldSnippet
          .Replace("__FULLY_QUALIFIED_NAME__", message.FullyQualifiedName)
          .Replace("__SIMPLE_NAME__", message.SimpleName);
      sb.AppendLine(field);
    }

    return sb.ToString();
  }

  private static string GenerateLazyProperties(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    // Note: We don't use lazy properties anymore since we create in GetTypeInfo with provided options
    sb.AppendLine("// JsonTypeInfo objects are created on-demand in GetTypeInfo() using provided options");

    return sb.ToString();
  }

  private static string GenerateGetTypeInfo(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    // Load snippets
    var valueObjectCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "GET_TYPE_INFO_VALUE_OBJECT");

    var messageCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "GET_TYPE_INFO_MESSAGE");

    var envelopeCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "GET_TYPE_INFO_MESSAGE_ENVELOPE");

    // Implement IJsonTypeInfoResolver.GetTypeInfo(Type, JsonSerializerOptions)
    sb.AppendLine("JsonTypeInfo? IJsonTypeInfoResolver.GetTypeInfo(Type type, JsonSerializerOptions options) {");
    sb.AppendLine("  return GetTypeInfoInternal(type, options);");
    sb.AppendLine("}");
    sb.AppendLine();

    // Override base GetTypeInfo(Type) for compatibility
    sb.AppendLine("public override JsonTypeInfo? GetTypeInfo(Type type) {");
    sb.AppendLine("  // When called directly (not in resolver chain), Options might be null");
    sb.AppendLine("  if (Options == null) return null;");
    sb.AppendLine("  return GetTypeInfoInternal(type, Options);");
    sb.AppendLine("}");
    sb.AppendLine();

    // Shared implementation
    sb.AppendLine("private JsonTypeInfo? GetTypeInfoInternal(Type type, JsonSerializerOptions options) {");
    sb.AppendLine("  // Core Whizbang value objects with custom converters");
    sb.AppendLine(valueObjectCheckSnippet.Replace("__TYPE_NAME__", "MessageId"));
    sb.AppendLine(valueObjectCheckSnippet.Replace("__TYPE_NAME__", "CorrelationId"));
    sb.AppendLine();

    // Discovered message types
    sb.AppendLine("  // Discovered message types (ICommand, IEvent)");
    foreach (var message in messages) {
      var check = messageCheckSnippet
          .Replace("__FULLY_QUALIFIED_NAME__", message.FullyQualifiedName)
          .Replace("__SIMPLE_NAME__", message.SimpleName);
      sb.AppendLine(check);
      sb.AppendLine();
    }

    // MessageEnvelope<T> types
    sb.AppendLine("  // MessageEnvelope<T> for discovered message types");
    foreach (var message in messages) {
      var check = envelopeCheckSnippet
          .Replace("__FULLY_QUALIFIED_NAME__", message.FullyQualifiedName)
          .Replace("__SIMPLE_NAME__", message.SimpleName);
      sb.AppendLine(check);
      sb.AppendLine();
    }

    sb.AppendLine("  // Return null for types we don't handle - let next resolver in chain handle them");
    sb.AppendLine("  return null;");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private static string GenerateHelperMethods(Assembly assembly) {
    var sb = new StringBuilder();

    // Load helper snippets
    var createPropertySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "HELPER_CREATE_PROPERTY");

    var getOrCreateTypeInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "HELPER_GET_OR_CREATE_TYPE_INFO");

    sb.AppendLine(createPropertySnippet);
    sb.AppendLine();
    sb.AppendLine(getOrCreateTypeInfoSnippet);

    return sb.ToString();
  }

  private static string GenerateCoreTypeFactories(Assembly assembly) {
    var sb = new System.Text.StringBuilder();

    // Load snippet
    var coreTypeFactorySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "CORE_TYPE_FACTORY");

    // MessageId factory - use custom AOT-compatible converter
    sb.AppendLine(coreTypeFactorySnippet.Replace("__TYPE_NAME__", "MessageId"));
    sb.AppendLine();

    // CorrelationId factory - use custom AOT-compatible converter
    sb.AppendLine(coreTypeFactorySnippet.Replace("__TYPE_NAME__", "CorrelationId"));
    sb.AppendLine();

    // Infrastructure types will use default serialization - we only need special handling for MessageId and CorrelationId

    return sb.ToString();
  }

  private static string GenerateMessageTypeFactories(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new StringBuilder();

    // Load snippets
    var propertyCreationSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "PROPERTY_CREATION_CALL");

    var parameterInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "PARAMETER_INFO_VALUES");

    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<{message.FullyQualifiedName}> Create_{message.SimpleName}(JsonSerializerOptions options) {{");

      // Generate properties array
      sb.AppendLine($"  var properties = new JsonPropertyInfo[{message.Properties.Length}];");
      sb.AppendLine();

      for (int i = 0; i < message.Properties.Length; i++) {
        var prop = message.Properties[i];
        var setter = prop.IsInitOnly
            ? "null,  // Init-only property, STJ will use reflection"
            : $"(obj, value) => (({message.FullyQualifiedName})obj).{prop.Name} = value,";

        var propertyCode = propertyCreationSnippet
            .Replace("__INDEX__", i.ToString())
            .Replace("__PROPERTY_TYPE__", prop.Type)
            .Replace("__PROPERTY_NAME__", prop.Name)
            .Replace("__MESSAGE_TYPE__", message.FullyQualifiedName)
            .Replace("__SETTER__", setter);

        sb.AppendLine(propertyCode);
        sb.AppendLine();
      }

      // Generate different code based on constructor type
      if (message.HasParameterizedConstructor) {
        // Type has parameterized constructor (e.g., record with primary constructor)
        // Generate constructor parameters using snippet
        sb.AppendLine($"  var ctorParams = new JsonParameterInfoValues[{message.Properties.Length}];");
        for (int i = 0; i < message.Properties.Length; i++) {
          var prop = message.Properties[i];
          var parameterCode = parameterInfoSnippet
              .Replace("__INDEX__", i.ToString())
              .Replace("__PARAMETER_NAME__", prop.Name)
              .Replace("__PROPERTY_TYPE__", prop.Type);

          sb.AppendLine(parameterCode);
        }
        sb.AppendLine();

        // Create JsonObjectInfoValues with parameterized constructor
        sb.AppendLine($"  var objectInfo = new JsonObjectInfoValues<{message.FullyQualifiedName}> {{");
        sb.AppendLine($"      ObjectWithParameterizedConstructorCreator = static args => new {message.FullyQualifiedName}(");
        for (int i = 0; i < message.Properties.Length; i++) {
          var prop = message.Properties[i];
          var comma = i < message.Properties.Length - 1 ? "," : "";
          sb.AppendLine($"          ({prop.Type})args[{i}]{comma}");
        }
        sb.AppendLine("      ),");
        sb.AppendLine($"      PropertyMetadataInitializer = _ => properties,");
        sb.AppendLine($"      ConstructorParameterMetadataInitializer = () => ctorParams");
        sb.AppendLine($"  }};");
      } else {
        // Type has no parameterized constructor but has init-only properties (e.g., record with required properties)
        // Use object initializer syntax to set init-only properties during construction
        // Generate constructor parameters using snippet
        sb.AppendLine($"  var ctorParams = new JsonParameterInfoValues[{message.Properties.Length}];");
        for (int i = 0; i < message.Properties.Length; i++) {
          var prop = message.Properties[i];
          var parameterCode = parameterInfoSnippet
              .Replace("__INDEX__", i.ToString())
              .Replace("__PARAMETER_NAME__", prop.Name)
              .Replace("__PROPERTY_TYPE__", prop.Type);

          sb.AppendLine(parameterCode);
        }
        sb.AppendLine();

        // Create JsonObjectInfoValues with object initializer
        sb.AppendLine($"  var objectInfo = new JsonObjectInfoValues<{message.FullyQualifiedName}> {{");
        sb.AppendLine($"      ObjectWithParameterizedConstructorCreator = static args => new {message.FullyQualifiedName}() {{");
        for (int i = 0; i < message.Properties.Length; i++) {
          var prop = message.Properties[i];
          var comma = i < message.Properties.Length - 1 ? "," : "";
          sb.AppendLine($"          {prop.Name} = ({prop.Type})args[{i}]{comma}");
        }
        sb.AppendLine("      },");
        sb.AppendLine($"      PropertyMetadataInitializer = _ => properties,");
        sb.AppendLine($"      ConstructorParameterMetadataInitializer = () => ctorParams");
        sb.AppendLine($"  }};");
      }
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

  private static string GenerateMessageEnvelopeFactories(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new StringBuilder();

    // Load snippets
    var propertyCreationSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "PROPERTY_CREATION_CALL");

    var parameterInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "PARAMETER_INFO_VALUES");

    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<MessageEnvelope<{message.FullyQualifiedName}>> CreateMessageEnvelope_{message.SimpleName}(JsonSerializerOptions options) {{");

      // Generate properties array for MessageEnvelope<T> (MessageId, Payload, Hops)
      sb.AppendLine("  var properties = new JsonPropertyInfo[3];");
      sb.AppendLine();

      // Property 0: MessageId using snippet
      var messageIdProperty = propertyCreationSnippet
          .Replace("__INDEX__", "0")
          .Replace("__PROPERTY_TYPE__", "MessageId")
          .Replace("__PROPERTY_NAME__", "MessageId")
          .Replace("__MESSAGE_TYPE__", $"MessageEnvelope<{message.FullyQualifiedName}>")
          .Replace("__SETTER__", "null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine(messageIdProperty);
      sb.AppendLine();

      // Property 1: Payload using snippet
      var payloadProperty = propertyCreationSnippet
          .Replace("__INDEX__", "1")
          .Replace("__PROPERTY_TYPE__", message.FullyQualifiedName)
          .Replace("__PROPERTY_NAME__", "Payload")
          .Replace("__MESSAGE_TYPE__", $"MessageEnvelope<{message.FullyQualifiedName}>")
          .Replace("__SETTER__", "null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine(payloadProperty);
      sb.AppendLine();

      // Property 2: Hops using snippet
      var hopsProperty = propertyCreationSnippet
          .Replace("__INDEX__", "2")
          .Replace("__PROPERTY_TYPE__", "List<MessageHop>")
          .Replace("__PROPERTY_NAME__", "Hops")
          .Replace("__MESSAGE_TYPE__", $"MessageEnvelope<{message.FullyQualifiedName}>")
          .Replace("__SETTER__", "null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine(hopsProperty);
      sb.AppendLine();

      // Constructor parameters using snippet
      sb.AppendLine("  var ctorParams = new JsonParameterInfoValues[3];");

      var messageIdParam = parameterInfoSnippet
          .Replace("__INDEX__", "0")
          .Replace("__PARAMETER_NAME__", "messageId")
          .Replace("__PROPERTY_TYPE__", "MessageId");
      sb.AppendLine(messageIdParam);

      var payloadParam = parameterInfoSnippet
          .Replace("__INDEX__", "1")
          .Replace("__PARAMETER_NAME__", "payload")
          .Replace("__PROPERTY_TYPE__", message.FullyQualifiedName);
      sb.AppendLine(payloadParam);

      var hopsParam = parameterInfoSnippet
          .Replace("__INDEX__", "2")
          .Replace("__PARAMETER_NAME__", "hops")
          .Replace("__PROPERTY_TYPE__", "List<MessageHop>");
      sb.AppendLine(hopsParam);
      sb.AppendLine();

      // Create JsonObjectInfoValues
      sb.AppendLine($"  var objectInfo = new JsonObjectInfoValues<MessageEnvelope<{message.FullyQualifiedName}>> {{");
      sb.AppendLine($"      ObjectWithParameterizedConstructorCreator = static args => new MessageEnvelope<{message.FullyQualifiedName}>(");
      sb.AppendLine("          (MessageId)args[0],");
      sb.AppendLine($"          ({message.FullyQualifiedName})args[1],");
      sb.AppendLine("          (List<MessageHop>)args[2]),");
      sb.AppendLine("      PropertyMetadataInitializer = _ => properties,");
      sb.AppendLine("      ConstructorParameterMetadataInitializer = () => ctorParams");
      sb.AppendLine("  };");
      sb.AppendLine();

      // Create JsonTypeInfo
      sb.AppendLine($"  var jsonTypeInfo = JsonMetadataServices.CreateObjectInfo(options, objectInfo);");
      sb.AppendLine("  jsonTypeInfo.OriginatingResolver = this;");
      sb.AppendLine("  return jsonTypeInfo;");
      sb.AppendLine("}");
      sb.AppendLine();
    }

    return sb.ToString();
  }

  private static string GenerateAssemblyAwareHelper(Assembly assembly) {
    // Load snippet
    var createOptionsSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        "JsonContextSnippets.cs",
        "HELPER_CREATE_OPTIONS");

    return createOptionsSnippet;
  }
}
