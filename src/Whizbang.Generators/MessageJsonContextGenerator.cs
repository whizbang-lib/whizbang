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
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithGetOnlyProperty_UsesNullSetterAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithRecordStructNestedType_DiscoversStructAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithReadonlyRecordStruct_UsesConstructorInitializationAsync</tests>
/// <docs>source-generators/json-contexts</docs>
/// Source generator that discovers message types (ICommand, IEvent) and generates
/// WhizbangJsonContext with JsonTypeInfo for AOT-compatible serialization.
/// This context handles message types discovered in the current assembly.
/// Use with WhizbangInfrastructureJsonContext for complete Whizbang serialization support.
/// </summary>
[Generator]
public class MessageJsonContextGenerator : IIncrementalGenerator {
  private const string I_COMMAND = "Whizbang.Core.ICommand";
  private const string I_EVENT = "Whizbang.Core.IEvent";
  private const string I_PERSPECTIVE_FOR = "Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string GRAPHQL_NAME_ATTRIBUTE = "HotChocolate.GraphQLNameAttribute";
  private const string WHIZBANG_ID_ATTRIBUTE = "Whizbang.Core.WhizbangIdAttribute";
  private const string WHIZBANG_SERIALIZABLE = "Whizbang.WhizbangSerializableAttribute";

  // Template placeholders
  private const string TEMPLATE_SNIPPET_FILE = "JsonContextSnippets.cs";
  private const string PLACEHOLDER_TYPE_NAME = "__TYPE_NAME__";
  private const string PLACEHOLDER_MESSAGE_ID = "MessageId";
  private const string PLACEHOLDER_FULLY_QUALIFIED_NAME = "__FULLY_QUALIFIED_NAME__";
  private const string PLACEHOLDER_SIMPLE_NAME = "__SIMPLE_NAME__";
  private const string PLACEHOLDER_UNIQUE_IDENTIFIER = "__UNIQUE_IDENTIFIER__";
  private const string PLACEHOLDER_GLOBAL = "global::";
  private const string PLACEHOLDER_INDEX = "__INDEX__";
  private const string PLACEHOLDER_PROPERTY_TYPE = "__PROPERTY_TYPE__";
  private const string PLACEHOLDER_PROPERTY_NAME = "__PROPERTY_NAME__";
  private const string PLACEHOLDER_MESSAGE_TYPE = "__MESSAGE_TYPE__";
  private const string PLACEHOLDER_SETTER = "__SETTER__";
  private const string PLACEHOLDER_PARAMETER_NAME = "__PARAMETER_NAME__";

