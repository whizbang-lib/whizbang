using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
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

    // Core Whizbang types that require custom converters (Vogen value objects)
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.MessageId>? _MessageId;");
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.CorrelationId>? _CorrelationId;");
    sb.AppendLine();

    // Discovered message types - need JsonTypeInfo for AOT
    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<{message.FullyQualifiedName}>? _{message.SimpleName};");
    }
    sb.AppendLine();

    // MessageEnvelope<T> for each discovered message type
    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<MessageEnvelope<{message.FullyQualifiedName}>>? _MessageEnvelope_{message.SimpleName};");
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

    // Implement IJsonTypeInfoResolver.GetTypeInfo(Type, JsonSerializerOptions)
    // This is the method that gets called when used in a resolver chain
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
    sb.AppendLine("  if (type == typeof(global::Whizbang.Core.ValueObjects.MessageId)) return Create_MessageId(options);");
    sb.AppendLine("  if (type == typeof(global::Whizbang.Core.ValueObjects.CorrelationId)) return Create_CorrelationId(options);");
    sb.AppendLine();

    // Discovered message types
    sb.AppendLine("  // Discovered message types (ICommand, IEvent)");
    foreach (var message in messages) {
      sb.AppendLine($"  if (type == typeof({message.FullyQualifiedName})) {{");
      sb.AppendLine($"    return Create_{message.SimpleName}(options);");
      sb.AppendLine($"  }}");
      sb.AppendLine();
    }

    // MessageEnvelope<T> types
    sb.AppendLine("  // MessageEnvelope<T> for discovered message types");
    foreach (var message in messages) {
      sb.AppendLine($"  if (type == typeof(MessageEnvelope<{message.FullyQualifiedName}>)) {{");
      sb.AppendLine($"    return CreateMessageEnvelope_{message.SimpleName}(options);");
      sb.AppendLine($"  }}");
      sb.AppendLine();
    }

    sb.AppendLine("  // Return null for types we don't handle - let next resolver in chain handle them");
    sb.AppendLine("  return null;");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private static string GenerateHelperMethods(Assembly assembly) {
    var sb = new System.Text.StringBuilder();

    // Generate CreateProperty helper for message type factories
    sb.AppendLine("private JsonPropertyInfo CreateProperty<TProperty>(");
    sb.AppendLine("    JsonSerializerOptions options,");
    sb.AppendLine("    string propertyName,");
    sb.AppendLine("    Func<object, TProperty> getter,");
    sb.AppendLine("    Action<object, TProperty>? setter,");
    sb.AppendLine("    JsonTypeInfo<TProperty> propertyTypeInfo) {");
    sb.AppendLine();
    sb.AppendLine("  var propertyInfo = new JsonPropertyInfoValues<TProperty> {");
    sb.AppendLine("    IsProperty = true,");
    sb.AppendLine("    IsPublic = true,");
    sb.AppendLine("    DeclaringType = typeof(object),  // Generic - not specific to MessageEnvelope");
    sb.AppendLine("    PropertyTypeInfo = propertyTypeInfo,");
    sb.AppendLine("    Getter = getter,");
    sb.AppendLine("    Setter = setter,");
    sb.AppendLine("    PropertyName = propertyName,");
    sb.AppendLine("    JsonPropertyName = propertyName");
    sb.AppendLine("  };");
    sb.AppendLine();
    sb.AppendLine("  return JsonMetadataServices.CreatePropertyInfo(options, propertyInfo);");
    sb.AppendLine("}");
    sb.AppendLine();

    // Add helper to get JsonTypeInfo for any type (primitives + custom)
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Gets JsonTypeInfo for a type, handling primitives in AOT-compatible way.");
    sb.AppendLine("/// For complex types, queries the full resolver chain.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("private JsonTypeInfo<T> GetOrCreateTypeInfo<T>(JsonSerializerOptions options) {");
    sb.AppendLine("  var type = typeof(T);");
    sb.AppendLine();
    sb.AppendLine("  // Try our own resolver first (MessageId, CorrelationId, discovered types, etc.)");
    sb.AppendLine("  var typeInfo = GetTypeInfoInternal(type, options);");
    sb.AppendLine("  if (typeInfo != null) return (JsonTypeInfo<T>)typeInfo;");
    sb.AppendLine();
    sb.AppendLine("  // Handle common primitive types using JsonMetadataServices (AOT-compatible)");
    sb.AppendLine("  if (type == typeof(string)) return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<string>(options, JsonMetadataServices.StringConverter);");
    sb.AppendLine("  if (type == typeof(int)) return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);");
    sb.AppendLine("  if (type == typeof(long)) return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<long>(options, JsonMetadataServices.Int64Converter);");
    sb.AppendLine("  if (type == typeof(bool)) return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<bool>(options, JsonMetadataServices.BooleanConverter);");
    sb.AppendLine("  if (type == typeof(DateTime)) return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateTime>(options, JsonMetadataServices.DateTimeConverter);");
    sb.AppendLine("  if (type == typeof(DateTimeOffset)) return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, JsonMetadataServices.DateTimeOffsetConverter);");
    sb.AppendLine("  if (type == typeof(Guid)) return (JsonTypeInfo<T>)(object)JsonMetadataServices.CreateValueInfo<Guid>(options, JsonMetadataServices.GuidConverter);");
    sb.AppendLine();
    sb.AppendLine("  // For complex types (List<T>, Dictionary<K,V>, etc.), query the full resolver chain");
    sb.AppendLine("  // This will check InfrastructureJsonContext and user-provided resolvers");
    sb.AppendLine("  var chainTypeInfo = options.GetTypeInfo(type);");
    sb.AppendLine("  if (chainTypeInfo != null) return (JsonTypeInfo<T>)chainTypeInfo;");
    sb.AppendLine();
    sb.AppendLine("  // If still null, type is not registered anywhere - throw helpful error");
    sb.AppendLine("  throw new InvalidOperationException($\"No JsonTypeInfo found for type {type.FullName}. \" +");
    sb.AppendLine("    \"Ensure you pass a resolver for this type to CreateOptions(), or add [JsonSerializable] to a JsonSerializerContext.\");");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private static string GenerateCoreTypeFactories(Assembly assembly) {
    var sb = new System.Text.StringBuilder();

    // MessageId factory - use custom AOT-compatible converter
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.MessageId> Create_MessageId(JsonSerializerOptions options) {");
    sb.AppendLine("  var converter = new global::Whizbang.Core.ValueObjects.MessageIdJsonConverter();");
    sb.AppendLine("  var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<global::Whizbang.Core.ValueObjects.MessageId>(options, converter);");
    sb.AppendLine("  jsonTypeInfo.OriginatingResolver = this;");
    sb.AppendLine("  return jsonTypeInfo;");
    sb.AppendLine("}");
    sb.AppendLine();

    // CorrelationId factory - use custom AOT-compatible converter
    sb.AppendLine("private JsonTypeInfo<global::Whizbang.Core.ValueObjects.CorrelationId> Create_CorrelationId(JsonSerializerOptions options) {");
    sb.AppendLine("  var converter = new global::Whizbang.Core.ValueObjects.CorrelationIdJsonConverter();");
    sb.AppendLine("  var jsonTypeInfo = JsonMetadataServices.CreateValueInfo<global::Whizbang.Core.ValueObjects.CorrelationId>(options, converter);");
    sb.AppendLine("  jsonTypeInfo.OriginatingResolver = this;");
    sb.AppendLine("  return jsonTypeInfo;");
    sb.AppendLine("}");
    sb.AppendLine();

    // Infrastructure types will use default serialization - we only need special handling for MessageId and CorrelationId

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
        var setter = prop.IsInitOnly
            ? "null,  // Init-only property, STJ will use reflection"
            : $"(obj, value) => (({message.FullyQualifiedName})obj).{prop.Name} = value,";

        sb.AppendLine($"  properties[{i}] = CreateProperty<{prop.Type}>(");
        sb.AppendLine($"      options,");
        sb.AppendLine($"      \"{prop.Name}\",");
        sb.AppendLine($"      obj => (({message.FullyQualifiedName})obj).{prop.Name},");
        sb.AppendLine($"      {setter}");
        sb.AppendLine($"      GetOrCreateTypeInfo<{prop.Type}>(options));");
        sb.AppendLine();
      }

      // Generate different code based on constructor type
      if (message.HasParameterizedConstructor) {
        // Type has parameterized constructor (e.g., record with primary constructor)
        // Generate constructor parameters
        sb.AppendLine($"  var ctorParams = new JsonParameterInfoValues[{message.Properties.Length}];");
        for (int i = 0; i < message.Properties.Length; i++) {
          var prop = message.Properties[i];
          sb.AppendLine($"  ctorParams[{i}] = new JsonParameterInfoValues {{");
          sb.AppendLine($"      Name = \"{prop.Name}\",");
          sb.AppendLine($"      ParameterType = typeof({prop.Type}),");
          sb.AppendLine($"      Position = {i},");
          sb.AppendLine($"      HasDefaultValue = false,");
          sb.AppendLine($"      DefaultValue = null");
          sb.AppendLine($"  }};");
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
        // Type has no parameterized constructor (e.g., record with required properties)
        // For these types, use reflection to create instance without setting required members
        // This is necessary because we can't call new T() without satisfying required members
        sb.AppendLine($"  var objectInfo = new JsonObjectInfoValues<{message.FullyQualifiedName}> {{");
        sb.AppendLine($"      ObjectCreator = static () => (({message.FullyQualifiedName})System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof({message.FullyQualifiedName}))),");
        sb.AppendLine($"      PropertyMetadataInitializer = _ => properties");
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
    var sb = new System.Text.StringBuilder();

    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<MessageEnvelope<{message.FullyQualifiedName}>> CreateMessageEnvelope_{message.SimpleName}(JsonSerializerOptions options) {{");

      // Generate properties array for MessageEnvelope<T> (MessageId, Payload, Hops)
      sb.AppendLine("  var properties = new JsonPropertyInfo[3];");
      sb.AppendLine();

      // Property 0: MessageId
      sb.AppendLine("  properties[0] = CreateProperty<MessageId>(");
      sb.AppendLine("      options,");
      sb.AppendLine("      \"MessageId\",");
      sb.AppendLine($"      obj => ((MessageEnvelope<{message.FullyQualifiedName}>)obj).MessageId,");
      sb.AppendLine("      null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine("      GetOrCreateTypeInfo<MessageId>(options));");
      sb.AppendLine();

      // Property 1: Payload
      sb.AppendLine($"  properties[1] = CreateProperty<{message.FullyQualifiedName}>(");
      sb.AppendLine("      options,");
      sb.AppendLine("      \"Payload\",");
      sb.AppendLine($"      obj => ((MessageEnvelope<{message.FullyQualifiedName}>)obj).Payload,");
      sb.AppendLine("      null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine($"      GetOrCreateTypeInfo<{message.FullyQualifiedName}>(options));");
      sb.AppendLine();

      // Property 2: Hops
      sb.AppendLine("  properties[2] = CreateProperty<List<MessageHop>>(");
      sb.AppendLine("      options,");
      sb.AppendLine("      \"Hops\",");
      sb.AppendLine($"      obj => ((MessageEnvelope<{message.FullyQualifiedName}>)obj).Hops,");
      sb.AppendLine("      null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine("      GetOrCreateTypeInfo<List<MessageHop>>(options));");
      sb.AppendLine();

      // Constructor parameters (for deserialization)
      sb.AppendLine("  var ctorParams = new JsonParameterInfoValues[3];");
      sb.AppendLine("  ctorParams[0] = new JsonParameterInfoValues {");
      sb.AppendLine("    Name = \"messageId\",");
      sb.AppendLine("    ParameterType = typeof(MessageId),");
      sb.AppendLine("    Position = 0,");
      sb.AppendLine("    HasDefaultValue = false,");
      sb.AppendLine("    DefaultValue = null");
      sb.AppendLine("  };");
      sb.AppendLine("  ctorParams[1] = new JsonParameterInfoValues {");
      sb.AppendLine("    Name = \"payload\",");
      sb.AppendLine($"    ParameterType = typeof({message.FullyQualifiedName}),");
      sb.AppendLine("    Position = 1,");
      sb.AppendLine("    HasDefaultValue = false,");
      sb.AppendLine("    DefaultValue = null");
      sb.AppendLine("  };");
      sb.AppendLine("  ctorParams[2] = new JsonParameterInfoValues {");
      sb.AppendLine("    Name = \"hops\",");
      sb.AppendLine("    ParameterType = typeof(List<MessageHop>),");
      sb.AppendLine("    Position = 2,");
      sb.AppendLine("    HasDefaultValue = false,");
      sb.AppendLine("    DefaultValue = null");
      sb.AppendLine("  };");
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
    var sb = new System.Text.StringBuilder();

    // Generate helper that combines this context + user contexts (fully AOT-compatible)
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Creates JsonSerializerOptions with all required contexts for Whizbang serialization.");
    sb.AppendLine("/// Includes Whizbang types (MessageId, CorrelationId, MessageEnvelope), discovered message types,");
    sb.AppendLine("/// and primitive types (string, int, etc.). For complex types (List, Dictionary, custom classes),");
    sb.AppendLine("/// pass a JsonSerializerContext with [JsonSerializable] attributes as userResolvers.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("/// <param name=\"userResolvers\">Optional user JsonSerializerContext instances for complex types</param>");
    sb.AppendLine("/// <returns>AOT-compatible JsonSerializerOptions ready for use</returns>");
    sb.AppendLine("public static JsonSerializerOptions CreateOptions(params IJsonTypeInfoResolver[] userResolvers) {");
    sb.AppendLine("  // Create fully AOT-compatible resolver chain:");
    sb.AppendLine("  // 1. WhizbangJsonContext (message types, MessageEnvelope<T>, MessageId, CorrelationId)");
    sb.AppendLine("  // 2. User resolvers (custom application types)");
    sb.AppendLine("  // 3. InfrastructureJsonContext (MessageHop, SecurityContext, etc.)");
    sb.AppendLine("  var resolvers = new List<IJsonTypeInfoResolver> { Default };");
    sb.AppendLine("  resolvers.AddRange(userResolvers);");
    sb.AppendLine("  resolvers.Add(global::Whizbang.Core.Generated.InfrastructureJsonContext.Default);");
    sb.AppendLine();
    sb.AppendLine("  return new JsonSerializerOptions {");
    sb.AppendLine("    TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers.ToArray()),");
    sb.AppendLine("    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull");
    sb.AppendLine("  };");
    sb.AppendLine("}");

    return sb.ToString();
  }
}
