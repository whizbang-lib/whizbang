using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithSingleCommand_GeneratesWhizbangJsonContextAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithCommand_GeneratesMessageEnvelopeFactoryAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithMultipleMessages_GeneratesAllFactoriesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_GeneratesGetTypeInfoSwitchAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_GeneratesMessageEnvelopeFactoryMethodAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_GeneratesPropertyHelperMethodAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_GeneratesCoreValueObjectFactoriesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_GeneratesGetTypeInfoInternalMethodAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_ImplementsIJsonTypeInfoResolverAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_ReportsDiagnostic_ForDiscoveredMessageTypeAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithNoMessages_GeneratesOnlyCoreTypesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithNestedNamespaces_GeneratesFullyQualifiedNamesAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithNonMessageType_SkipsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithEmptyProject_GeneratesEmptyContextAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:MessageJsonContextGenerator_TypeImplementingBothInterfaces_GeneratesAsCommandAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:MessageJsonContextGenerator_ClassImplementingICommand_GeneratesJsonTypeInfoAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NoMessageTypes_ReportsDiagnosticWithZeroCountAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithMultipleProperties_GeneratesValidJsonObjectCreatorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_InternalCommand_SkipsNonPublicTypeAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithNestedCustomType_DiscoversAndGeneratesForBothAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithPrimitiveListProperty_SkipsNestedTypeDiscoveryAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithInternalNestedType_IncludesReferenceButSkipsFactoryAsync</tests>
/// Source generator that discovers message types (ICommand, IEvent) and generates
/// WhizbangJsonContext with JsonTypeInfo for AOT-compatible serialization.
/// This context handles message types discovered in the current assembly.
/// Use with WhizbangInfrastructureJsonContext for complete Whizbang serialization support.
/// </summary>
[Generator]
public class MessageJsonContextGenerator : IIncrementalGenerator {
  private const string I_COMMAND = "Whizbang.Core.ICommand";
  private const string I_EVENT = "Whizbang.Core.IEvent";
  private const string WHIZBANG_SERIALIZABLE = "Whizbang.WhizbangSerializableAttribute";