  /// <summary>
  /// Converts a fully qualified type name to a safe C# identifier for use in method names.
  /// This ensures unique method names even when multiple namespaces have types with the same simple name.
  /// Example: "global::JDX.Contracts.Job.CreateCommand" → "JDX_Contracts_Job_CreateCommand"
  /// Example: "string?" → "string_Nullable"
  /// </summary>
  private static string _toSafeMethodName(string fullyQualifiedName) {
    return fullyQualifiedName
        .Replace(PLACEHOLDER_GLOBAL, "")
        .Replace(".", "_")
        .Replace("?", "_Nullable");
  }

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover message types (commands, events, and types with [WhizbangSerializable])
    // Predicate includes:
    // 1. Records/classes with base types (ICommand, IEvent, IPerspectiveFor, etc.)
    // 2. Records/classes with attributes ([WhizbangSerializable], [GraphQLName], etc.)
    // 3. Nested records/classes (potential perspective models like ChatSession.ChatSessionModel)
    var messageTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
            (node is RecordDeclarationSyntax rec &&
                (rec.BaseList?.Types.Count > 0 || rec.AttributeLists.Count > 0 || rec.Parent is TypeDeclarationSyntax)) ||
            (node is ClassDeclarationSyntax cls &&
                (cls.BaseList?.Types.Count > 0 || cls.AttributeLists.Count > 0 || cls.Parent is TypeDeclarationSyntax)),
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
  private static readonly SymbolDisplayFormat _fullyQualifiedWithNullabilityFormat = new(
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
  /// Gets the CLR-format type name for runtime type resolution.
  /// CLR uses "+" for nested types instead of "." (which is C# syntax).
  /// E.g., "MyApp.AuthContracts+LoginCommand" instead of "MyApp.AuthContracts.LoginCommand"
  /// </summary>
  /// <param name="symbol">The type symbol</param>
  /// <returns>CLR-format type name without global:: prefix</returns>
  private static string _getClrTypeName(INamedTypeSymbol symbol) {
    if (symbol.ContainingType != null) {
      // Nested type - use + separator (CLR format)
      return _getClrTypeName(symbol.ContainingType) + "+" + symbol.Name;
    }

    if (!symbol.ContainingNamespace.IsGlobalNamespace) {
      return symbol.ContainingNamespace.ToDisplayString() + "." + symbol.Name;
    }

    return symbol.Name;
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
  /// Returns null if the node is not a serializable type.
  /// Discovers: ICommand, IEvent, [WhizbangSerializable], [GraphQLName], and perspective model types.
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

    // Check if marked with [GraphQLName] attribute (implies GraphQL serialization needed)
    bool hasGraphQLName = typeSymbol.GetAttributes()
        .Any(a => a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{GRAPHQL_NAME_ATTRIBUTE}");

    // Check if this type is a perspective model (used as TModel in IPerspectiveFor<TModel, ...>)
    // Look for sibling or nested types that implement IPerspectiveFor<ThisType, ...>
    bool isPerspectiveModel = _isPerspectiveModelType(typeSymbol);

    // Type must be a command, event, explicitly marked as serializable, has GraphQL attribute, or is a perspective model
    if (!isCommand && !isEvent && !isSerializable && !hasGraphQLName && !isPerspectiveModel) {
      return null;
    }

    var fullyQualifiedName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var clrTypeName = _getClrTypeName(typeSymbol);
    var simpleName = typeSymbol.Name;

    // Extract property information for JSON serialization, including inherited properties
    // Use custom format that includes nullability annotations to avoid CS8619/CS8603 warnings
    var properties = _getAllPropertiesIncludingInherited(typeSymbol)
        .Select(p => new PropertyInfo(
            Name: p.Name,
            Type: p.Type.ToDisplayString(_fullyQualifiedWithNullabilityFormat),
            IsValueType: _isValueType(p.Type),
            IsInitOnly: p.SetMethod?.IsInitOnly ?? false,
            CanWrite: p.SetMethod != null
        ))
        .ToArray();

    // Detect if type has a parameterized constructor matching all writable properties
    // This is true for records with primary constructors like: record MyRecord(string Prop1, int Prop2)
    // This is false for records with required properties like: record MyRecord { public required string Prop1 { get; init; } }
    // Computed properties (CanWrite = false) are excluded from constructor matching
    var writableProperties = properties.Where(p => p.CanWrite).ToArray();
    bool hasParameterizedConstructor = typeSymbol.Constructors.Any(c =>
        c.DeclaredAccessibility == Accessibility.Public &&
        c.Parameters.Length == writableProperties.Length &&
        c.Parameters.All(p => writableProperties.Any(prop =>
            prop.Name.Equals(p.Name, System.StringComparison.OrdinalIgnoreCase))));

    return new JsonMessageTypeInfo(
        FullyQualifiedName: fullyQualifiedName,
        ClrTypeName: clrTypeName,
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
    // Also discovers polymorphic base types with [JsonPolymorphic] attribute from property types
    var (nestedTypes, propertyPolymorphicTypes) = _discoverNestedTypes(messages, compilation);

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

    // Discover IReadOnlyList<T> types used in all messages and nested types
    var iReadOnlyListTypes = _discoverIReadOnlyListTypes(allTypes);

    // Report diagnostics for discovered IReadOnlyList types
    foreach (var iReadOnlyListType in iReadOnlyListTypes) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          $"IReadOnlyList<{iReadOnlyListType.ElementSimpleName}>",
          "collection interface type"
      ));
    }

    // Discover array types (T[]) used in all messages and nested types
    var arrayTypes = _discoverArrayTypes(allTypes);

    // Report diagnostics for discovered array types
    foreach (var arrayType in arrayTypes) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          $"{arrayType.ElementSimpleName}[]",
          "array type"
      ));
    }

    // Discover Dictionary<TKey, TValue> types used in all messages and nested types
    var dictionaryTypes = _discoverDictionaryTypes(allTypes);

    // Report diagnostics for discovered dictionary types
    foreach (var dictType in dictionaryTypes) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          $"Dictionary<{dictType.KeyTypeName}, {dictType.ValueSimpleName}>",
          "dictionary type"
      ));
    }

    // Discover enum types used in message and nested type properties
    var enumTypes = _discoverEnumTypes(allTypes, compilation);

    // Report diagnostics for discovered enum types
    foreach (var enumType in enumTypes) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.JsonSerializableTypeDiscovered,
          Location.None,
          enumType.SimpleName,
          "enum type"
      ));
    }

    // Discover polymorphic base types from inheritance relationships (message types)
    var allInheritanceInfo = _collectAllInheritanceInfo(messages, compilation);
    var messagePolymorphicTypes = _buildPolymorphicRegistry(allInheritanceInfo, compilation);

    // Merge message-derived and property-derived polymorphic types
    // Property-derived types come from nested type discovery (e.g., AbstractFieldSettings with [JsonPolymorphic])
    // Use dictionary to deduplicate by BaseTypeName (netstandard2.0 doesn't have DistinctBy)
    var polymorphicTypeDict = new Dictionary<string, PolymorphicTypeInfo>();
    foreach (var polyType in messagePolymorphicTypes) {
      polymorphicTypeDict[polyType.BaseTypeName] = polyType;
    }
    foreach (var polyType in propertyPolymorphicTypes) {
      // Property-derived types take precedence (they have [JsonDerivedType] attributes)
      polymorphicTypeDict[polyType.BaseTypeName] = polyType;
    }
    var polymorphicTypes = polymorphicTypeDict.Values.ToImmutableArray();

    // Report diagnostics for discovered polymorphic types
    foreach (var polyType in polymorphicTypes) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.PolymorphicBaseTypeDiscovered,
          Location.None,
          polyType.BaseSimpleName,
          polyType.DerivedTypes.Length
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

    // Generate lazy fields (messages + nested types + lists + ireadonlylists + arrays + dictionaries + enums + polymorphic)
    var lazyFields = new System.Text.StringBuilder();
    lazyFields.Append(_generateLazyFields(assembly, allTypes));
    lazyFields.Append(_generateListLazyFields(assembly, listTypes));
    lazyFields.Append(_generateIReadOnlyListLazyFields(assembly, iReadOnlyListTypes));
    lazyFields.Append(_generateArrayLazyFields(assembly, arrayTypes));
    lazyFields.Append(_generateDictionaryLazyFields(assembly, dictionaryTypes));
    lazyFields.Append(_generateEnumLazyFields(assembly, enumTypes));
    lazyFields.Append(_generatePolymorphicLazyFields(assembly, polymorphicTypes));

    // Generate factory methods (messages + lists + ireadonlylists + arrays + dictionaries + enums + polymorphic)
    var factories = new System.Text.StringBuilder();
    factories.Append(_generateMessageTypeFactories(assembly, allTypes));
    factories.Append(_generateListFactories(assembly, listTypes));
    factories.Append(_generateIReadOnlyListFactories(assembly, iReadOnlyListTypes));
    factories.Append(_generateArrayFactories(assembly, arrayTypes));
    factories.Append(_generateDictionaryFactories(assembly, dictionaryTypes));
    factories.Append(_generateEnumFactories(assembly, enumTypes));
    factories.Append(_generatePolymorphicFactories(assembly, polymorphicTypes));

    // Discover WhizbangId converters by examining message property types
    var converters = _discoverWhizbangIdConverters(allTypes);

    // Generate and replace each region
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_FIELDS", lazyFields.ToString());
    template = TemplateUtilities.ReplaceRegion(template, "LAZY_PROPERTIES", _generateInterfaceProperties(assembly, allTypes));
    template = TemplateUtilities.ReplaceRegion(template, "ASSEMBLY_AWARE_HELPER", _generateAssemblyAwareHelper(assembly, converters, messages, compilation));
    template = TemplateUtilities.ReplaceRegion(template, "GET_DISCOVERED_TYPE_INFO", _generateGetTypeInfo(assembly, allTypes, listTypes, iReadOnlyListTypes, arrayTypes, dictionaryTypes, enumTypes, polymorphicTypes));
    template = TemplateUtilities.ReplaceRegion(template, "HELPER_METHODS", _generateHelperMethods(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "GET_TYPE_INFO_BY_NAME", _generateGetTypeInfoByName(allTypes, compilation));
    template = TemplateUtilities.ReplaceRegion(template, "CORE_TYPE_FACTORIES", _generateCoreTypeFactories(assembly));
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_TYPE_FACTORIES", factories.ToString());
    template = TemplateUtilities.ReplaceRegion(template, "MESSAGE_ENVELOPE_FACTORIES", _generateMessageEnvelopeFactories(assembly, messages));

    context.AddSource("MessageJsonContext.g.cs", template);

    // Always generate WhizbangJsonContext facade
    {
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

    // Generate MessageJsonContextInitializer with RegisterDerivedType calls for polymorphic serialization
    {
      var initializerTemplate = TemplateUtilities.GetEmbeddedTemplate(assembly, "MessageJsonContextInitializerTemplate.cs");
      initializerTemplate = TemplateUtilities.ReplaceHeaderRegion(assembly, initializerTemplate);
      initializerTemplate = initializerTemplate.Replace("__NAMESPACE__", namespaceName);

      // Generate RegisterDerivedType calls for each message type
      var derivedTypeRegistrations = _generateDerivedTypeRegistrations(messages);
      initializerTemplate = TemplateUtilities.ReplaceRegion(initializerTemplate, "DERIVED_TYPE_REGISTRATIONS", derivedTypeRegistrations);

      context.AddSource("MessageJsonContextInitializer.g.cs", initializerTemplate);
    }
  }

  /// <summary>
  /// Generates RegisterDerivedType calls for all discovered message types.
  /// Each event type is registered with IEvent, each command type with ICommand.
  /// Uses fully qualified type name as discriminator to avoid collisions when
  /// multiple types have the same simple name in different namespaces.
  /// </summary>
  private static string _generateDerivedTypeRegistrations(ImmutableArray<JsonMessageTypeInfo> messages) {
    var sb = new System.Text.StringBuilder();

    foreach (var message in messages) {
      // Use fully qualified name without global:: prefix for discriminator to ensure uniqueness
      var discriminator = message.FullyQualifiedName.Replace("global::", "");

      if (message.IsEvent) {
        sb.AppendLine($"    JsonContextRegistry.RegisterDerivedType<global::Whizbang.Core.IEvent, {message.FullyQualifiedName}>(\"{discriminator}\");");
      }

      if (message.IsCommand) {
        sb.AppendLine($"    JsonContextRegistry.RegisterDerivedType<global::Whizbang.Core.ICommand, {message.FullyQualifiedName}>(\"{discriminator}\");");
      }

      // All message types (events and commands) are also IMessage
      if (message.IsEvent || message.IsCommand) {
        sb.AppendLine($"    JsonContextRegistry.RegisterDerivedType<global::Whizbang.Core.IMessage, {message.FullyQualifiedName}>(\"{discriminator}\");");
      }
    }

    return sb.ToString();
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
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, type.UniqueIdentifier);
      sb.AppendLine(field);
    }
    sb.AppendLine();

    // MessageEnvelope<T> ONLY for actual message types (commands/events), not nested types
    foreach (var type in allTypes.Where(t => t.IsCommand || t.IsEvent)) {
      var field = envelopeFieldSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, type.FullyQualifiedName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, type.UniqueIdentifier);
      sb.AppendLine(field);
    }
    sb.AppendLine();

    // Interface lazy fields - always generate for IEvent, ICommand, IMessage
    // These delegate to JsonContextRegistry which aggregates derived types from all assemblies
    var hasEvents = allTypes.Any(t => t.IsEvent);
    var hasCommands = allTypes.Any(t => t.IsCommand);

    if (hasEvents || hasCommands) {
      var interfaceFieldSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          TEMPLATE_SNIPPET_FILE,
          "LAZY_FIELD_INTERFACE");
      var messageEnvelopeInterfaceFieldSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          TEMPLATE_SNIPPET_FILE,
          "LAZY_FIELD_MESSAGE_ENVELOPE_INTERFACE");
      var listInterfaceFieldSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          TEMPLATE_SNIPPET_FILE,
          "LAZY_FIELD_LIST_INTERFACE");

      sb.AppendLine("  // Interface lazy fields for polymorphic serialization");

      // IMessage (always if we have any messages)
      sb.AppendLine(interfaceFieldSnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
          .Replace("__INTERFACE_NAME__", "IMessage"));
      sb.AppendLine(messageEnvelopeInterfaceFieldSnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
          .Replace("__INTERFACE_NAME__", "IMessage"));
      sb.AppendLine(listInterfaceFieldSnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
          .Replace("__INTERFACE_NAME__", "IMessage"));

      // IEvent (only if we have events)
      if (hasEvents) {
        sb.AppendLine(interfaceFieldSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
            .Replace("__INTERFACE_NAME__", "IEvent"));
        sb.AppendLine(messageEnvelopeInterfaceFieldSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
            .Replace("__INTERFACE_NAME__", "IEvent"));
        sb.AppendLine(listInterfaceFieldSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
            .Replace("__INTERFACE_NAME__", "IEvent"));
      }

      // ICommand (only if we have commands)
      if (hasCommands) {
        sb.AppendLine(interfaceFieldSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
            .Replace("__INTERFACE_NAME__", "ICommand"));
        sb.AppendLine(messageEnvelopeInterfaceFieldSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
            .Replace("__INTERFACE_NAME__", "ICommand"));
        sb.AppendLine(listInterfaceFieldSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
            .Replace("__INTERFACE_NAME__", "ICommand"));
      }
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }

  /// <summary>
  /// Generates interface properties that delegate to JsonContextRegistry for polymorphic serialization.
  /// Properties are generated for IEvent, ICommand, IMessage and their MessageEnvelope/List variants.
  /// </summary>
  private static string _generateInterfaceProperties(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> allTypes) {
    var sb = new StringBuilder();

    var hasEvents = allTypes.Any(t => t.IsEvent);
    var hasCommands = allTypes.Any(t => t.IsCommand);

    if (!hasEvents && !hasCommands) {
      sb.AppendLine("// No message types discovered - interface properties not generated");
      return sb.ToString();
    }

    var interfacePropertySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "INTERFACE_PROPERTY");
    var messageEnvelopeInterfacePropertySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "MESSAGE_ENVELOPE_INTERFACE_PROPERTY");
    var listInterfacePropertySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "LIST_INTERFACE_PROPERTY");

    sb.AppendLine("// Interface properties for polymorphic serialization");
    sb.AppendLine("// These delegate to JsonContextRegistry which aggregates derived types from all assemblies");
    sb.AppendLine();

    // IMessage (always if we have any messages)
    sb.AppendLine(interfacePropertySnippet
        .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
        .Replace("__INTERFACE_NAME__", "IMessage"));
    sb.AppendLine(messageEnvelopeInterfacePropertySnippet
        .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
        .Replace("__INTERFACE_NAME__", "IMessage"));
    sb.AppendLine(listInterfacePropertySnippet
        .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
        .Replace("__INTERFACE_NAME__", "IMessage"));

    // IEvent (only if we have events)
    if (hasEvents) {
      sb.AppendLine(interfacePropertySnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
          .Replace("__INTERFACE_NAME__", "IEvent"));
      sb.AppendLine(messageEnvelopeInterfacePropertySnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
          .Replace("__INTERFACE_NAME__", "IEvent"));
      sb.AppendLine(listInterfacePropertySnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
          .Replace("__INTERFACE_NAME__", "IEvent"));
    }

    // ICommand (only if we have commands)
    if (hasCommands) {
      sb.AppendLine(interfacePropertySnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
          .Replace("__INTERFACE_NAME__", "ICommand"));
      sb.AppendLine(messageEnvelopeInterfacePropertySnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
          .Replace("__INTERFACE_NAME__", "ICommand"));
      sb.AppendLine(listInterfacePropertySnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
          .Replace("__INTERFACE_NAME__", "ICommand"));
    }

    return sb.ToString();
  }

  private static string _generateGetTypeInfo(Assembly assembly, ImmutableArray<JsonMessageTypeInfo> allTypes, ImmutableArray<ListTypeInfo> listTypes, ImmutableArray<IReadOnlyListTypeInfo> iReadOnlyListTypes, ImmutableArray<ArrayTypeInfo> arrayTypes, ImmutableArray<DictionaryTypeInfo> dictionaryTypes, ImmutableArray<JsonEnumInfo> enumTypes, ImmutableArray<PolymorphicTypeInfo> polymorphicTypes) {
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

    var iReadOnlyListCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_IREADONLYLIST");

    var arrayCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_ARRAY");

    var dictionaryCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_DICTIONARY");

    var enumCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_ENUM");

    var nullableEnumCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_NULLABLE_ENUM");

    // Implement IJsonTypeInfoResolver.GetTypeInfo(Type, JsonSerializerOptions)
    // Must track types being created to detect circular references
    sb.AppendLine("JsonTypeInfo? IJsonTypeInfoResolver.GetTypeInfo(Type type, JsonSerializerOptions options) {");
    sb.AppendLine("  // Check for circular reference - if we're already creating this type, return null");
    sb.AppendLine("  // to let the resolver chain try other resolvers and break the recursion");
    sb.AppendLine("  if (TypesBeingCreated.Contains(type)) {");
    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  TypesBeingCreated.Add(type);");
    sb.AppendLine("  try {");
    sb.AppendLine("    return GetTypeInfoInternal(type, options);");
    sb.AppendLine("  } finally {");
    sb.AppendLine("    TypesBeingCreated.Remove(type);");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();

    // Override base GetTypeInfo(Type) for compatibility
    sb.AppendLine("public override JsonTypeInfo? GetTypeInfo(Type type) {");
    sb.AppendLine("  // When called directly (not in resolver chain), Options might be null");
    sb.AppendLine("  if (Options == null) return null;");
    sb.AppendLine();
    sb.AppendLine("  // Check for circular reference");
    sb.AppendLine("  if (TypesBeingCreated.Contains(type)) {");
    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  TypesBeingCreated.Add(type);");
    sb.AppendLine("  try {");
    sb.AppendLine("    return GetTypeInfoInternal(type, Options);");
    sb.AppendLine("  } finally {");
    sb.AppendLine("    TypesBeingCreated.Remove(type);");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();

    // Shared implementation
    sb.AppendLine("private JsonTypeInfo? GetTypeInfoInternal(Type type, JsonSerializerOptions options) {");
    sb.AppendLine("  // Core Whizbang value objects with custom converters");
    sb.AppendLine(valueObjectCheckSnippet.Replace(PLACEHOLDER_TYPE_NAME, PLACEHOLDER_MESSAGE_ID));
    sb.AppendLine(valueObjectCheckSnippet.Replace(PLACEHOLDER_TYPE_NAME, "CorrelationId"));
    sb.AppendLine();

    // Primitive types - create directly using JsonMetadataServices (AOT-compatible)
    // IMPORTANT: Do NOT delegate to GetOrCreateTypeInfo<T>() here because the caller
    // (IJsonTypeInfoResolver.GetTypeInfo) has already added this type to TypesBeingCreated,
    // which would trigger a false "circular reference" error in GetOrCreateTypeInfo.
    sb.AppendLine("  // Primitive types (common property types in messages)");
    sb.AppendLine("  // Create directly using JsonMetadataServices - do NOT use GetOrCreateTypeInfo to avoid false circular reference detection");
    sb.AppendLine("  if (type == typeof(string)) return JsonMetadataServices.CreateValueInfo<string>(options, JsonMetadataServices.StringConverter);");
    sb.AppendLine("  if (type == typeof(int)) return JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);");
    sb.AppendLine("  if (type == typeof(long)) return JsonMetadataServices.CreateValueInfo<long>(options, JsonMetadataServices.Int64Converter);");
    sb.AppendLine("  if (type == typeof(bool)) return JsonMetadataServices.CreateValueInfo<bool>(options, JsonMetadataServices.BooleanConverter);");
    sb.AppendLine("  if (type == typeof(Guid)) return JsonMetadataServices.CreateValueInfo<Guid>(options, JsonMetadataServices.GuidConverter);");
    sb.AppendLine("  if (type == typeof(DateTime)) return JsonMetadataServices.CreateValueInfo<DateTime>(options, JsonMetadataServices.DateTimeConverter);");
    sb.AppendLine("  if (type == typeof(DateTimeOffset)) return JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, new global::Whizbang.Core.Serialization.LenientDateTimeOffsetConverter());");
    sb.AppendLine("  if (type == typeof(TimeSpan)) return JsonMetadataServices.CreateValueInfo<TimeSpan>(options, JsonMetadataServices.TimeSpanConverter);");
    sb.AppendLine("  if (type == typeof(DateOnly)) return JsonMetadataServices.CreateValueInfo<DateOnly>(options, JsonMetadataServices.DateOnlyConverter);");
    sb.AppendLine("  if (type == typeof(TimeOnly)) return JsonMetadataServices.CreateValueInfo<TimeOnly>(options, JsonMetadataServices.TimeOnlyConverter);");
    sb.AppendLine("  if (type == typeof(decimal)) return JsonMetadataServices.CreateValueInfo<decimal>(options, JsonMetadataServices.DecimalConverter);");
    sb.AppendLine("  if (type == typeof(double)) return JsonMetadataServices.CreateValueInfo<double>(options, JsonMetadataServices.DoubleConverter);");
    sb.AppendLine("  if (type == typeof(float)) return JsonMetadataServices.CreateValueInfo<float>(options, JsonMetadataServices.SingleConverter);");
    sb.AppendLine("  if (type == typeof(byte)) return JsonMetadataServices.CreateValueInfo<byte>(options, JsonMetadataServices.ByteConverter);");
    sb.AppendLine("  if (type == typeof(sbyte)) return JsonMetadataServices.CreateValueInfo<sbyte>(options, JsonMetadataServices.SByteConverter);");
    sb.AppendLine("  if (type == typeof(short)) return JsonMetadataServices.CreateValueInfo<short>(options, JsonMetadataServices.Int16Converter);");
    sb.AppendLine("  if (type == typeof(ushort)) return JsonMetadataServices.CreateValueInfo<ushort>(options, JsonMetadataServices.UInt16Converter);");
    sb.AppendLine("  if (type == typeof(uint)) return JsonMetadataServices.CreateValueInfo<uint>(options, JsonMetadataServices.UInt32Converter);");
    sb.AppendLine("  if (type == typeof(ulong)) return JsonMetadataServices.CreateValueInfo<ulong>(options, JsonMetadataServices.UInt64Converter);");
    sb.AppendLine("  if (type == typeof(char)) return JsonMetadataServices.CreateValueInfo<char>(options, JsonMetadataServices.CharConverter);");
    sb.AppendLine();
    sb.AppendLine("  // Nullable primitive types - create underlying type info first, then wrap with nullable converter");
    sb.AppendLine("  if (type == typeof(int?)) { var u = JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter); return JsonMetadataServices.CreateValueInfo<int?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(long?)) { var u = JsonMetadataServices.CreateValueInfo<long>(options, JsonMetadataServices.Int64Converter); return JsonMetadataServices.CreateValueInfo<long?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(bool?)) { var u = JsonMetadataServices.CreateValueInfo<bool>(options, JsonMetadataServices.BooleanConverter); return JsonMetadataServices.CreateValueInfo<bool?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(Guid?)) { var u = JsonMetadataServices.CreateValueInfo<Guid>(options, JsonMetadataServices.GuidConverter); return JsonMetadataServices.CreateValueInfo<Guid?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(DateTime?)) { var u = JsonMetadataServices.CreateValueInfo<DateTime>(options, JsonMetadataServices.DateTimeConverter); return JsonMetadataServices.CreateValueInfo<DateTime?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(DateTimeOffset?)) { var u = JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, JsonMetadataServices.DateTimeOffsetConverter); return JsonMetadataServices.CreateValueInfo<DateTimeOffset?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(TimeSpan?)) { var u = JsonMetadataServices.CreateValueInfo<TimeSpan>(options, JsonMetadataServices.TimeSpanConverter); return JsonMetadataServices.CreateValueInfo<TimeSpan?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(DateOnly?)) { var u = JsonMetadataServices.CreateValueInfo<DateOnly>(options, JsonMetadataServices.DateOnlyConverter); return JsonMetadataServices.CreateValueInfo<DateOnly?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(TimeOnly?)) { var u = JsonMetadataServices.CreateValueInfo<TimeOnly>(options, JsonMetadataServices.TimeOnlyConverter); return JsonMetadataServices.CreateValueInfo<TimeOnly?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(decimal?)) { var u = JsonMetadataServices.CreateValueInfo<decimal>(options, JsonMetadataServices.DecimalConverter); return JsonMetadataServices.CreateValueInfo<decimal?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(double?)) { var u = JsonMetadataServices.CreateValueInfo<double>(options, JsonMetadataServices.DoubleConverter); return JsonMetadataServices.CreateValueInfo<double?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(float?)) { var u = JsonMetadataServices.CreateValueInfo<float>(options, JsonMetadataServices.SingleConverter); return JsonMetadataServices.CreateValueInfo<float?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(byte?)) { var u = JsonMetadataServices.CreateValueInfo<byte>(options, JsonMetadataServices.ByteConverter); return JsonMetadataServices.CreateValueInfo<byte?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(sbyte?)) { var u = JsonMetadataServices.CreateValueInfo<sbyte>(options, JsonMetadataServices.SByteConverter); return JsonMetadataServices.CreateValueInfo<sbyte?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(short?)) { var u = JsonMetadataServices.CreateValueInfo<short>(options, JsonMetadataServices.Int16Converter); return JsonMetadataServices.CreateValueInfo<short?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(ushort?)) { var u = JsonMetadataServices.CreateValueInfo<ushort>(options, JsonMetadataServices.UInt16Converter); return JsonMetadataServices.CreateValueInfo<ushort?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(uint?)) { var u = JsonMetadataServices.CreateValueInfo<uint>(options, JsonMetadataServices.UInt32Converter); return JsonMetadataServices.CreateValueInfo<uint?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(ulong?)) { var u = JsonMetadataServices.CreateValueInfo<ulong>(options, JsonMetadataServices.UInt64Converter); return JsonMetadataServices.CreateValueInfo<ulong?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine("  if (type == typeof(char?)) { var u = JsonMetadataServices.CreateValueInfo<char>(options, JsonMetadataServices.CharConverter); return JsonMetadataServices.CreateValueInfo<char?>(options, JsonMetadataServices.GetNullableConverter(u)); }");
    sb.AppendLine();

    // List<primitive> types - needed for nested collections like List<List<string>>
    // When List<List<string>> is created, it needs JsonTypeInfo for List<string> as element type
    sb.AppendLine("  // List<primitive> types - enables nested collections like List<List<string>>");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<string>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<string>(options, JsonMetadataServices.StringConverter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<string>, string>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<string>> { ObjectCreator = static () => new global::System.Collections.Generic.List<string>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<int>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<int>(options, JsonMetadataServices.Int32Converter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<int>, int>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<int>> { ObjectCreator = static () => new global::System.Collections.Generic.List<int>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<long>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<long>(options, JsonMetadataServices.Int64Converter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<long>, long>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<long>> { ObjectCreator = static () => new global::System.Collections.Generic.List<long>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<bool>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<bool>(options, JsonMetadataServices.BooleanConverter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<bool>, bool>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<bool>> { ObjectCreator = static () => new global::System.Collections.Generic.List<bool>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<Guid>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<Guid>(options, JsonMetadataServices.GuidConverter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<Guid>, Guid>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<Guid>> { ObjectCreator = static () => new global::System.Collections.Generic.List<Guid>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<DateTime>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<DateTime>(options, JsonMetadataServices.DateTimeConverter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<DateTime>, DateTime>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<DateTime>> { ObjectCreator = static () => new global::System.Collections.Generic.List<DateTime>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<DateTimeOffset>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<DateTimeOffset>(options, new global::Whizbang.Core.Serialization.LenientDateTimeOffsetConverter());");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<DateTimeOffset>, DateTimeOffset>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<DateTimeOffset>> { ObjectCreator = static () => new global::System.Collections.Generic.List<DateTimeOffset>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<decimal>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<decimal>(options, JsonMetadataServices.DecimalConverter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<decimal>, decimal>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<decimal>> { ObjectCreator = static () => new global::System.Collections.Generic.List<decimal>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<double>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<double>(options, JsonMetadataServices.DoubleConverter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<double>, double>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<double>> { ObjectCreator = static () => new global::System.Collections.Generic.List<double>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<float>)) {");
    sb.AppendLine("    var elementInfo = JsonMetadataServices.CreateValueInfo<float>(options, JsonMetadataServices.SingleConverter);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<float>, float>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<float>> { ObjectCreator = static () => new global::System.Collections.Generic.List<float>(), ElementInfo = elementInfo });");
    sb.AppendLine("  }");
    sb.AppendLine();

    // List<List<primitive>> types - needed for deeply nested collections like List<List<string>>
    // When List<List<string>> is serialized, STJ needs explicit JsonTypeInfo for the outer list type in AOT mode
    sb.AppendLine("  // List<List<primitive>> types - enables deeply nested collections");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<string>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<string>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<string>>, global::System.Collections.Generic.List<string>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<string>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<string>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<int>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>>, global::System.Collections.Generic.List<int>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<int>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<long>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<long>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<long>>, global::System.Collections.Generic.List<long>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<long>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<long>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<bool>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<bool>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<bool>>, global::System.Collections.Generic.List<bool>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<bool>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<bool>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<Guid>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<Guid>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<Guid>>, global::System.Collections.Generic.List<Guid>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<Guid>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<Guid>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<decimal>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<decimal>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<decimal>>, global::System.Collections.Generic.List<decimal>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<decimal>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<decimal>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<double>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<double>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<double>>, global::System.Collections.Generic.List<double>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<double>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<double>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine("  if (type == typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<float>>)) {");
    sb.AppendLine("    var innerListInfo = GetOrCreateTypeInfo<global::System.Collections.Generic.List<float>>(options);");
    sb.AppendLine("    return JsonMetadataServices.CreateListInfo<global::System.Collections.Generic.List<global::System.Collections.Generic.List<float>>, global::System.Collections.Generic.List<float>>(options, new JsonCollectionInfoValues<global::System.Collections.Generic.List<global::System.Collections.Generic.List<float>>> { ObjectCreator = static () => new global::System.Collections.Generic.List<global::System.Collections.Generic.List<float>>(), ElementInfo = innerListInfo });");
    sb.AppendLine("  }");
    sb.AppendLine();

    // All discovered types (messages + nested types)
    sb.AppendLine("  // Discovered types (messages + nested types)");
    foreach (var type in allTypes) {
      var check = messageCheckSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, type.FullyQualifiedName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, type.UniqueIdentifier);
      sb.AppendLine(check);
      sb.AppendLine();
    }

    // MessageEnvelope<T> ONLY for actual message types (commands/events), not nested types
    sb.AppendLine("  // MessageEnvelope<T> for discovered message types");
    foreach (var type in allTypes.Where(t => t.IsCommand || t.IsEvent)) {
      var check = envelopeCheckSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, type.FullyQualifiedName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, type.UniqueIdentifier);
      sb.AppendLine(check);
      sb.AppendLine();
    }

    // List<T> types discovered in messages
    if (!listTypes.IsEmpty) {
      sb.AppendLine("  // List<T> types discovered in messages");
      foreach (var listType in listTypes) {
        var check = listCheckSnippet
            .Replace("__ELEMENT_TYPE__", listType.ElementTypeName)
            .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", listType.ElementUniqueIdentifier);
        sb.AppendLine(check);
        sb.AppendLine();
      }
    }

    // IReadOnlyList<T> types discovered in messages
    if (!iReadOnlyListTypes.IsEmpty) {
      sb.AppendLine("  // IReadOnlyList<T> types discovered in messages");
      foreach (var iReadOnlyListType in iReadOnlyListTypes) {
        var check = iReadOnlyListCheckSnippet
            .Replace("__ELEMENT_TYPE__", iReadOnlyListType.ElementTypeName)
            .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", iReadOnlyListType.ElementUniqueIdentifier);
        sb.AppendLine(check);
        sb.AppendLine();
      }
    }

    // Array types (T[]) discovered in messages
    if (!arrayTypes.IsEmpty) {
      sb.AppendLine("  // Array types (T[]) discovered in messages");
      foreach (var arrayType in arrayTypes) {
        var check = arrayCheckSnippet
            .Replace("__ELEMENT_TYPE__", arrayType.ElementTypeName)
            .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", arrayType.ElementUniqueIdentifier);
        sb.AppendLine(check);
        sb.AppendLine();
      }
    }

    // Dictionary<TKey, TValue> types discovered in messages
    if (!dictionaryTypes.IsEmpty) {
      sb.AppendLine("  // Dictionary<TKey, TValue> types discovered in messages");
      foreach (var dictType in dictionaryTypes) {
        var check = dictionaryCheckSnippet
            .Replace("__KEY_TYPE__", dictType.KeyTypeName)
            .Replace("__VALUE_TYPE__", dictType.ValueTypeName)
            .Replace("__UNIQUE_IDENTIFIER__", dictType.UniqueIdentifier);
        sb.AppendLine(check);
        sb.AppendLine();
      }
    }

    // Enum types discovered in message and nested type properties (both non-nullable and nullable)
    if (!enumTypes.IsEmpty) {
      sb.AppendLine("  // Enum types discovered in messages and nested types");
      foreach (var enumType in enumTypes) {
        // Non-nullable enum
        var check = enumCheckSnippet
            .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, enumType.FullyQualifiedName)
            .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, enumType.UniqueIdentifier);
        sb.AppendLine(check);
        sb.AppendLine();

        // Nullable enum (always generate both - no need to discover which are used as nullable)
        var nullableCheck = nullableEnumCheckSnippet
            .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, enumType.FullyQualifiedName)
            .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, enumType.UniqueIdentifier);
        sb.AppendLine(nullableCheck);
        sb.AppendLine();
      }
    }

    // Polymorphic base types for automatic JSON serialization
    if (!polymorphicTypes.IsEmpty) {
      var polymorphicCheckSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          TEMPLATE_SNIPPET_FILE,
          "GET_TYPE_INFO_POLYMORPHIC");

      sb.AppendLine("  // Polymorphic base types");
      foreach (var polyType in polymorphicTypes) {
        var check = polymorphicCheckSnippet
            .Replace("__BASE_TYPE__", polyType.BaseTypeName)
            .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, polyType.UniqueIdentifier);
        sb.AppendLine(check);
        sb.AppendLine();
      }
    }

    // Interface types for polymorphic serialization (IEvent, ICommand, IMessage)
    // These delegate to JsonContextRegistry which aggregates derived types from all assemblies
    var hasEvents = allTypes.Any(t => t.IsEvent);
    var hasCommands = allTypes.Any(t => t.IsCommand);

    if (hasEvents || hasCommands) {
      var interfaceCheckSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          TEMPLATE_SNIPPET_FILE,
          "GET_TYPE_INFO_INTERFACE");
      var messageEnvelopeInterfaceCheckSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          TEMPLATE_SNIPPET_FILE,
          "GET_TYPE_INFO_MESSAGE_ENVELOPE_INTERFACE");
      var listInterfaceCheckSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          TEMPLATE_SNIPPET_FILE,
          "GET_TYPE_INFO_LIST_INTERFACE");

      sb.AppendLine("  // Interface types for polymorphic serialization");
      sb.AppendLine("  // These delegate to JsonContextRegistry which aggregates derived types from all assemblies");

      // IMessage (always if we have any messages)
      sb.AppendLine(interfaceCheckSnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
          .Replace("__INTERFACE_NAME__", "IMessage"));
      sb.AppendLine(messageEnvelopeInterfaceCheckSnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
          .Replace("__INTERFACE_NAME__", "IMessage"));
      sb.AppendLine(listInterfaceCheckSnippet
          .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IMessage")
          .Replace("__INTERFACE_NAME__", "IMessage"));
      sb.AppendLine();

      // IEvent (only if we have events)
      if (hasEvents) {
        sb.AppendLine(interfaceCheckSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
            .Replace("__INTERFACE_NAME__", "IEvent"));
        sb.AppendLine(messageEnvelopeInterfaceCheckSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
            .Replace("__INTERFACE_NAME__", "IEvent"));
        sb.AppendLine(listInterfaceCheckSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.IEvent")
            .Replace("__INTERFACE_NAME__", "IEvent"));
        sb.AppendLine();
      }

      // ICommand (only if we have commands)
      if (hasCommands) {
        sb.AppendLine(interfaceCheckSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
            .Replace("__INTERFACE_NAME__", "ICommand"));
        sb.AppendLine(messageEnvelopeInterfaceCheckSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
            .Replace("__INTERFACE_NAME__", "ICommand"));
        sb.AppendLine(listInterfaceCheckSnippet
            .Replace("__INTERFACE_TYPE__", "global::Whizbang.Core.ICommand")
            .Replace("__INTERFACE_NAME__", "ICommand"));
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

    // Thread-local field for circular reference detection in GetOrCreateTypeInfo
    var typesBeingCreatedSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "TYPES_BEING_CREATED_FIELD");

    var getOrCreateTypeInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "HELPER_GET_OR_CREATE_TYPE_INFO");

    // TryGetOrCreateTypeInfo for graceful circular reference handling in collections
    var tryGetOrCreateTypeInfoSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "HELPER_TRY_GET_OR_CREATE_TYPE_INFO");

    sb.AppendLine(createPropertySnippet);
    sb.AppendLine();
    sb.AppendLine(typesBeingCreatedSnippet);
    sb.AppendLine();
    sb.AppendLine(getOrCreateTypeInfoSnippet);
    sb.AppendLine();
    sb.AppendLine(tryGetOrCreateTypeInfoSnippet);

    return sb.ToString();
  }

  /// <summary>
  /// Generates AOT-safe GetTypeInfoByName method that maps assembly-qualified type names to JsonTypeInfo
  /// using compile-time typeof() calls instead of runtime Type.GetType().
  /// This avoids IL2057 trimming warnings by using static type references.
  /// NOTE: Deprecated in favor of JsonContextRegistry.GetTypeInfoByName for cross-assembly support.
  /// </summary>
  private static string _generateGetTypeInfoByName(ImmutableArray<JsonMessageTypeInfo> allTypes, Compilation compilation) {
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
    sb.AppendLine("[global::System.Obsolete(\"Use JsonContextRegistry.GetTypeInfoByName() for cross-assembly type resolution with fuzzy matching support.\")]");
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
    var messageTypes = allTypes.Where(t => t.IsCommand || t.IsEvent);
    var typeMappings = messageTypes.Select(type => {
      // Use CLR type name format (uses + for nested types) for runtime type resolution
      // ClrTypeName is like "MyApp.Commands.CreateOrder" or "MyApp.AuthContracts+LoginCommand"
      return $"    \"{type.ClrTypeName}, {actualAssemblyName}\" => context.GetTypeInfoInternal(typeof({type.FullyQualifiedName}), options),";
    });
    sb.AppendLine(string.Join("\n", typeMappings));

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
      // Generate the main factory method with deferred property initialization
      // This enables support for self-referencing types (e.g., Event with List<Event> property)
      sb.AppendLine($"private JsonTypeInfo<{message.FullyQualifiedName}> Create_{message.UniqueIdentifier}(JsonSerializerOptions options) {{");

      // Filter to only writable properties for constructor params and object initializer
      // Computed properties (CanWrite = false) cannot be assigned and are excluded
      var writableProperties = message.Properties.Where(p => p.CanWrite).ToArray();

      // Generate different code based on constructor type
      if (message.HasParameterizedConstructor) {
        // Type has parameterized constructor (e.g., record with primary constructor)
        // Create JsonObjectInfoValues with DEFERRED property initialization
        sb.AppendLine($"  var objectInfo = new JsonObjectInfoValues<{message.FullyQualifiedName}> {{");
        sb.AppendLine($"      ObjectWithParameterizedConstructorCreator = static args => new {message.FullyQualifiedName}(");
        for (int i = 0; i < writableProperties.Length; i++) {
          var prop = writableProperties[i];
          var comma = i < writableProperties.Length - 1 ? "," : "";
          sb.AppendLine($"          ({prop.Type})args[{i}]{comma}");
        }
        sb.AppendLine("      ),");
        sb.AppendLine($"      PropertyMetadataInitializer = _ => CreatePropertiesFor_{message.UniqueIdentifier}(options),");
        sb.AppendLine($"      ConstructorParameterMetadataInitializer = () => CreateCtorParamsFor_{message.UniqueIdentifier}()");
        sb.AppendLine($"  }};");
      } else {
        // Type has no parameterized constructor but has init-only properties
        // Create JsonObjectInfoValues with DEFERRED property initialization
        sb.AppendLine($"  var objectInfo = new JsonObjectInfoValues<{message.FullyQualifiedName}> {{");
        sb.AppendLine($"      ObjectWithParameterizedConstructorCreator = static args => new {message.FullyQualifiedName}() {{");
        for (int i = 0; i < writableProperties.Length; i++) {
          var prop = writableProperties[i];
          var comma = i < writableProperties.Length - 1 ? "," : "";
          sb.AppendLine($"          {prop.Name} = ({prop.Type})args[{i}]{comma}");
        }
        sb.AppendLine("      },");
        sb.AppendLine($"      PropertyMetadataInitializer = _ => CreatePropertiesFor_{message.UniqueIdentifier}(options),");
        sb.AppendLine($"      ConstructorParameterMetadataInitializer = () => CreateCtorParamsFor_{message.UniqueIdentifier}()");
        sb.AppendLine($"  }};");
      }
      sb.AppendLine();

      // Create JsonTypeInfo and CACHE IT IMMEDIATELY before returning
      // This is critical for self-referencing types - the cache must be populated
      // before the deferred PropertyMetadataInitializer runs
      sb.AppendLine($"  var jsonTypeInfo = JsonMetadataServices.CreateObjectInfo(options, objectInfo);");
      sb.AppendLine($"  TypeInfoCache[typeof({message.FullyQualifiedName})] = jsonTypeInfo;");
      sb.AppendLine($"  jsonTypeInfo.OriginatingResolver = this;");
      sb.AppendLine($"  return jsonTypeInfo;");
      sb.AppendLine($"}}");
      sb.AppendLine();

      // Generate the deferred property creation method
      sb.AppendLine($"private JsonPropertyInfo[] CreatePropertiesFor_{message.UniqueIdentifier}(JsonSerializerOptions options) {{");
      sb.AppendLine($"  var properties = new JsonPropertyInfo[{message.Properties.Length}];");
      sb.AppendLine();

      for (int i = 0; i < message.Properties.Length; i++) {
        var prop = message.Properties[i];
        var setter = !prop.CanWrite || prop.IsInitOnly
            ? "null"
            : $"(obj, value) => (({message.FullyQualifiedName})obj).{prop.Name} = value!";

        var propertyCode = propertyCreationSnippet
            .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture))
            .Replace(PLACEHOLDER_PROPERTY_TYPE, prop.Type)
            .Replace(PLACEHOLDER_PROPERTY_NAME, prop.Name)
            .Replace(PLACEHOLDER_MESSAGE_TYPE, message.FullyQualifiedName)
            .Replace(PLACEHOLDER_SETTER, setter);

        sb.AppendLine(propertyCode);
        sb.AppendLine();
      }
      sb.AppendLine($"  return properties;");
      sb.AppendLine($"}}");
      sb.AppendLine();

      // Generate the deferred constructor params creation method
      sb.AppendLine($"private JsonParameterInfoValues[] CreateCtorParamsFor_{message.UniqueIdentifier}() {{");
      sb.AppendLine($"  var ctorParams = new JsonParameterInfoValues[{writableProperties.Length}];");
      for (int i = 0; i < writableProperties.Length; i++) {
        var prop = writableProperties[i];
        var parameterCode = parameterInfoSnippet
            .Replace(PLACEHOLDER_INDEX, i.ToString(CultureInfo.InvariantCulture))
            .Replace(PLACEHOLDER_PARAMETER_NAME, prop.Name)
            .Replace(PLACEHOLDER_PROPERTY_TYPE, _getTypeOfExpression(prop));

        sb.AppendLine(parameterCode);
      }
      sb.AppendLine($"  return ctorParams;");
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
      sb.AppendLine($"private JsonTypeInfo<MessageEnvelope<{message.FullyQualifiedName}>> CreateMessageEnvelope_{message.UniqueIdentifier}(JsonSerializerOptions options) {{");

      // Generate properties array for MessageEnvelope<T> (MessageId, Payload, Hops)
      sb.AppendLine("  var properties = new JsonPropertyInfo[3];");
      sb.AppendLine();

      // Property 0: MessageId using snippet
      var messageIdProperty = propertyCreationSnippet
          .Replace(PLACEHOLDER_INDEX, "0")
          .Replace(PLACEHOLDER_PROPERTY_TYPE, PLACEHOLDER_MESSAGE_ID)
          .Replace(PLACEHOLDER_PROPERTY_NAME, PLACEHOLDER_MESSAGE_ID)
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
          .Replace(PLACEHOLDER_PROPERTY_TYPE, PLACEHOLDER_MESSAGE_ID);
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
      sb.AppendLine($"          ({PLACEHOLDER_MESSAGE_ID})args[0],");
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
  /// Uses queue-based recursion to discover deeply nested types (e.g., Event → Stage → Step → Action).
  /// Also discovers types used as direct properties (non-collection), not just collection element types.
  /// These types need JsonTypeInfo generated for AOT serialization to work properly.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithDeeplyNestedTypes_DiscoversAllLevelsAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithCircularReferences_HandlesWithoutInfiniteLoopAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithSelfReferencingType_HandlesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithDirectPropertyNestedType_DiscoversNestedTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithDeepDirectPropertyNesting_DiscoversAllTypesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithMixedCollectionAndDirectNestedTypes_DiscoversAllTypesAsync</tests>
  private static (ImmutableArray<JsonMessageTypeInfo> NestedTypes, ImmutableArray<PolymorphicTypeInfo> PolymorphicTypes) _discoverNestedTypes(
      ImmutableArray<JsonMessageTypeInfo> messages,
      Compilation compilation) {

    var nestedTypes = new Dictionary<string, JsonMessageTypeInfo>();
    var discoveredPolymorphicTypes = new Dictionary<string, PolymorphicTypeInfo>();

    // Use a queue to process types recursively - starts with all message types
    var typesToProcess = new Queue<JsonMessageTypeInfo>(messages);

    // Track all processed types to prevent infinite loops (circular references, self-references)
    var processedTypes = new HashSet<string>(messages.Select(m => m.FullyQualifiedName));

    while (typesToProcess.Count > 0) {
      var currentType = typesToProcess.Dequeue();

      foreach (var property in currentType.Properties) {
        // Try to extract type from collections first, then check for direct property types
        var elementTypeName = _extractElementType(property.Type);
        var typeNameToProcess = elementTypeName ?? _extractDirectPropertyType(property.Type);

        if (typeNameToProcess == null) {
          continue;
        }

        // Skip if already processed (handles circular and self-references)
        if (processedTypes.Contains(typeNameToProcess)) {
          continue;
        }

        // Skip primitive and framework types
        if (_isPrimitiveOrFrameworkType(typeNameToProcess)) {
          continue;
        }

        // Skip System.* types (collections, framework types) - STJ handles these natively
        // This handles cases like List<List<T>> where element type is List<T>
        if (typeNameToProcess.StartsWith("global::System.", StringComparison.Ordinal)) {
          continue;
        }

        // Try to get public type symbol
        var typeSymbol = _tryGetPublicTypeSymbol(typeNameToProcess, compilation);
        if (typeSymbol == null) {
          continue;
        }

        // Skip enums - they're handled by _discoverEnumTypes
        if (typeSymbol.TypeKind == TypeKind.Enum) {
          continue;
        }

        // Handle abstract types with [JsonPolymorphic] - discover their derived types
        // For polymorphic types, we generate JsonTypeInfo for both:
        // 1. The abstract base type (with polymorphic options for derived type dispatch)
        // 2. All concrete derived types (for actual serialization)
        if (typeSymbol.IsAbstract) {
          // Check if this abstract type has [JsonPolymorphic] attribute
          if (_hasJsonPolymorphicAttribute(typeSymbol)) {
            // Discover derived types from [JsonDerivedType] attributes
            var derivedTypes = _discoverDerivedTypesFromAttributes(typeSymbol, compilation);
            var derivedTypeNames = new List<string>();

            foreach (var derivedType in derivedTypes) {
              var derivedTypeName = derivedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
              derivedTypeNames.Add(derivedTypeName);

              // Skip if already processed
              if (processedTypes.Contains(derivedTypeName)) {
                continue;
              }

              // Extract properties and create type info for derived type
              var derivedProperties = _extractPropertiesFromType(derivedType);
              var hasDerivedCtor = _hasMatchingParameterizedConstructor(derivedType, derivedProperties);
              var derivedClrTypeName = _getClrTypeName(derivedType);

              var derivedTypeInfo = new JsonMessageTypeInfo(
                  FullyQualifiedName: derivedTypeName,
                  ClrTypeName: derivedClrTypeName,
                  SimpleName: derivedType.Name,
                  IsCommand: false,
                  IsEvent: false,
                  IsSerializable: false,
                  Properties: derivedProperties,
                  HasParameterizedConstructor: hasDerivedCtor
              );

              nestedTypes[derivedTypeName] = derivedTypeInfo;
              processedTypes.Add(derivedTypeName);
              typesToProcess.Enqueue(derivedTypeInfo);
            }

            // Create polymorphic type info for the abstract base type
            // This allows STJ to dispatch to the correct derived type during deserialization
            if (derivedTypeNames.Count > 0 && !discoveredPolymorphicTypes.ContainsKey(typeNameToProcess)) {
              var simpleName = typeSymbol.Name;
              var isInterface = typeSymbol.TypeKind == TypeKind.Interface;
              discoveredPolymorphicTypes[typeNameToProcess] = new PolymorphicTypeInfo(
                  BaseTypeName: typeNameToProcess,
                  BaseSimpleName: simpleName,
                  DerivedTypes: derivedTypeNames.ToImmutableArray(),
                  IsInterface: isInterface
              );
            }
          }
          // Mark abstract type as processed to avoid re-checking
          processedTypes.Add(typeNameToProcess);
          continue;
        }

        // Skip [WhizbangId] types - they have their own converters generated by WhizbangIdGenerator
        // If we generate JsonTypeInfo here, it will incorrectly create an empty object metadata
        // that overrides the proper converter-based handling from WhizbangIdJsonContext
        // Note: We check for the attribute, not IWhizbangId interface, because generators run in parallel
        // and MessageJsonContextGenerator may not see the interface that WhizbangIdGenerator adds
        if (_hasWhizbangIdAttribute(typeSymbol)) {
          continue;
        }

        // Note: Structs (including record struct) are now supported.
        // The IsInitOnly fix (SetMethod == null || IsInitOnly) properly handles
        // get-only properties, so structs work correctly with constructor initialization.

        // Extract properties and detect constructor
        var nestedProperties = _extractPropertiesFromType(typeSymbol);
        bool hasParameterizedConstructor = _hasMatchingParameterizedConstructor(typeSymbol, nestedProperties);

        // Build CLR type name for nested types (uses + separator for nested types)
        var clrTypeName = _getClrTypeName(typeSymbol);

        // Build nested type info
        var nestedTypeInfo = new JsonMessageTypeInfo(
            FullyQualifiedName: typeNameToProcess,
            ClrTypeName: clrTypeName,
            SimpleName: typeSymbol.Name,
            IsCommand: false,  // Nested types are not commands/events
            IsEvent: false,
            IsSerializable: false,  // Nested types discovered through property analysis, not attribute
            Properties: nestedProperties,
            HasParameterizedConstructor: hasParameterizedConstructor
        );

        nestedTypes[typeNameToProcess] = nestedTypeInfo;
        processedTypes.Add(typeNameToProcess);

        // Queue for recursive processing - discovers deeply nested types
        typesToProcess.Enqueue(nestedTypeInfo);
      }
    }

    return (nestedTypes.Values.ToImmutableArray(), discoveredPolymorphicTypes.Values.ToImmutableArray());
  }

  /// <summary>
  /// Discovers enum types used in message properties and nested type properties.
  /// Enums need JsonTypeInfo generated for AOT serialization to work properly.
  /// Recursively discovers enums in all types (messages + nested types).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithEnumProperty_DiscoversEnumAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NestedTypeWithEnumProperty_DiscoversEnumAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DeeplyNestedEnumProperty_DiscoversEnumAsync</tests>
  private static ImmutableArray<JsonEnumInfo> _discoverEnumTypes(
      ImmutableArray<JsonMessageTypeInfo> allTypes,
      Compilation compilation) {

    var discoveredEnums = new Dictionary<string, JsonEnumInfo>();

    foreach (var type in allTypes) {
      foreach (var property in type.Properties) {
        // Get the property type (handle nullable and collection wrappers)
        var propertyTypeName = property.Type;

        // Strip nullable suffix if present
        if (propertyTypeName.EndsWith("?", StringComparison.Ordinal)) {
          propertyTypeName = propertyTypeName[..^1];
        }

        // Check if it's already discovered
        if (discoveredEnums.ContainsKey(propertyTypeName)) {
          continue;
        }

        // Skip collection types (their element types are handled separately)
        if (_extractElementType(property.Type) != null) {
          continue;
        }

        // Skip primitive and framework types
        if (_isPrimitiveOrFrameworkType(propertyTypeName)) {
          continue;
        }

        // Skip framework enums (DayOfWeek, etc.) - they're handled by STJ
        if (_isFrameworkEnum(propertyTypeName)) {
          continue;
        }

        // Try to get the type symbol
        var typeSymbol = _tryGetPublicTypeSymbol(propertyTypeName, compilation);
        if (typeSymbol == null) {
          continue;
        }

        // Check if it's an enum
        if (typeSymbol.TypeKind != TypeKind.Enum) {
          continue;
        }

        // Add to discovered enums
        discoveredEnums[propertyTypeName] = new JsonEnumInfo(
            FullyQualifiedName: propertyTypeName,
            SimpleName: typeSymbol.Name
        );
      }
    }

    return discoveredEnums.Values.ToImmutableArray();
  }

  /// <summary>
  /// Checks if a type is a framework enum that System.Text.Json handles natively.
  /// </summary>
  private static bool _isFrameworkEnum(string fullyQualifiedTypeName) {
    var frameworkEnums = new[] {
      "global::System.DayOfWeek",
      "global::System.DateTimeKind",
      "global::System.StringComparison",
      "global::System.EnvironmentVariableTarget",
      "global::System.IO.FileMode",
      "global::System.IO.FileAccess",
      "global::System.IO.FileShare"
    };

    return frameworkEnums.Contains(fullyQualifiedTypeName);
  }

  /// <summary>
  /// Extracts the element type from a generic collection type.
  /// For example: "global::System.Collections.Generic.List&lt;global::MyApp.OrderLineItem&gt;" returns "global::MyApp.OrderLineItem"
  /// For Dictionary types, extracts the VALUE type (second type parameter).
  /// For example: "global::System.Collections.Generic.Dictionary&lt;string, global::MyApp.SeedSectionContext&gt;" returns "global::MyApp.SeedSectionContext"
  /// For nested collections (e.g., Dictionary&lt;string, List&lt;T&gt;&gt;), recursively extracts until reaching a non-collection type.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithDictionaryProperty_DiscoversValueTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithNestedDictionaryValue_DiscoversDeepTypesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithIDictionaryProperty_DiscoversValueTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DictionaryWithNestedGenericValue_DiscoversInnerTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NestedDictionaryValue_DiscoversDeepestTypeAsync</tests>
  private static string? _extractElementType(string fullyQualifiedTypeName) {
    var elementType = _extractElementTypeSingleLevel(fullyQualifiedTypeName);

    // If the extracted element type is itself a collection, recursively extract
    // This handles cases like Dictionary<string, List<T>> -> T
    // or Dictionary<string, Dictionary<int, T>> -> T
    while (elementType != null) {
      var nestedElementType = _extractElementTypeSingleLevel(elementType);
      if (nestedElementType == null) {
        // No more nesting, return the current element type
        break;
      }
      elementType = nestedElementType;
    }

    return elementType;
  }

  /// <summary>
  /// Extracts the element type from a generic collection type (single level, no recursion).
  /// For List/IEnumerable types, returns the type argument.
  /// For Dictionary types, returns the VALUE type (second type parameter).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_TripleNestedCollections_DiscoversDeepestTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DictionaryWithArrayValue_DiscoversArrayElementTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_TripleNestedList_DiscoversDeepestTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DictionaryWithIEnumerableValue_DiscoversElementTypeAsync</tests>
  private static string? _extractElementTypeSingleLevel(string fullyQualifiedTypeName) {
    // Strip nullable suffix for analysis (e.g., "T[]?" -> "T[]")
    var typeName = fullyQualifiedTypeName;
    if (typeName.EndsWith("?", StringComparison.Ordinal)) {
      typeName = typeName[..^1];
    }

    // Check for array types (T[]) - extract element type T
    if (typeName.EndsWith("[]", StringComparison.Ordinal)) {
      return typeName[..^2]; // Remove "[]" suffix to get element type
    }

    // Check for common generic collection types (single type parameter)
    var singleTypeParamCollections = new[] {
      "global::System.Collections.Generic.List<",
      "global::System.Collections.Generic.IList<",
      "global::System.Collections.Generic.IReadOnlyList<",
      "global::System.Collections.Generic.ICollection<",
      "global::System.Collections.Generic.IReadOnlyCollection<",
      "global::System.Collections.Generic.IEnumerable<"
    };

    var matchingPrefix = singleTypeParamCollections.FirstOrDefault(prefix =>
        typeName.StartsWith(prefix, StringComparison.Ordinal));

    if (matchingPrefix != null) {
      // Extract the type argument between < and >
      var startIndex = matchingPrefix.Length;
      var endIndex = typeName.LastIndexOf('>');
      if (endIndex > startIndex) {
        return typeName[startIndex..endIndex];
      }
    }

    // Check for dictionary types (extract VALUE type - second type parameter)
    var dictionaryTypes = new[] {
      "global::System.Collections.Generic.Dictionary<",
      "global::System.Collections.Generic.IDictionary<",
      "global::System.Collections.Generic.IReadOnlyDictionary<"
    };

    var matchingDictPrefix = dictionaryTypes.FirstOrDefault(prefix =>
        typeName.StartsWith(prefix, StringComparison.Ordinal));

    if (matchingDictPrefix != null) {
      // Extract the VALUE type (second type argument)
      // Format: Dictionary<TKey, TValue>
      var startIndex = matchingDictPrefix.Length;
      var endIndex = typeName.LastIndexOf('>');
      if (endIndex > startIndex) {
        var typeArgs = typeName[startIndex..endIndex];
        // Find the comma separating TKey and TValue (accounting for nested generics)
        var commaIndex = _findTopLevelComma(typeArgs);
        if (commaIndex > 0) {
          // Return TValue (everything after comma, trimmed)
          return typeArgs[(commaIndex + 1)..].Trim();
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Finds the index of the first top-level comma in a type arguments string.
  /// This correctly handles nested generics like "string, Dictionary&lt;int, string&gt;".
  /// </summary>
  /// <param name="typeArgs">The type arguments string (e.g., "string, MyType" or "int, List&lt;string&gt;")</param>
  /// <returns>The index of the top-level comma, or -1 if not found</returns>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DictionaryWithNonStringKey_DiscoversValueTypeOnlyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DictionaryWithNestedGenericValue_DiscoversInnerTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NestedDictionaryValue_DiscoversDeepestTypeAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_TripleNestedCollections_DiscoversDeepestTypeAsync</tests>
  private static int _findTopLevelComma(string typeArgs) {
    int depth = 0;
    for (int i = 0; i < typeArgs.Length; i++) {
      char c = typeArgs[i];
      if (c == '<') {
        depth++;
      } else if (c == '>') {
        depth--;
      } else if (c == ',' && depth == 0) {
        return i;
      }
    }
    return -1;
  }

  /// <summary>
  /// Checks if a type is a collection type that would be handled by _extractElementType.
  /// Includes Dictionary types whose value types are extracted.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DictionaryAsDirectProperty_TreatedAsCollectionAsync</tests>
  private static bool _isCollectionType(string fullyQualifiedTypeName) {
    return fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.List<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.IList<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.ICollection<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.IReadOnlyCollection<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.IEnumerable<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.Dictionary<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.IDictionary<", StringComparison.Ordinal) ||
           fullyQualifiedTypeName.StartsWith("global::System.Collections.Generic.IReadOnlyDictionary<", StringComparison.Ordinal);
  }

  /// <summary>
  /// Extracts type name from a direct (non-collection) property.
  /// Returns null if the type is a primitive, framework type, or collection.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithDirectPropertyNestedType_DiscoversNestedTypeAsync</tests>
  private static string? _extractDirectPropertyType(string fullyQualifiedTypeName) {
    // Strip nullable suffix if present
    var typeName = fullyQualifiedTypeName;
    if (typeName.EndsWith("?", StringComparison.Ordinal)) {
      typeName = typeName[..^1];
    }

    // Skip primitive and framework types
    if (_isPrimitiveOrFrameworkType(typeName)) {
      return null;
    }

    // Skip all System.* types - they're either handled natively by STJ or shouldn't be discovered
    if (typeName.StartsWith("global::System.", StringComparison.Ordinal)) {
      return null;
    }

    // Skip collection types (handled by _extractElementType)
    if (_isCollectionType(typeName)) {
      return null;
    }

    // Skip array types
    if (typeName.EndsWith("[]", StringComparison.Ordinal)) {
      return null;
    }

    // Return the type name for non-primitive, non-collection types
    return typeName;
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
      "global::System.DateOnly",
      "global::System.TimeOnly",
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
  /// Normalizes C# keyword aliases to their fully qualified CLR type names.
  /// This ensures consistent naming for generated identifiers (e.g., CreateList_System_Int32__Nullable instead of CreateList_int__Nullable).
  /// Handles both nullable (int?) and non-nullable (int) forms.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithListOfNullableInt_GeneratesListFactoryAsync</tests>
  private static string _normalizeKeywordAliases(string typeName) {
    // Map of C# keyword aliases to their fully qualified CLR names
    var keywordToClrType = new Dictionary<string, string> {
      { "int", "global::System.Int32" },
      { "uint", "global::System.UInt32" },
      { "long", "global::System.Int64" },
      { "ulong", "global::System.UInt64" },
      { "short", "global::System.Int16" },
      { "ushort", "global::System.UInt16" },
      { "byte", "global::System.Byte" },
      { "sbyte", "global::System.SByte" },
      { "bool", "global::System.Boolean" },
      { "char", "global::System.Char" },
      { "float", "global::System.Single" },
      { "double", "global::System.Double" },
      { "decimal", "global::System.Decimal" },
      { "string", "global::System.String" },
      { "object", "global::System.Object" }
    };

    // Check for nullable suffix
    var isNullable = typeName.EndsWith("?", StringComparison.Ordinal);
    var baseTypeName = isNullable ? typeName[..^1] : typeName;

    // Try to normalize the base type
    if (keywordToClrType.TryGetValue(baseTypeName, out var clrTypeName)) {
      return isNullable ? clrTypeName + "?" : clrTypeName;
    }

    // Return as-is if not a keyword alias
    return typeName;
  }

  /// <summary>
  /// Discovers WhizbangId JSON converters by examining property types in messages.
  /// Infers converter names for types that look like ID types (e.g., ProductId -> ProductIdJsonConverter).
  /// Uses naming conventions since source generators run in parallel and generated types may not be visible.
  /// Returns info about converters that need to be registered in JsonSerializerOptions.
  /// </summary>
  private static ImmutableArray<WhizbangIdTypeInfo> _discoverWhizbangIdConverters(
      ImmutableArray<JsonMessageTypeInfo> allTypes) {

    var converters = allTypes
        .SelectMany(type => type.Properties)
        .Where(property => !_isPrimitiveOrFrameworkType(property.Type))
        .Where(property => _extractElementType(property.Type) == null)
        .Select(property => {
          // Extract simple type name from fully qualified name
          // e.g., "global::ECommerce.Contracts.Commands.ProductId" -> "ProductId"
          var parts = property.Type.Replace(PLACEHOLDER_GLOBAL, "").Split('.');
          var typeName = parts[^1];
          return (property, parts, typeName);
        })
        .Where(x => x.typeName.EndsWith("Id", StringComparison.Ordinal))
        .Select(x => {
          // Infer converter name: {TypeName}JsonConverter
          var converterTypeName = $"{x.typeName}JsonConverter";

          // Infer converter namespace (same as the property type)
          var propertyTypeNamespace = string.Join(".", x.parts.Take(x.parts.Length - 1));
          var converterFullName = $"{propertyTypeNamespace}.{converterTypeName}";

          // Add the converter (optimistic registration - if it doesn't exist, compilation will fail with clear error)
          return new WhizbangIdTypeInfo(
              TypeName: converterTypeName,
              FullyQualifiedTypeName: converterFullName
          );
        })
        .GroupBy(c => c.TypeName)
        .Select(g => g.First())
        .ToImmutableArray();

    return converters;
  }

  /// <summary>
  /// Discovers array types (T[]) used in message properties.
  /// Returns info needed to generate explicit T[] JsonTypeInfo for AOT compatibility.
  /// Arrays are treated similarly to List&lt;T&gt; - when we discover a type, we support arrays of it.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithArrayProperty_DiscoversArrayTypeAsync</tests>
  private static ImmutableArray<ArrayTypeInfo> _discoverArrayTypes(ImmutableArray<JsonMessageTypeInfo> allTypes) {
    var arrayTypes = new Dictionary<string, ArrayTypeInfo>();

    foreach (var type in allTypes) {
      foreach (var property in type.Properties) {
        var rawTypeName = property.Type;

        // Strip nullable suffix if present (T[]? becomes T[])
        if (rawTypeName.EndsWith("?", StringComparison.Ordinal)) {
          rawTypeName = rawTypeName[..^1];
        }

        // Check if it's an array type
        if (!rawTypeName.EndsWith("[]", StringComparison.Ordinal)) {
          continue;
        }

        // Extract element type (remove the [] suffix)
        var elementTypeName = rawTypeName[..^2];

        // Normalize C# keyword aliases (int, bool, decimal) to fully qualified names
        elementTypeName = _normalizeKeywordAliases(elementTypeName);

        // Create key: ElementType[]
        var arrayTypeName = $"{elementTypeName}[]";
        if (arrayTypes.ContainsKey(arrayTypeName)) {
          continue;
        }

        // Extract simple name from fully qualified element type
        var parts = elementTypeName.Split('.');
        var elementSimpleName = parts[^1].Replace(PLACEHOLDER_GLOBAL, "");

        arrayTypes[arrayTypeName] = new ArrayTypeInfo(
            ArrayTypeName: arrayTypeName,
            ElementTypeName: elementTypeName,
            ElementSimpleName: elementSimpleName
        );
      }
    }

    return arrayTypes.Values.ToImmutableArray();
  }

  /// <summary>
  /// Discovers List&lt;T&gt; types used in message properties.
  /// Returns info needed to generate explicit List&lt;T&gt; JsonTypeInfo for AOT compatibility.
  /// </summary>
  private static ImmutableArray<ListTypeInfo> _discoverListTypes(ImmutableArray<JsonMessageTypeInfo> allTypes) {
    var listTypes = new Dictionary<string, ListTypeInfo>();

    foreach (var type in allTypes) {
      foreach (var property in type.Properties) {
        var rawElementTypeName = _extractElementType(property.Type);
        if (rawElementTypeName == null) {
          continue;
        }

        // Normalize C# keyword aliases (int, bool, decimal) to fully qualified names
        // This ensures consistent naming for generated identifiers (e.g., CreateList_System_Int32__Nullable)
        var elementTypeName = _normalizeKeywordAliases(rawElementTypeName);

        // Skip nested collection types (List<List<T>>, List<IEnumerable<T>>, etc.)
        // System.Text.Json handles nested collections natively - no custom factory needed
        // This also prevents invalid method names with <> characters
        // BUT: DO include nullable value types (Guid?, int?, DateTime?, etc.)
        if (_isNestedCollectionType(elementTypeName)) {
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
  /// Determines whether an element type is a nested collection type.
  /// Nested collections (List&lt;List&lt;T&gt;&gt;, List&lt;IEnumerable&lt;T&gt;&gt;, etc.) are handled natively by System.Text.Json.
  /// This returns false for value types like Guid?, int?, DateTime? which need explicit List&lt;T&gt; factories.
  /// </summary>
  private static bool _isNestedCollectionType(string elementTypeName) {
    // Only skip actual collection types, not value types like Guid?, int?, etc.
    // Collection types live under System.Collections.* or System.Linq.*
    return elementTypeName.StartsWith("global::System.Collections.", StringComparison.Ordinal) ||
           elementTypeName.StartsWith("global::System.Linq.", StringComparison.Ordinal);
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
          .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", listType.ElementUniqueIdentifier);
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
          .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", listType.ElementUniqueIdentifier);
      sb.AppendLine(factory);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  /// <summary>
  /// Discovers IReadOnlyList&lt;T&gt; types used in message properties.
  /// Returns info needed to generate explicit IReadOnlyList&lt;T&gt; JsonTypeInfo for AOT compatibility.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithIReadOnlyListProperty_GeneratesIReadOnlyListFactoryAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MultipleIReadOnlyListProperties_GeneratesAllFactoriesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_BugReport_IReadOnlyListCatalogItem_GeneratesFactoryAsync</tests>
  private static ImmutableArray<IReadOnlyListTypeInfo> _discoverIReadOnlyListTypes(ImmutableArray<JsonMessageTypeInfo> allTypes) {
    var iReadOnlyListTypes = new Dictionary<string, IReadOnlyListTypeInfo>();

    foreach (var type in allTypes) {
      foreach (var property in type.Properties) {
        _discoverIReadOnlyListType(property.Type, iReadOnlyListTypes);
      }
    }

    return iReadOnlyListTypes.Values.ToImmutableArray();
  }

  /// <summary>
  /// Extracts IReadOnlyList type info from a fully qualified type name if it's an IReadOnlyList type.
  /// </summary>
  private static void _discoverIReadOnlyListType(string fullyQualifiedTypeName, Dictionary<string, IReadOnlyListTypeInfo> iReadOnlyListTypes) {
    // Strip nullable suffix for analysis
    var typeName = fullyQualifiedTypeName;
    if (typeName.EndsWith("?", StringComparison.Ordinal)) {
      typeName = typeName[..^1];
    }

    const string iReadOnlyListPrefix = "global::System.Collections.Generic.IReadOnlyList<";

    if (typeName.StartsWith(iReadOnlyListPrefix, StringComparison.Ordinal)) {
      var startIndex = iReadOnlyListPrefix.Length;
      var endIndex = typeName.LastIndexOf('>');
      if (endIndex > startIndex) {
        var rawElementTypeName = typeName[startIndex..endIndex];
        var elementTypeName = _normalizeKeywordAliases(rawElementTypeName);

        // Create key using the IReadOnlyList type
        var iReadOnlyListTypeName = $"global::System.Collections.Generic.IReadOnlyList<{elementTypeName}>";
        if (iReadOnlyListTypes.ContainsKey(iReadOnlyListTypeName)) {
          return;
        }

        // Extract simple name from element type
        var parts = elementTypeName.Split('.');
        var elementSimpleName = parts[^1].Replace("global::", "").TrimEnd('?');

        iReadOnlyListTypes[iReadOnlyListTypeName] = new IReadOnlyListTypeInfo(
            IReadOnlyListTypeName: iReadOnlyListTypeName,
            ElementTypeName: elementTypeName,
            ElementSimpleName: elementSimpleName
        );

        // Recursively discover IReadOnlyList in the element type
        _discoverIReadOnlyListType(elementTypeName, iReadOnlyListTypes);
      }
    }

    // Also check if this is a collection containing IReadOnlyList (e.g., List<IReadOnlyList<T>>)
    var elementType = _extractElementTypeSingleLevel(typeName);
    if (elementType != null) {
      _discoverIReadOnlyListType(elementType, iReadOnlyListTypes);
    }
  }

  /// <summary>
  /// Generates lazy fields for IReadOnlyList&lt;T&gt; types.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithIReadOnlyListProperty_GeneratesIReadOnlyListFactoryAsync</tests>
  private static string _generateIReadOnlyListLazyFields(Assembly assembly, ImmutableArray<IReadOnlyListTypeInfo> iReadOnlyListTypes) {
    if (iReadOnlyListTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();

    // Suppress CS0169 warning for unused fields (fields are reserved for future lazy initialization)
    sb.AppendLine("#pragma warning disable CS0169  // Field is never used");
    sb.AppendLine();

    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "LAZY_FIELD_IREADONLYLIST");

    foreach (var iReadOnlyListType in iReadOnlyListTypes) {
      var field = snippet
          .Replace("__ELEMENT_TYPE__", iReadOnlyListType.ElementTypeName)
          .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", iReadOnlyListType.ElementUniqueIdentifier);
      sb.AppendLine(field);
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }

  /// <summary>
  /// Generates factory methods for IReadOnlyList&lt;T&gt; types.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithIReadOnlyListProperty_GeneratesIReadOnlyListFactoryAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_IReadOnlyListWithNestedGenericElement_GeneratesFactoryAsync</tests>
  private static string _generateIReadOnlyListFactories(Assembly assembly, ImmutableArray<IReadOnlyListTypeInfo> iReadOnlyListTypes) {
    if (iReadOnlyListTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();
    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "IREADONLYLIST_TYPE_FACTORY");

    foreach (var iReadOnlyListType in iReadOnlyListTypes) {
      var factory = snippet
          .Replace("__ELEMENT_TYPE__", iReadOnlyListType.ElementTypeName)
          .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", iReadOnlyListType.ElementUniqueIdentifier);
      sb.AppendLine(factory);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  /// <summary>
  /// Generates lazy fields for array types (T[]).
  /// </summary>
  private static string _generateArrayLazyFields(Assembly assembly, ImmutableArray<ArrayTypeInfo> arrayTypes) {
    if (arrayTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();

    // Suppress CS0169 warning for unused fields (fields are reserved for future lazy initialization)
    sb.AppendLine("#pragma warning disable CS0169  // Field is never used");
    sb.AppendLine();

    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "LAZY_FIELD_ARRAY");

    foreach (var arrayType in arrayTypes) {
      var field = snippet
          .Replace("__ELEMENT_TYPE__", arrayType.ElementTypeName)
          .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", arrayType.ElementUniqueIdentifier);
      sb.AppendLine(field);
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }

  /// <summary>
  /// Generates factory methods for array types (T[]).
  /// </summary>
  private static string _generateArrayFactories(Assembly assembly, ImmutableArray<ArrayTypeInfo> arrayTypes) {
    if (arrayTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();
    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "ARRAY_TYPE_FACTORY");

    foreach (var arrayType in arrayTypes) {
      var factory = snippet
          .Replace("__ELEMENT_TYPE__", arrayType.ElementTypeName)
          .Replace("__ELEMENT_UNIQUE_IDENTIFIER__", arrayType.ElementUniqueIdentifier);
      sb.AppendLine(factory);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  /// <summary>
  /// Discovers Dictionary&lt;TKey, TValue&gt; types used in message properties.
  /// Returns info needed to generate explicit Dictionary JsonTypeInfo for AOT compatibility.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithDictionaryProperty_GeneratesDictionaryFactoryAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MultipleDictionaryProperties_GeneratesAllFactoriesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MultipleDictionaryProperties_DiscoversAllValueTypesAsync</tests>
  private static ImmutableArray<DictionaryTypeInfo> _discoverDictionaryTypes(ImmutableArray<JsonMessageTypeInfo> allTypes) {
    var dictionaryTypes = new Dictionary<string, DictionaryTypeInfo>();

    foreach (var type in allTypes) {
      foreach (var property in type.Properties) {
        _discoverDictionaryType(property.Type, dictionaryTypes);
      }
    }

    return dictionaryTypes.Values.ToImmutableArray();
  }

  /// <summary>
  /// Extracts Dictionary type info from a fully qualified type name if it's a Dictionary type.
  /// </summary>
  private static void _discoverDictionaryType(string fullyQualifiedTypeName, Dictionary<string, DictionaryTypeInfo> dictionaryTypes) {
    // Strip nullable suffix for analysis
    var typeName = fullyQualifiedTypeName;
    if (typeName.EndsWith("?", StringComparison.Ordinal)) {
      typeName = typeName[..^1];
    }

    // Check for dictionary types
    var dictionaryPrefixes = new[] {
      "global::System.Collections.Generic.Dictionary<",
      "global::System.Collections.Generic.IDictionary<",
      "global::System.Collections.Generic.IReadOnlyDictionary<"
    };

    var matchingPrefix = dictionaryPrefixes.FirstOrDefault(prefix =>
        typeName.StartsWith(prefix, StringComparison.Ordinal));

    if (matchingPrefix != null) {
      var startIndex = matchingPrefix.Length;
      var endIndex = typeName.LastIndexOf('>');
      if (endIndex > startIndex) {
        var typeArgs = typeName[startIndex..endIndex];
        var commaIndex = _findTopLevelComma(typeArgs);
        if (commaIndex > 0) {
          var keyType = _normalizeKeywordAliases(typeArgs[..commaIndex].Trim());
          var valueType = _normalizeKeywordAliases(typeArgs[(commaIndex + 1)..].Trim());

          // Create key using the concrete Dictionary type (not interface)
          var dictionaryTypeName = $"global::System.Collections.Generic.Dictionary<{keyType}, {valueType}>";
          if (dictionaryTypes.ContainsKey(dictionaryTypeName)) {
            return;
          }

          // Extract simple name from value type
          var parts = valueType.Split('.');
          var valueSimpleName = parts[^1].Replace("global::", "").TrimEnd('?');

          dictionaryTypes[dictionaryTypeName] = new DictionaryTypeInfo(
              DictionaryTypeName: dictionaryTypeName,
              KeyTypeName: keyType,
              ValueTypeName: valueType,
              ValueSimpleName: valueSimpleName
          );

          // Recursively discover dictionaries in the value type
          _discoverDictionaryType(valueType, dictionaryTypes);
        }
      }
    }

    // Also check if this is a collection containing dictionaries (e.g., List<Dictionary<K,V>>)
    var elementType = _extractElementTypeSingleLevel(typeName);
    if (elementType != null) {
      _discoverDictionaryType(elementType, dictionaryTypes);
    }
  }

  /// <summary>
  /// Generates lazy fields for Dictionary&lt;TKey, TValue&gt; types.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithDictionaryProperty_GeneratesDictionaryFactoryAsync</tests>
  private static string _generateDictionaryLazyFields(Assembly assembly, ImmutableArray<DictionaryTypeInfo> dictionaryTypes) {
    if (dictionaryTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();

    // Suppress CS0169 warning for unused fields (fields are reserved for future lazy initialization)
    sb.AppendLine("#pragma warning disable CS0169  // Field is never used");
    sb.AppendLine();

    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "LAZY_FIELD_DICTIONARY");

    foreach (var dictType in dictionaryTypes) {
      var field = snippet
          .Replace("__KEY_TYPE__", dictType.KeyTypeName)
          .Replace("__VALUE_TYPE__", dictType.ValueTypeName)
          .Replace("__UNIQUE_IDENTIFIER__", dictType.UniqueIdentifier);
      sb.AppendLine(field);
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }

  /// <summary>
  /// Generates factory methods for Dictionary&lt;TKey, TValue&gt; types.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithDictionaryProperty_GeneratesDictionaryFactoryAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_DictionaryWithNestedGenericValue_GeneratesFactoryAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MultipleDictionaryProperties_GeneratesAllFactoriesAsync</tests>
  private static string _generateDictionaryFactories(Assembly assembly, ImmutableArray<DictionaryTypeInfo> dictionaryTypes) {
    if (dictionaryTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();
    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "DICTIONARY_TYPE_FACTORY");

    foreach (var dictType in dictionaryTypes) {
      var factory = snippet
          .Replace("__KEY_TYPE__", dictType.KeyTypeName)
          .Replace("__VALUE_TYPE__", dictType.ValueTypeName)
          .Replace("__UNIQUE_IDENTIFIER__", dictType.UniqueIdentifier);
      sb.AppendLine(factory);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  /// <summary>
  /// Generates lazy fields for enum types (both non-nullable and nullable versions).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithEnumProperty_DiscoversEnumAsync</tests>
  private static string _generateEnumLazyFields(Assembly assembly, ImmutableArray<JsonEnumInfo> enumTypes) {
    if (enumTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();

    // Suppress CS0169 warning for unused fields (fields are reserved for future lazy initialization)
    sb.AppendLine("#pragma warning disable CS0169  // Field is never used");
    sb.AppendLine();

    var enumSnippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "LAZY_FIELD_ENUM");
    var nullableEnumSnippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "LAZY_FIELD_NULLABLE_ENUM");

    foreach (var enumType in enumTypes) {
      // Non-nullable enum
      var field = enumSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, enumType.FullyQualifiedName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, enumType.UniqueIdentifier);
      sb.AppendLine(field);

      // Nullable enum (always generate both - no need to discover which are used as nullable)
      var nullableField = nullableEnumSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, enumType.FullyQualifiedName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, enumType.UniqueIdentifier);
      sb.AppendLine(nullableField);
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }

  /// <summary>
  /// Generates factory methods for enum types (both non-nullable and nullable versions).
  /// Uses JsonMetadataServices.GetEnumConverter for non-nullable and GetNullableConverter for nullable.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_MessageWithEnumProperty_DiscoversEnumAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_NestedTypeWithEnumProperty_DiscoversEnumAsync</tests>
  private static string _generateEnumFactories(Assembly assembly, ImmutableArray<JsonEnumInfo> enumTypes) {
    if (enumTypes.IsEmpty) {
      return string.Empty;
    }

    var sb = new System.Text.StringBuilder();
    var enumSnippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "ENUM_TYPE_FACTORY");
    var nullableEnumSnippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "NULLABLE_ENUM_TYPE_FACTORY");

    foreach (var enumType in enumTypes) {
      // Non-nullable enum factory
      var factory = enumSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, enumType.FullyQualifiedName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, enumType.UniqueIdentifier);
      sb.AppendLine(factory);
      sb.AppendLine();

      // Nullable enum factory (always generate both - no need to discover which are used as nullable)
      var nullableFactory = nullableEnumSnippet
          .Replace(PLACEHOLDER_FULLY_QUALIFIED_NAME, enumType.FullyQualifiedName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, enumType.UniqueIdentifier);
      sb.AppendLine(nullableFactory);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  // ========================================
  // Helper Methods for _discoverNestedTypes Complexity Reduction
  // ========================================

  /// <summary>
  /// Checks if a type has the [WhizbangId] attribute.
  /// Types with [WhizbangId] have their own JSON converters generated by WhizbangIdGenerator
  /// and should NOT have JsonTypeInfo generated by MessageJsonContextGenerator.
  /// We check for the attribute (not IWhizbangId interface) because generators run in parallel -
  /// MessageJsonContextGenerator may not see the interface that WhizbangIdGenerator adds.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithWhizbangIdProperty_SkipsConverterGenerationAsync</tests>
  private static bool _hasWhizbangIdAttribute(INamedTypeSymbol typeSymbol) {
    return typeSymbol.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{WHIZBANG_ID_ATTRIBUTE}");
  }

  /// <summary>
  /// Checks if a type is used as a perspective model (TModel in IPerspectiveFor&lt;TModel, ...&gt;).
  /// Perspective models are stored as JSONB in the database and need JSON serialization.
  /// Looks for containing type, sibling types, or nested types that implement IPerspectiveFor with this type as TModel.
  /// </summary>
  private static bool _isPerspectiveModelType(INamedTypeSymbol typeSymbol) {
    // Get the containing type (for nested types) or containing namespace members
    var containingType = typeSymbol.ContainingType;

    // Build list of types to check for IPerspectiveFor<ThisType, ...> implementations
    var typesToCheck = new List<INamedTypeSymbol>();

    if (containingType != null) {
      // For nested types:
      // 1. Check the containing type itself (e.g., ChatSession implements IPerspectiveFor<ChatSessionModel>)
      typesToCheck.Add(containingType);
      // 2. Check all sibling types nested in the same container
      typesToCheck.AddRange(containingType.GetTypeMembers());
    } else {
      // For top-level types, check other types in the same namespace
      // This is more expensive but handles the common case of projection classes
      var containingNamespace = typeSymbol.ContainingNamespace;
      if (containingNamespace == null) {
        return false;
      }
      typesToCheck.AddRange(containingNamespace.GetTypeMembers());
    }

    foreach (var candidateType in typesToCheck) {
      // Check if this type implements IPerspectiveFor<typeSymbol, ...>
      foreach (var iface in candidateType.AllInterfaces) {
        var ifaceName = iface.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Check if it's IPerspectiveFor<TModel, ...> (can have 2-10 type arguments)
        if (!ifaceName.StartsWith($"global::{I_PERSPECTIVE_FOR}<", System.StringComparison.Ordinal)) {
          continue;
        }

        // Check if the first type argument is our type
        if (iface.TypeArguments.Length > 0) {
          var modelType = iface.TypeArguments[0];
          if (SymbolEqualityComparer.Default.Equals(modelType, typeSymbol)) {
            return true;
          }
        }
      }
    }

    return false;
  }

  /// <summary>
  /// Checks if a type has the [JsonPolymorphic] attribute.
  /// Types with this attribute indicate they have derived types that should be discovered.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithJsonPolymorphicAbstractType_DiscoversDerivedTypesAsync</tests>
  private static bool _hasJsonPolymorphicAttribute(INamedTypeSymbol typeSymbol) {
    return typeSymbol.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ==
        "global::System.Text.Json.Serialization.JsonPolymorphicAttribute");
  }

  /// <summary>
  /// Discovers derived types from [JsonDerivedType] attributes on a polymorphic base type.
  /// Returns public, non-abstract derived types that can be instantiated.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithJsonDerivedTypeAttributes_DiscoversDerivedTypesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithJsonDerivedTypeInDifferentNamespace_DiscoversAsync</tests>
  private static List<INamedTypeSymbol> _discoverDerivedTypesFromAttributes(
      INamedTypeSymbol polymorphicBaseType,
      Compilation compilation) {

    var discoveredTypes = new List<INamedTypeSymbol>();

    foreach (var attr in polymorphicBaseType.GetAttributes()) {
      // Check for [JsonDerivedType(typeof(DerivedType), ...)]
      if (attr.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) !=
          "global::System.Text.Json.Serialization.JsonDerivedTypeAttribute") {
        continue;
      }

      // First constructor argument is the derived type
      if (attr.ConstructorArguments.Length == 0) {
        continue;
      }

      var typeArg = attr.ConstructorArguments[0];
      if (typeArg.Value is not INamedTypeSymbol derivedType) {
        continue;
      }

      // Skip abstract types (they need their own discovery)
      if (derivedType.IsAbstract) {
        continue;
      }

      // Skip non-public types
      if (derivedType.DeclaredAccessibility != Accessibility.Public) {
        continue;
      }

      discoveredTypes.Add(derivedType);
    }

    return discoveredTypes;
  }

  /// <summary>
  /// Attempts to get a public type symbol from the compilation.
  /// Returns null if type doesn't exist or isn't public.
  /// Handles nested types by trying progressively converting '.' to '+' from right to left.
  /// </summary>
  /// <remarks>
  /// GetTypeByMetadataName expects metadata format with '+' for nested types:
  /// - Top-level: "Namespace.ClassName"
  /// - Nested: "Namespace.ContainerClass+NestedClass"
  ///
  /// But property types come from ToDisplayString which uses '.' for nested types:
  /// - "global::Namespace.ContainerClass.NestedClass"
  ///
  /// This method tries both formats to handle nested types correctly.
  /// </remarks>
  private static INamedTypeSymbol? _tryGetPublicTypeSymbol(string elementTypeName, Compilation compilation) {
    var typeName = elementTypeName.Replace(PLACEHOLDER_GLOBAL, "");

    // First try direct lookup (works for non-nested types)
    var typeSymbol = compilation.GetTypeByMetadataName(typeName);
    if (typeSymbol != null && typeSymbol.DeclaredAccessibility == Accessibility.Public) {
      return typeSymbol;
    }

    // If not found, try converting '.' to '+' for potential nested types
    // Start from the rightmost '.' and work left (handles deeper nesting levels)
    // Example: "Namespace.Container.Nested" -> try "Namespace.Container+Nested"
    // Example: "Ns.A.B.C" for nested B.C -> try "Ns.A+B.C", then "Ns.A+B+C"
    var chars = typeName.ToCharArray();
    for (int i = chars.Length - 1; i >= 0; i--) {
      if (chars[i] == '.') {
        chars[i] = '+';
        var candidate = new string(chars);
        typeSymbol = compilation.GetTypeByMetadataName(candidate);
        if (typeSymbol != null && typeSymbol.DeclaredAccessibility == Accessibility.Public) {
          return typeSymbol;
        }
      }
    }

    return null;
  }

  /// <summary>
  /// Extracts property information from a type symbol, including inherited properties.
  /// </summary>
  private static PropertyInfo[] _extractPropertiesFromType(INamedTypeSymbol typeSymbol) {
    return _getAllPropertiesIncludingInherited(typeSymbol)
        .Select(p => new PropertyInfo(
            Name: p.Name,
            Type: p.Type.ToDisplayString(_fullyQualifiedWithNullabilityFormat),
            IsValueType: _isValueType(p.Type),
            IsInitOnly: p.SetMethod?.IsInitOnly ?? false,
            CanWrite: p.SetMethod != null
        ))
        .ToArray();
  }

  /// <summary>
  /// Gets all public instance properties from a type, including inherited properties.
  /// Properties are returned in order: base class properties first, then derived class properties.
  /// Uses property name to dedupe (derived class property overrides base class property).
  /// </summary>
  private static List<IPropertySymbol> _getAllPropertiesIncludingInherited(INamedTypeSymbol typeSymbol) {
    // Use shared utility (returns derived→base order) and reverse to get base→derived order
    return typeSymbol.GetAllProperties().Reverse().ToList();
  }

  /// <summary>
  /// Checks if a type has a parameterized constructor matching its writable properties.
  /// Computed properties (CanWrite = false) are excluded from constructor matching.
  /// </summary>
  private static bool _hasMatchingParameterizedConstructor(INamedTypeSymbol typeSymbol, PropertyInfo[] properties) {
    var writableProperties = properties.Where(p => p.CanWrite).ToArray();
    return typeSymbol.Constructors.Any(c =>
        c.DeclaredAccessibility == Accessibility.Public &&
        c.Parameters.Length == writableProperties.Length &&
        c.Parameters.All(p => writableProperties.Any(prop =>
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

    var typeRegistrations = messageTypes.Select(message => {
      // Use CLR type name format (uses + for nested types) for runtime type resolution
      var assemblyQualifiedName = $"{message.ClrTypeName}, {actualAssemblyName}";

      return $"  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterTypeName(\n" +
             $"    \"{assemblyQualifiedName}\",\n" +
             $"    typeof({message.FullyQualifiedName}),\n" +
             $"    MessageJsonContext.Default);";
    });
    sb.AppendLine(string.Join("\n", typeRegistrations));
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

    var envelopeRegistrations = messageTypes.Select(message => {
      // Use CLR type name format (uses + for nested types) for runtime type resolution
      var envelopeTypeName = $"Whizbang.Core.Observability.MessageEnvelope`1[[{message.ClrTypeName}, {actualAssemblyName}]], Whizbang.Core";

      return $"  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterTypeName(\n" +
             $"    \"{envelopeTypeName}\",\n" +
             $"    typeof(global::Whizbang.Core.Observability.MessageEnvelope<{message.FullyQualifiedName}>),\n" +
             $"    MessageJsonContext.Default);";
    });
    sb.AppendLine(string.Join("\n", envelopeRegistrations));
  }

  // ========================================
  // Polymorphic Type Discovery and Generation
  // ========================================

  /// <summary>
  /// Extracts inheritance relationships from a type symbol.
  /// Records each derived-to-base relationship for polymorphic serialization support.
  /// Skips System.* and Whizbang.Core.I* interfaces (ICommand, IEvent, IMessage).
  /// </summary>
  /// <param name="typeSymbol">The type symbol to extract inheritance from</param>
  /// <returns>Array of InheritanceInfo records for each base class and interface</returns>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithUserBaseClass_AutoDiscoversPolymorphicTypesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithDeepInheritance_DiscoversAllLevelsAsync</tests>
  /// <docs>source-generators/polymorphic-serialization</docs>
  private static InheritanceInfo[] _extractInheritanceInfo(INamedTypeSymbol typeSymbol) {
    var inheritanceList = new List<InheritanceInfo>();
    var derivedTypeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Walk up base type chain (classes only)
    var currentBase = typeSymbol.BaseType;
    while (currentBase != null) {
      var baseTypeName = currentBase.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

      // Skip System.* types (object, ValueType, etc.)
      // Also skip C# keyword aliases like "object", "string" which FullyQualifiedFormat may return
      if (baseTypeName.StartsWith("global::System.", StringComparison.Ordinal) ||
          baseTypeName == "object" ||
          baseTypeName == "string") {
        break; // Stop walking up the chain once we hit System types
      }

      // Record the relationship for both abstract and non-abstract base classes
      // so derived types are discovered for polymorphic serialization
      inheritanceList.Add(new InheritanceInfo(
          DerivedTypeName: derivedTypeName,
          BaseTypeName: baseTypeName,
          IsInterface: false
      ));

      currentBase = currentBase.BaseType;
    }

    // Process interfaces (excluding core Whizbang interfaces and System interfaces)
    foreach (var iface in typeSymbol.AllInterfaces) {
      var interfaceName = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

      // Skip System.* interfaces
      if (interfaceName.StartsWith("global::System.", StringComparison.Ordinal)) {
        continue;
      }

      // Include Whizbang.Core.ICommand and Whizbang.Core.IEvent for polymorphic collections
      // Skip other Whizbang.Core.* interfaces (IMessage, IHasId, etc.)
      if (interfaceName.StartsWith("global::Whizbang.Core.", StringComparison.Ordinal)) {
        if (interfaceName != $"global::{I_COMMAND}" && interfaceName != $"global::{I_EVENT}") {
          continue;
        }
      }

      inheritanceList.Add(new InheritanceInfo(
          DerivedTypeName: derivedTypeName,
          BaseTypeName: interfaceName,
          IsInterface: true
      ));
    }

    return inheritanceList.ToArray();
  }

  /// <summary>
  /// Builds a polymorphic registry by grouping inheritance info by base type.
  /// Excludes base types that already have explicit [JsonPolymorphic] attribute.
  /// </summary>
  /// <param name="allInheritanceInfo">All inheritance relationships discovered from message types</param>
  /// <param name="compilation">The compilation for type lookup</param>
  /// <returns>Array of PolymorphicTypeInfo records for each polymorphic base type</returns>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithExplicitJsonPolymorphic_UsesUserAttributesAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs:Generator_WithAbstractDerivedType_ExcludesItAsync</tests>
  /// <docs>source-generators/polymorphic-serialization</docs>
  private static ImmutableArray<PolymorphicTypeInfo> _buildPolymorphicRegistry(
      ImmutableArray<InheritanceInfo> allInheritanceInfo,
      Compilation compilation) {

    // Group inheritance info by base type
    var grouped = allInheritanceInfo
        .GroupBy(i => i.BaseTypeName)
        .Where(g => g.Any()); // At least one derived type

    var registry = new List<PolymorphicTypeInfo>();

    foreach (var group in grouped) {
      var baseTypeName = group.Key;
      var isInterface = group.First().IsInterface;

      // Try to get the base type symbol to check for [JsonPolymorphic]
      var baseSymbol = _tryGetTypeSymbolByName(baseTypeName, compilation);
      if (baseSymbol != null) {
        // Skip if base type already has explicit [JsonPolymorphic] attribute
        if (_hasJsonPolymorphicAttribute(baseSymbol)) {
          continue;
        }

        // Skip non-public base types
        if (baseSymbol.DeclaredAccessibility != Accessibility.Public) {
          continue;
        }

        // Skip abstract base types that are not interfaces
        // Abstract classes can't be instantiated, so no point in generating polymorphic factories
        // Interfaces (ICommand, IEvent) ARE allowed even though they can't be instantiated
        if (!isInterface && baseSymbol.IsAbstract) {
          continue;
        }
      }

      // Extract simple name from fully qualified base type name
      var simpleName = _extractSimpleName(baseTypeName);

      // Get all derived type names, excluding abstract types
      var derivedTypes = group
          .Select(i => i.DerivedTypeName)
          .Distinct()
          .Where(derivedName => {
            // Exclude abstract derived types - they can't be instantiated
            var derivedSymbol = _tryGetTypeSymbolByName(derivedName, compilation);
            if (derivedSymbol == null) {
              return false;
            }
            if (derivedSymbol.IsAbstract) {
              return false;
            }
            if (derivedSymbol.DeclaredAccessibility != Accessibility.Public) {
              return false;
            }
            return true;
          })
          .ToImmutableArray();

      // Only create polymorphic info if there are concrete derived types
      if (derivedTypes.Length == 0) {
        continue;
      }

      registry.Add(new PolymorphicTypeInfo(
          BaseTypeName: baseTypeName,
          BaseSimpleName: simpleName,
          DerivedTypes: derivedTypes,
          IsInterface: isInterface
      ));
    }

    return registry.ToImmutableArray();
  }

  /// <summary>
  /// Extracts simple type name from fully qualified name.
  /// E.g., "global::MyApp.Events.BaseEvent" → "BaseEvent"
  /// </summary>
  private static string _extractSimpleName(string fullyQualifiedName) {
    var name = fullyQualifiedName.Replace("global::", "");
    var lastDot = name.LastIndexOf('.');
    return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
  }

  /// <summary>
  /// Tries to get a type symbol by its fully qualified name.
  /// Returns null if the type cannot be found.
  /// </summary>
  private static INamedTypeSymbol? _tryGetTypeSymbolByName(string fullyQualifiedName, Compilation compilation) {
    // Remove global:: prefix for GetTypeByMetadataName
    var metadataName = fullyQualifiedName.Replace("global::", "");
    return compilation.GetTypeByMetadataName(metadataName);
  }

  /// <summary>
  /// Generates lazy fields for polymorphic base types.
  /// </summary>
  private static string _generatePolymorphicLazyFields(Assembly assembly, ImmutableArray<PolymorphicTypeInfo> polymorphicTypes) {
    if (polymorphicTypes.IsEmpty) {
      return "";
    }

    var sb = new System.Text.StringBuilder();

    // Suppress CS0169 warning for unused fields (fields are reserved for future lazy initialization)
    sb.AppendLine("#pragma warning disable CS0169  // Field is never used");
    sb.AppendLine();

    var lazyFieldSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "LAZY_FIELD_POLYMORPHIC");

    sb.AppendLine("  // Polymorphic base types for automatic JSON serialization");
    foreach (var polyType in polymorphicTypes) {
      var field = lazyFieldSnippet
          .Replace("__BASE_TYPE__", polyType.BaseTypeName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, polyType.UniqueIdentifier);
      sb.AppendLine(field);
    }

    // Restore CS0169 warning
    sb.AppendLine();
    sb.AppendLine("#pragma warning restore CS0169");

    return sb.ToString();
  }

  /// <summary>
  /// Generates GetTypeInfo checks for polymorphic base types.
  /// </summary>
  private static string _generatePolymorphicTypeChecks(Assembly assembly, ImmutableArray<PolymorphicTypeInfo> polymorphicTypes) {
    if (polymorphicTypes.IsEmpty) {
      return "";
    }

    var sb = new System.Text.StringBuilder();
    var typeCheckSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "GET_TYPE_INFO_POLYMORPHIC");

    sb.AppendLine("  // Polymorphic base types");
    foreach (var polyType in polymorphicTypes) {
      var check = typeCheckSnippet
          .Replace("__BASE_TYPE__", polyType.BaseTypeName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, polyType.UniqueIdentifier);
      sb.AppendLine(check);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  /// <summary>
  /// Generates factory methods for polymorphic base types.
  /// </summary>
  private static string _generatePolymorphicFactories(Assembly assembly, ImmutableArray<PolymorphicTypeInfo> polymorphicTypes) {
    if (polymorphicTypes.IsEmpty) {
      return "";
    }

    var sb = new System.Text.StringBuilder();
    var factorySnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "POLYMORPHIC_TYPE_FACTORY");
    var derivedRegistrationSnippet = TemplateUtilities.ExtractSnippet(
        assembly,
        TEMPLATE_SNIPPET_FILE,
        "POLYMORPHIC_DERIVED_REGISTRATION");

    foreach (var polyType in polymorphicTypes) {
      // Build derived type registrations
      var registrations = new System.Text.StringBuilder();
      foreach (var derivedType in polyType.DerivedTypes) {
        var discriminator = _extractSimpleName(derivedType);
        var registration = derivedRegistrationSnippet
            .Replace("__DERIVED_TYPE__", derivedType)
            .Replace("__DERIVED_TYPE_DISCRIMINATOR__", discriminator);
        registrations.AppendLine(registration);
      }

      // Generate factory method
      var factory = factorySnippet
          .Replace("__BASE_TYPE__", polyType.BaseTypeName)
          .Replace(PLACEHOLDER_UNIQUE_IDENTIFIER, polyType.UniqueIdentifier)
          .Replace("__DERIVED_TYPE_REGISTRATIONS__", registrations.ToString());
      sb.AppendLine(factory);
      sb.AppendLine();
    }

    return sb.ToString();
  }

  /// <summary>
  /// Collects inheritance information from all message types for polymorphic serialization.
  /// </summary>
  /// <param name="messages">All discovered message types (commands, events, serializable types)</param>
  /// <param name="compilation">The compilation for type symbol lookup</param>
  /// <returns>Flat array of all inheritance relationships</returns>
  /// <docs>source-generators/polymorphic-serialization</docs>
  private static ImmutableArray<InheritanceInfo> _collectAllInheritanceInfo(
      ImmutableArray<JsonMessageTypeInfo> messages,
      Compilation compilation) {

    var allInheritance = new List<InheritanceInfo>();

    foreach (var message in messages) {
      // Get the type symbol for this message
      var typeSymbol = _tryGetTypeSymbolByName(message.FullyQualifiedName, compilation);
      if (typeSymbol == null) {
        continue;
      }

      // Extract inheritance info for this type
      var inheritanceInfo = _extractInheritanceInfo(typeSymbol);
      allInheritance.AddRange(inheritanceInfo);
    }

    return allInheritance.ToImmutableArray();
  }
}