  // Template placeholders
  private const string TEMPLATE_SNIPPET_FILE = "JsonContextSnippets.cs";
  private const string PLACEHOLDER_TYPE_NAME = "__TYPE_NAME__";
  private const string PLACEHOLDER_MESSAGE_ID = "MessageId";
  private const string PLACEHOLDER_FULLY_QUALIFIED_NAME = "__FULLY_QUALIFIED_NAME__";
  private const string PLACEHOLDER_SIMPLE_NAME = "__SIMPLE_NAME__";
  private const string PLACEHOLDER_GLOBAL = "global::";
  private const string PLACEHOLDER_INDEX = "__INDEX__";
  private const string PLACEHOLDER_PROPERTY_TYPE = "__PROPERTY_TYPE__";
  private const string PLACEHOLDER_PROPERTY_NAME = "__PROPERTY_NAME__";
  private const string PLACEHOLDER_MESSAGE_TYPE = "__MESSAGE_TYPE__";
  private const string PLACEHOLDER_SETTER = "__SETTER__";
  private const string PLACEHOLDER_PARAMETER_NAME = "__PARAMETER_NAME__";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover message types (commands, events, and types with [WhizbangSerializable])
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            (node is RecordDeclarationSyntax rec && (rec.BaseList?.Types.Count > 0 || rec.AttributeLists.Count > 0)) ||
            (node is ClassDeclarationSyntax cls && (cls.BaseList?.Types.Count > 0 || cls.AttributeLists.Count > 0)),
        transform: static (ctx, ct) => _extractMessageTypeInfo(ctx, ct)
    ).Where(static info => info is not null);

    // Combine messages with compilation
    var messagesWithCompilation = messageTypes.Collect().Combine(context.CompilationProvider);

    // Generate WhizbangJsonContext from collected message types
    context.RegisterSourceOutput(
        messagesWithCompilation,
        static (ctx, data) => _generateWhizbangJsonContext(
            ctx,
            data.Left!,    // messages
            data.Right     // compilation
        )
    );
  }

  /// <summary>
  /// Symbol display format that includes nullable reference type annotations.
  /// This is critical for generating correct nullable-aware code in CS8619/CS8603 scenarios.
  /// </summary>
  private static readonly SymbolDisplayFormat _fullyQualifiedWithNullabilityFormat = new SymbolDisplayFormat(
      globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
      typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
      genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
      miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

  /// <summary>
  /// Gets the correct typeof() expression for a property based on its type and whether it's a value type.
  /// For nullable value types (decimal?), keeps the '?' to get typeof(decimal?).
  /// For nullable reference types (string?), strips the '?' to get typeof(string) since typeof(string?) is invalid C#.
  /// </summary>
  /// <param name="prop">Property information including type name and value type status</param>
  /// <returns>Type name suitable for typeof() expression</returns>
  private static string _getTypeOfExpression(PropertyInfo prop) {
    // If the type has a nullable marker '?'
    if (prop.Type.EndsWith("?", StringComparison.Ordinal)) {
      if (prop.IsValueType) {
        // Nullable value type: typeof(decimal?) is valid - keep the '?'
        return prop.Type;
      } else {
        // Nullable reference type: typeof(string?) is invalid - strip the '?'
        return prop.Type.Substring(0, prop.Type.Length - 1);
      }
    }

    // Non-nullable type - use as-is
    return prop.Type;
  }

  /// <summary>
  /// Determines if a type symbol represents a value type (struct, enum, primitive).
  /// This includes Nullable&lt;T&gt; where T is a value type.
  /// Used to determine correct typeof() expression for nullable types.
  /// </summary>
  /// <param name="typeSymbol">The type symbol to check</param>
  /// <returns>True if the type is a value type or Nullable&lt;T&gt; where T is a value type</returns>
  private static bool _isValueType(ITypeSymbol typeSymbol) {
    // Check if the type itself is a value type
    if (typeSymbol.IsValueType) {
      return true;
    }

    // Check if this is Nullable<T> where T is a value type
    // Nullable<T> is a struct (value type), but we want to know if the underlying type is a value type
    if (typeSymbol is INamedTypeSymbol namedType &&
        namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) {
      // Nullable<T> is itself a value type, so return true
      return true;
    }

    return false;
  }

  /// <summary>
  /// Determines the message kind for diagnostic reporting.
  /// </summary>
  private static string _getMessageKind(JsonMessageTypeInfo message) {
    if (message.IsCommand) {
      return "command";
    }
    if (message.IsEvent) {
      return "event";
    }
    return "serializable type";
  }

  /// <summary>
  /// Extracts message type information from syntax node using semantic analysis.
  /// Returns null if the node is not a message type (ICommand or IEvent).
  /// </summary>
  private static JsonMessageTypeInfo? _extractMessageTypeInfo(
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

    // Check if marked with [WhizbangSerializable] attribute
    bool isSerializable = typeSymbol.GetAttributes()
        .Any(a => a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{WHIZBANG_SERIALIZABLE}");

    // Type must be a command, event, or explicitly marked as serializable
    if (!isCommand && !isEvent && !isSerializable) {
      return null;
    }

    var fullyQualifiedName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var simpleName = typeSymbol.Name;

    // Extract property information for JSON serialization
    // Use custom format that includes nullability annotations to avoid CS8619/CS8603 warnings
    var properties = typeSymbol.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
        .Select(p => new PropertyInfo(
            Name: p.Name,
            Type: p.Type.ToDisplayString(_fullyQualifiedWithNullabilityFormat),
            IsValueType: _isValueType(p.Type),
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
        IsSerializable: isSerializable,
        Properties: properties,
        HasParameterizedConstructor: hasParameterizedConstructor
    );
  }

  /// <summary>
  /// Generates WhizbangJsonContext.g.cs with JsonTypeInfo objects for all discovered message types
  /// and Whizbang core types (MessageId, CorrelationId, etc.).
  /// </summary>
  private static void _generateWhizbangJsonContext(
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
      var messageKind = _getMessageKind(message);
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          message.SimpleName,
          messageKind
      ));
    }

    // Discover nested custom types used in message properties (e.g., OrderLineItem in List<OrderLineItem>)
    var nestedTypes = _discoverNestedTypes(messages, compilation);

    // Report diagnostics for discovered nested types
    foreach (var nestedType in nestedTypes) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          nestedType.SimpleName,
          "nested type"
      ));
    }

    // Combine messages and nested types for code generation
    var allTypes = messages.Concat(nestedTypes).ToImmutableArray();

    // Discover List<T> types used in all messages and nested types
    var listTypes = _discoverListTypes(allTypes);

    // Report diagnostics for discovered list types
    foreach (var listType in listTypes) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          $"List<{listType.ElementSimpleName}>",
          "collection type"
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

    // Generate lazy fields (messages + nested types + lists)
    var lazyFields = new System.Text.StringBuilder();
    lazyFields.Append(_generateLazyFields(assembly, allTypes));
    lazyFields.Append(_generateListLazyFields(assembly, listTypes));

    // Generate factory methods (messages + lists)
    var factories = new System.Text.StringBuilder();
    factories.Append(_generateMessageTypeFactories(assembly, allTypes));
    factories.Append(_generateListFactories(assembly, listTypes));

    // Discover WhizbangId converters by examining message property types
    var converters = _discoverWhizbangIdConverters(allTypes, compilation);

    // Generate and replace each region
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_FIELDS", lazyFields.ToString());
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_PROPERTIES", "// JsonTypeInfo objects are created on-demand in GetTypeInfo() using provided options");
    template = TemplateUtilities.ReplaceRegion(template, "ASSEMBLY_AWARE_HELPER", _generateAssemblyAwareHelper(assembly, converters, messages, compilation));
    template = TemplateUtilities.ReplaceRegion(template, "GET_DISCOVERED_TYPE_INFO", _generateGetTypeInfo(assembly, allTypes, listTypes));
    template = TemplateUtilities.ReplaceRegion(template, "HELPER_METHODS", _generateHelperMethods(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "GET_TYPE_INFO_BY_NAME", _generateGetTypeInfoByName(assembly, allTypes, compilation));
    template = TemplateUtilities.ReplaceRegion(template, "CORE_TYPE_FACTORIES", _generateCoreTypeFactories(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_TYPE_FACTORIES", factories.ToString());
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_ENVELOPE_FACTORIES", _generateMessageEnvelopeFactories(assembly, messages));

    context.AddSource("MessageJsonContext.g.cs", template);

    // Only generate WhizbangJsonContext facade if there are messages
    // (Whizbang.Core has a hand-written version since it has no messages)
    if (messages.Length > 0) {
      var facadeTemplate = TemplateUtilities.GetEmbeddedTemplate(assembly, "WhizbangJsonContextFacadeTemplate.cs");
      facadeTemplate = TemplateUtilities.ReplaceHeaderRegion(assembly, facadeTemplate);
      facadeTemplate = facadeTemplate.Replace("__ASSEMBLY_NAME__", assemblyName);
      facadeTemplate = facadeTemplate.Replace("__NAMESPACE__", namespaceName);

      // Reuse converters already discovered at line 195 for facade generation
      // Generate converter registration code
      var converterRegistrations = new System.Text.StringBuilder();
      if (!converters.IsEmpty) {
        converterRegistrations.AppendLine();
        converterRegistrations.AppendLine("    // Register WhizbangId converters");
        foreach (var converter in converters) {
          converterRegistrations.AppendLine($"    options.Converters.Add(new global::{converter.FullyQualifiedTypeName}());");
        }
      }
      facadeTemplate = facadeTemplate.Replace("__CONVERTER_REGISTRATIONS__", converterRegistrations.ToString());

      context.AddSource("WhizbangJsonContext.g.cs", facadeTemplate);
    }
  }

  private static string _generateLazyFields(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> allTypes) {
    var sb = new System.Text.StringBuilder();

    // Suppress CS0169 warning for unused fields (fields are reserved for future lazy initialization)
    sb.AppendLine("#pragma warning disable CS0169  // Field is never used");
    sb.AppendLine();

    // Load snippets
    var valueObjectFieldSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "LAZY_FIELD_VALUE_OBJECT");

    var messageFieldSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "LAZY_FIELD_MESSAGE");

    var envelopeFieldSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "LAZY_FIELD_MESSAGE_ENVELOPE");

    // Core Whizbang types that require custom converters (Vogen value objects)
    sb.AppendLine(valueObjectFieldSnippet.Replace(PLACEHOLDER_TYPE_NAME, PLACEHOLDER_MESSAGE_ID));
    sb.AppendLine(valueObjectFieldSnippet.Replace(PLACEHOLDER_TYPE_NAME, "CorrelationId"));
    sb.AppendLine();

    // Discovered types (messages + nested types) - need JsonTypeInfo for AOT
    foreach (var type in allTypes) {
      var field = messageFieldSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, type.FullyQualifiedName)
          .Replace(PLACEHOLDER_SIMPLE_NAME, type.SimpleName);
      sb.AppendLine(field);
    }
    sb.AppendLine();

    // MessageEnvelope<T> ONLY for actual message types (commands/events), not nested types
    foreach (var type in allTypes.Where(t => t.IsCommand || t.IsEvent)) {
      var field = envelopeFieldSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, type.FullyQualifiedName)
          .Replace(PLACEHOLDER_SIMPLE_NAME, type.SimpleName);
      sb.AppendLine(field);
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }


  private static string _generateGetTypeInfo(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> allTypes, ImmutableArray<ListTypeInfo> listTypes) {
    var sb = new System.Text.StringBuilder();

    // Load snippets
    var valueObjectCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_VALUE_OBJECT");

    var messageCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_MESSAGE");

    var envelopeCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_MESSAGE_ENVELOPE");

    var listCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_LIST");

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
    sb.AppendLine(valueObjectCheckSnippet.Replace(PLACEHOLDER_TYPE_NAME, PLACEHOLDER_MESSAGE_ID));
    sb.AppendLine(valueObjectCheckSnippet.Replace(PLACEHOLDER_TYPE_NAME, "CorrelationId"));
    sb.AppendLine();

    // All discovered types (messages + nested types)
    sb.AppendLine("  // Discovered types (messages + nested types)");
    foreach (var type in allTypes) {
      var check = messageCheckSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, type.FullyQualifiedName)
          .Replace(PLACEHOLDER_SIMPLE_NAME, type.SimpleName);
      sb.AppendLine(check);
      sb.AppendLine();
    }

    // MessageEnvelope<T> ONLY for actual message types (commands/events), not nested types
    sb.AppendLine("  // MessageEnvelope<T> for discovered message types");
    foreach (var type in allTypes.Where(t => t.IsCommand || t.IsEvent)) {
      var check = envelopeCheckSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, type.FullyQualifiedName)
          .Replace(PLACEHOLDER_SIMPLE_NAME, type.SimpleName);
      sb.AppendLine(check);
      sb.AppendLine();
    }

    // List<T> types discovered in messages
    if (!listTypes.IsEmpty) {
      sb.AppendLine("  // List<T> types discovered in messages");
      foreach (var listType in listTypes) {
        var check = listCheckSnippet
            .Replace("__ELEMENT_TYPE__", listType.ElementTypeName)
            .Replace("__ELEMENT_SIMPLE_NAME__", listType.ElementSimpleName);
        sb.AppendLine(check);
        sb.AppendLine();
      }
    }

    sb.AppendLine("  // Return null for types we don't handle - let next resolver in chain handle them");
    sb.AppendLine("  return null;");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private static string _generateHelperMethods(Assembly assembly) {
    var sb = new StringBuilder();

    // Load helper snippets
    var createPropertySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "HELPER_CREATE_PROPERTY");

    var getOrCreateTypeInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "HELPER_GET_OR_CREATE_TYPE_INFO");

    sb.AppendLine(createPropertySnippet);
    sb.AppendLine();
    sb.AppendLine(getOrCreateTypeInfoSnippet);

    return sb.ToString();
  }

  /// <summary>
  /// Generates AOT-safe GetTypeInfoByName method that maps assembly-qualified type names to JsonTypeInfo
  /// using compile-time typeof() calls instead of runtime Type.GetType().
  /// This avoids IL2057 trimming warnings by using static type references.
  /// NOTE: Deprecated in favor of JsonContextRegistry.GetTypeInfoByName for cross-assembly support.
  /// </summary>
  private static string _generateGetTypeInfoByName(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> allTypes, Compilation compilation) {
    var sb = new StringBuilder();

    // Get the actual assembly name from the compilation
    var actualAssemblyName = compilation.AssemblyName ?? "Unknown";

    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Gets JsonTypeInfo for a message type by its assembly-qualified name.");
    sb.AppendLine("/// Uses compile-time typeof() calls for AOT compatibility (avoids IL2057 trimming warnings).");
    sb.AppendLine("/// This method is generated per-assembly and knows only about types in this assembly.");
    sb.AppendLine("/// DEPRECATED: Use JsonContextRegistry.GetTypeInfoByName() instead for cross-assembly support.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("/// <param name=\"assemblyQualifiedTypeName\">Assembly-qualified type name (e.g., \"YourNamespace.Commands.CreateOrder, YourAssembly\")</param>");
    sb.AppendLine("/// <param name=\"options\">JsonSerializerOptions to use for creating JsonTypeInfo</param>");
    sb.AppendLine("/// <returns>JsonTypeInfo for the type, or null if not found in this assembly</returns>");
    sb.AppendLine("[System.Obsolete(\"Use JsonContextRegistry.GetTypeInfoByName() for cross-assembly type resolution with fuzzy matching support.\")]");
    sb.AppendLine("public static JsonTypeInfo? GetTypeInfoByName(string assemblyQualifiedTypeName, JsonSerializerOptions options) {");
    sb.AppendLine("  if (string.IsNullOrEmpty(assemblyQualifiedTypeName)) return null;");
    sb.AppendLine("  if (options == null) return null;");
    sb.AppendLine();
    sb.AppendLine("  // Create a temporary context instance to access GetTypeInfoInternal");
    sb.AppendLine("  var context = new MessageJsonContext(options);");
    sb.AppendLine();
    sb.AppendLine("  // Use switch expression for AOT-safe type name mapping");
    sb.AppendLine("  // Each case uses typeof() which is compile-time and AOT-safe");
    sb.AppendLine("  var typeInfo = assemblyQualifiedTypeName switch {");

    // Only generate mappings for actual message types (commands/events), not nested types
    var messageTypes = allTypes.Where(t => t.IsCommand || t.IsEvent).ToList();
    foreach (var type in messageTypes) {
      // Get assembly-qualified name using the ACTUAL compilation assembly name
      // FullyQualifiedName is like "global::MyApp.Commands.CreateOrder"
      // We need "MyApp.Commands.CreateOrder, MyApp.Contracts" (actual assembly name)
      var typeNameWithoutGlobal = type.FullyQualifiedName.Replace(PLACEHOLDER_GLOBAL, "");

      sb.AppendLine($"    \"{typeNameWithoutGlobal}, {actualAssemblyName}\" => context.GetTypeInfoInternal(typeof({type.FullyQualifiedName}), options),");
    }

    sb.AppendLine("    _ => (JsonTypeInfo?)null  // Type not found in this assembly");
    sb.AppendLine("  };");
    sb.AppendLine();
    sb.AppendLine("  return typeInfo;");
    sb.AppendLine("}");

    return sb.ToString();
  }

  private static string _generateCoreTypeFactories(Assembly assembly) {
    var sb = new System.Text.StringBuilder();

    // Load snippet
    var coreTypeFactorySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "CORE_TYPE_FACTORY");

    // MessageId factory - use custom AOT-compatible converter
    sb.AppendLine(coreTypeFactorySnippet.Replace(PLACEHOLDER_TYPE_NAME, PLACEHOLDER_MESSAGE_ID));
    sb.AppendLine();

    // CorrelationId factory - use custom AOT-compatible converter
    sb.AppendLine(coreTypeFactorySnippet.Replace("__TYPE_NAME__", "CorrelationId"));
    sb.AppendLine();

    // Infrastructure types will use default serialization - we only need special handling for MessageId and CorrelationId

    return sb.ToString();
  }

  private static string _generateMessageTypeFactories(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new StringBuilder();

    // Load snippets
    var propertyCreationSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "PROPERTY_CREATION_CALL");

    var parameterInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
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
            .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture))
            .Replace(PLACEHOLDER_PROPERTY_TYPE, prop.Type)
            .Replace(PLACEHOLDER_PROPERTY_NAME, prop.Name)
            .Replace(PLACEHOLDER_MESSAGE_TYPE, message.FullyQualifiedName)
            .Replace(PLACEHOLDER_SETTER, setter);

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
              .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture))
              .Replace(PLACEHOLDER_PARAMETER_NAME, prop.Name)
              .Replace(PLACEHOLDER_PROPERTY_TYPE, _getTypeOfExpression(prop));

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
              .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture))
              .Replace(PLACEHOLDER_PARAMETER_NAME, prop.Name)
              .Replace(PLACEHOLDER_PROPERTY_TYPE, _getTypeOfExpression(prop));

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

  private static string _generateMessageEnvelopeFactories(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new StringBuilder();

    // Load snippets
    var propertyCreationSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "PROPERTY_CREATION_CALL");

    var parameterInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "PARAMETER_INFO_VALUES");

    foreach (var message in messages) {
      sb.AppendLine($"private JsonTypeInfo<MessageEnvelope<{message.FullyQualifiedName}>> CreateMessageEnvelope_{message.SimpleName}(JsonSerializerOptions options) {{");

      // Generate properties array for MessageEnvelope<T> (MessageId, Payload, Hops)
      sb.AppendLine("  var properties = new JsonPropertyInfo[3];");
      sb.AppendLine();

      // Property 0: MessageId using snippet
      var messageIdProperty = propertyCreationSnippet
          .Replace(PLACEHOLDER_INDEX, "0")
          .Replace(PLACEHOLDER_PROPERTY_TYPE, "MessageId")
          .Replace(PLACEHOLDER_PROPERTY_NAME, "MessageId")
          .Replace(PLACEHOLDER_MESSAGE_TYPE, $"MessageEnvelope<{message.FullyQualifiedName}>")
          .Replace(PLACEHOLDER_SETTER, "null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine(messageIdProperty);
      sb.AppendLine();

      // Property 1: Payload using snippet
      var payloadProperty = propertyCreationSnippet
          .Replace(PLACEHOLDER_INDEX, "1")
          .Replace(PLACEHOLDER_PROPERTY_TYPE, message.FullyQualifiedName)
          .Replace(PLACEHOLDER_PROPERTY_NAME, "Payload")
          .Replace(PLACEHOLDER_MESSAGE_TYPE, $"MessageEnvelope<{message.FullyQualifiedName}>")
          .Replace(PLACEHOLDER_SETTER, "null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine(payloadProperty);
      sb.AppendLine();

      // Property 2: Hops using snippet
      var hopsProperty = propertyCreationSnippet
          .Replace(PLACEHOLDER_INDEX, "2")
          .Replace(PLACEHOLDER_PROPERTY_TYPE, "List<MessageHop>")
          .Replace(PLACEHOLDER_PROPERTY_NAME, "Hops")
          .Replace(PLACEHOLDER_MESSAGE_TYPE, $"MessageEnvelope<{message.FullyQualifiedName}>")
          .Replace(PLACEHOLDER_SETTER, "null,  // MessageEnvelope uses constructor, no setter needed");
      sb.AppendLine(hopsProperty);
      sb.AppendLine();

      // Constructor parameters using snippet
      sb.AppendLine("  var ctorParams = new JsonParameterInfoValues[3];");

      var messageIdParam = parameterInfoSnippet
          .Replace(PLACEHOLDER_INDEX, "0")
          .Replace(PLACEHOLDER_PARAMETER_NAME, "messageId")
          .Replace(PLACEHOLDER_PROPERTY_TYPE, "MessageId");
      sb.AppendLine(messageIdParam);

      var payloadParam = parameterInfoSnippet
          .Replace(PLACEHOLDER_INDEX, "1")
          .Replace(PLACEHOLDER_PARAMETER_NAME, "payload")
          .Replace(PLACEHOLDER_PROPERTY_TYPE, message.FullyQualifiedName);
      sb.AppendLine(payloadParam);

      var hopsParam = parameterInfoSnippet
          .Replace(PLACEHOLDER_INDEX, "2")
          .Replace(PLACEHOLDER_PARAMETER_NAME, "hops")
          .Replace(PLACEHOLDER_PROPERTY_TYPE, "List<MessageHop>");
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

  private static string _generateAssemblyAwareHelper(Assembly assembly, ImmutableArray<WhizbangIdTypeInfo> converters, ImmutableArray<JsonMessageTypeInfo> messages, Compilation compilation) {
    // Load snippet template
    var createOptionsSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "HELPER_CREATE_OPTIONS");

    // Get assembly name for type registrations
    var actualAssemblyName = compilation.AssemblyName ?? "Unknown";

    // Build all registrations using helper methods
    var registrations = new System.Text.StringBuilder();

    // Generate converter registrations (WhizbangId types)
    _generateConverterRegistrations(registrations, converters);

    // Generate message type registrations for cross-assembly resolution
    var messageTypes = messages.Where(m => m.IsCommand || m.IsEvent || m.IsSerializable).ToList();
    _generateMessageTypeRegistrations(registrations, messageTypes, actualAssemblyName);

    // Generate MessageEnvelope<T> wrapper type registrations for transport
    _generateEnvelopeTypeRegistrations(registrations, messageTypes, actualAssemblyName);

    // Replace placeholder and return
    return createOptionsSnippet.Replace("__CONVERTER_REGISTRATIONS__", registrations.ToString());
  }

  /// <summary>
  /// Discovers nested custom types used in message properties (e.g., OrderLineItem inside List&lt;OrderLineItem&gt;).
  /// These types need JsonTypeInfo generated for AOT serialization to work properly.
  /// </summary>
  private static ImmutableArray<JsonMessageTypeInfo> _discoverNestedTypes(
      ImmutableArray<JsonMessageTypeInfo> messages,
      Compilation compilation) {

    var nestedTypes = new Dictionary<string, JsonMessageTypeInfo>();

    foreach (var message in messages) {
      foreach (var property in message.Properties) {
        // Extract element type from generic collections
        var elementTypeName = _extractElementType(property.Type);
        if (elementTypeName == null) {
          continue;
        }

        // Check if this type should be skipped
        if (_shouldSkipNestedType(elementTypeName, nestedTypes, messages)) {
          continue;
        }

        // Try to get public type symbol
        var typeSymbol = _tryGetPublicTypeSymbol(elementTypeName, compilation);
        if (typeSymbol == null) {
          continue;
        }

        // Extract properties and detect constructor
        var nestedProperties = _extractPropertiesFromType(typeSymbol);
        bool hasParameterizedConstructor = _hasMatchingParameterizedConstructor(typeSymbol, nestedProperties);

        // Build nested type info
        var nestedTypeInfo = new JsonMessageTypeInfo(
            FullyQualifiedName: elementTypeName,
            SimpleName: typeSymbol.Name,
            IsCommand: false,  // Nested types are not commands/events
            IsEvent: false,
            IsSerializable: false,  // Nested types discovered through property analysis, not attribute
            Properties: nestedProperties,
            HasParameterizedConstructor: hasParameterizedConstructor
        );

        nestedTypes[elementTypeName] = nestedTypeInfo;
      }
    }

    return nestedTypes.Values.ToImmutableArray();
  }

  /// <summary>
  /// Extracts the element type from a generic collection type.
  /// For example: "global::System.Collections.Generic.List&lt;global::MyApp.OrderLineItem&gt;" returns "global::MyApp.OrderLineItem"
  /// </summary>
  private static string? _extractElementType(string fullyQualifiedTypeName) {
    // Check for common generic collection types
    var genericTypes = new[] {
      "global::System.Collections.Generic.List<",
      "global::System.Collections.Generic.IList<",
      "global::System.Collections.Generic.IReadOnlyList<",
      "global::System.Collections.Generic.ICollection<",
      "global::System.Collections.Generic.IReadOnlyCollection<",
      "global::System.Collections.Generic.IEnumerable<"
    };

    foreach (var genericPrefix in genericTypes) {
      if (fullyQualifiedTypeName.StartsWith(genericPrefix, StringComparison.Ordinal)) {
        // Extract the type argument between < and >
        var startIndex = genericPrefix.Length;
        var endIndex = fullyQualifiedTypeName.LastIndexOf('>');
        if (endIndex > startIndex) {
          return fullyQualifiedTypeName[startIndex..endIndex];
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Checks if a type is a primitive or framework type that doesn't need custom JsonTypeInfo.
  /// </summary>
  private static bool _isPrimitiveOrFrameworkType(string fullyQualifiedTypeName) {
    var frameworkTypes = new[] {
      "global::System.String",
      "global::System.Int32",
      "global::System.Int64",
      "global::System.Decimal",
      "global::System.Double",
      "global::System.Single",
      "global::System.Boolean",
      "global::System.DateTime",
      "global::System.DateTimeOffset",
      "global::System.TimeSpan",
      "global::System.Guid",
      "global::System.Byte",
      "global::System.SByte",
      "global::System.Int16",
      "global::System.UInt16",
      "global::System.UInt32",
      "global::System.UInt64",
      "global::System.Char"
    };

    return frameworkTypes.Contains(fullyQualifiedTypeName);
  }

  /// <summary>
  /// Discovers WhizbangId JSON converters by examining property types in messages.
  /// Infers converter names for types that look like ID types (e.g., ProductId -> ProductIdJsonConverter).
  /// Uses naming conventions since source generators run in parallel and generated types may not be visible.
  /// Returns info about converters that need to be registered in JsonSerializerOptions.
  /// </summary>
  private static ImmutableArray<WhizbangIdTypeInfo> _discoverWhizbangIdConverters(
      ImmutableArray<JsonMessageTypeInfo> allTypes,
      Compilation compilation) {

    var converters = new Dictionary<string, WhizbangIdTypeInfo>();

    foreach (var type in allTypes) {
      foreach (var property in type.Properties) {
        // Skip primitive and framework types
        if (_isPrimitiveOrFrameworkType(property.Type)) {
          continue;
        }

        // Skip collection types
        if (_extractElementType(property.Type) != null) {
          continue;
        }

        // Extract simple type name from fully qualified name
        // e.g., "global::ECommerce.Contracts.Commands.ProductId" -> "ProductId"
        var parts = property.Type.Replace(PLACEHOLDER_GLOBAL, "").Split('.');
        var typeName = parts[^1];

        // Heuristic: If type name ends with "Id", it's likely a WhizbangId type with a generated converter
        // This includes types like ProductId, OrderId, CustomerId, MessageId, CorrelationId, etc.
        if (!typeName.EndsWith("Id", StringComparison.Ordinal)) {
          continue;
        }

        // Infer converter name: {TypeName}JsonConverter
        var converterTypeName = $"{typeName}JsonConverter";

        // Check if converter already discovered
        if (converters.ContainsKey(converterTypeName)) {
          continue;
        }

        // Infer converter namespace (same as the property type)
        var propertyTypeNamespace = string.Join(".", parts.Take(parts.Length - 1));
        var converterFullName = $"{propertyTypeNamespace}.{converterTypeName}";

        // Add the converter (optimistic registration - if it doesn't exist, compilation will fail with clear error)
        converters[converterTypeName] = new WhizbangIdTypeInfo(
            TypeName: converterTypeName,
            FullyQualifiedTypeName: converterFullName
        );
      }
    }

    return converters.Values.ToImmutableArray();
  }

  /// <summary>
  /// Discovers List&lt;T&gt; types used in message properties.
  /// Returns info needed to generate explicit List&lt;T&gt; JsonTypeInfo for AOT compatibility.
  /// </summary>
  private static ImmutableArray<ListTypeInfo> _discoverListTypes(ImmutableArray<JsonMessageTypeInfo> allTypes) {
    var listTypes = new Dictionary<string, ListTypeInfo>();

    foreach (var type in allTypes) {
      foreach (var property in type.Properties) {
        var elementTypeName = _extractElementType(property.Type);
        if (elementTypeName == null) {
          continue;
        }

        // Create key: List<ElementType>
        var listTypeName = $"global::System.Collections.Generic.List<{elementTypeName}>";
        if (listTypes.ContainsKey(listTypeName)) {
          continue;
        }

        // Extract simple name from fully qualified element type
        var parts = elementTypeName.Split('.');
        var elementSimpleName = parts[^1].Replace(PLACEHOLDER_GLOBAL, "");

        listTypes[listTypeName] = new ListTypeInfo(
            ListTypeName: listTypeName,
            ElementTypeName: elementTypeName,
            ElementSimpleName: elementSimpleName
        );
      }
    }

    return listTypes.Values.ToImmutableArray();
  }

  /// <summary>
  /// Generates lazy fields for List&lt;T&gt; types.
  /// </summary>
  private static string _generateListLazyFields(Assembly assembly, ImmutableArray<ListTypeInfo> listTypes) {
    if (listTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();

    // Suppress CS0169 warning for unused fields (fields are reserved for future lazy initialization)
    sb.AppendLine("#pragma warning disable CS0169  // Field is never used");
    sb.AppendLine();

    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "LAZY_FIELD_LIST");

    foreach (var listType in listTypes) {
      var field = snippet
          .Replace("__ELEMENT_TYPE__", listType.ElementTypeName)
          .Replace("__ELEMENT_SIMPLE_NAME__", listType.ElementSimpleName);
      sb.AppendLine(field);
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }


  /// <summary>
  /// Generates factory methods for List&lt;T&gt; types.
  /// </summary>
  private static string _generateListFactories(Assembly assembly, ImmutableArray<ListTypeInfo> listTypes) {
    if (listTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();
    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "LIST_TYPE_FACTORY");

    foreach (var listType in listTypes) {
      var factory = snippet
          .Replace("__ELEMENT_TYPE__", listType.ElementTypeName)
          .Replace("__ELEMENT_SIMPLE_NAME__", listType.ElementSimpleName);
      sb.AppendLine(factory);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  // ========================================
  // Helper Methods for _discoverNestedTypes Complexity Reduction
  // ========================================

  /// <summary>
  /// Checks if a nested type should be skipped during discovery.
  /// </summary>
  private static bool _shouldSkipNestedType(
      string elementTypeName,
      Dictionary<string, JsonMessageTypeInfo> nestedTypes,
      ImmutableArray<JsonMessageTypeInfo> messages) {

    // Skip if already discovered
    if (nestedTypes.ContainsKey(elementTypeName)) {
      return true;
    }

    // Skip if it's already a message type
    if (messages.Any(m => m.FullyQualifiedName == elementTypeName)) {
      return true;
    }

    // Skip primitive and framework types
    if (_isPrimitiveOrFrameworkType(elementTypeName)) {
      return true;
    }

    return false;
  }

  /// <summary>
  /// Attempts to get a public type symbol from the compilation.
  /// Returns null if type doesn't exist or isn't public.
  /// </summary>
  private static INamedTypeSymbol? _tryGetPublicTypeSymbol(string elementTypeName, Compilation compilation) {
    var typeSymbol = compilation.GetTypeByMetadataName(elementTypeName.Replace(PLACEHOLDER_GLOBAL, ""));
    if (typeSymbol == null) {
      return null;
    }

    // Skip non-public types
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    return typeSymbol;
  }

  /// <summary>
  /// Extracts property information from a type symbol.
  /// </summary>
  private static PropertyInfo[] _extractPropertiesFromType(INamedTypeSymbol typeSymbol) {
    return typeSymbol.GetMembers()
        .OfType<IPropertySymbol>()
        .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic)
        .Select(p => new PropertyInfo(
            Name: p.Name,
            Type: p.Type.ToDisplayString(_fullyQualifiedWithNullabilityFormat),
            IsValueType: _isValueType(p.Type),
            IsInitOnly: p.SetMethod?.IsInitOnly ?? false
        ))
        .ToArray();
  }

  /// <summary>
  /// Checks if a type has a parameterized constructor matching its properties.
  /// </summary>
  private static bool _hasMatchingParameterizedConstructor(INamedTypeSymbol typeSymbol, PropertyInfo[] properties) {
    return typeSymbol.Constructors.Any(c =>
        c.DeclaredAccessibility == Accessibility.Public &&
        c.Parameters.Length == properties.Length &&
        c.Parameters.All(p => properties.Any(prop =>
            prop.Name.Equals(p.Name, System.StringComparison.OrdinalIgnoreCase))));
  }

  // ========================================
  // Helper Methods for _generateAssemblyAwareHelper Complexity Reduction
  // ========================================

  /// <summary>
  /// Generates converter registration code for ModuleInitializer.
  /// </summary>
  private static void _generateConverterRegistrations(
      System.Text.StringBuilder sb,
      ImmutableArray<WhizbangIdTypeInfo> converters) {

    if (converters.IsEmpty) {
      return;
    }

    foreach (var converter in converters) {
      sb.AppendLine($"  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterConverter(new global::{converter.FullyQualifiedTypeName}());");
    }
  }

  /// <summary>
  /// Generates message type registration code for cross-assembly resolution.
  /// </summary>
  private static void _generateMessageTypeRegistrations(
      System.Text.StringBuilder sb,
      List<JsonMessageTypeInfo> messageTypes,
      string actualAssemblyName) {

    if (messageTypes.Count == 0) {
      return;
    }

    sb.AppendLine();
    sb.AppendLine("  // Register type name mappings for cross-assembly resolution");

    foreach (var message in messageTypes) {
      var typeNameWithoutGlobal = message.FullyQualifiedName.Replace(PLACEHOLDER_GLOBAL, "");
      var assemblyQualifiedName = $"{typeNameWithoutGlobal}, {actualAssemblyName}";

      sb.AppendLine($"  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterTypeName(");
      sb.AppendLine($"    \"{assemblyQualifiedName}\",");
      sb.AppendLine($"    typeof({message.FullyQualifiedName}),");
      sb.AppendLine($"    MessageJsonContext.Default);");
    }
  }

  /// <summary>
  /// Generates MessageEnvelope wrapper type registrations for transport deserialization.
  /// </summary>
  private static void _generateEnvelopeTypeRegistrations(
      System.Text.StringBuilder sb,
      List<JsonMessageTypeInfo> messageTypes,
      string actualAssemblyName) {

    if (messageTypes.Count == 0) {
      return;
    }

    sb.AppendLine();
    sb.AppendLine("  // Register MessageEnvelope<T> wrapper types for transport deserialization");

    foreach (var message in messageTypes) {
      var typeNameWithoutGlobal = message.FullyQualifiedName.Replace(PLACEHOLDER_GLOBAL, "");
      var envelopeTypeName = $"Whizbang.Core.Observability.MessageEnvelope`1[[{typeNameWithoutGlobal}, {actualAssemblyName}]], Whizbang.Core";

      sb.AppendLine($"  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterTypeName(");
      sb.AppendLine($"    \"{envelopeTypeName}\",");
      sb.AppendLine($"    typeof(global::Whizbang.Core.Observability.MessageEnvelope<{message.FullyQualifiedName}>),");
      sb.AppendLine($"    MessageJsonContext.Default);");
    }
  }
}
