using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithExplicitTypeDeclaration_GeneratesValueObjectAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithMultipleIdTypes_GeneratesAllAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithCustomNamespace_UsesSpecifiedNamespaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithNamespaceProperty_UsesSpecifiedNamespaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithNonPartialStruct_ProducesDiagnosticAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithIdType_GeneratesJsonConverterAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithPropertyBasedDiscovery_GeneratesValueObjectAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithParameterBasedDiscovery_GeneratesValueObjectAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithHybridDiscovery_GeneratesAllIdsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithPropertyBasedAndCustomNamespace_UsesCustomNamespaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithDuplicateDiscovery_GeneratesOnlyOnceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithCollision_EmitsDiagnosticAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdGeneratorTests.cs:Generator_WithCollisionSuppressed_NoWarningAsync</tests>
/// Source generator that discovers [WhizbangId] attributes and generates strongly-typed ID value objects.
/// Supports three discovery patterns: explicit type, property-based, and parameter-based.
/// Generated IDs include UUIDv7 support, equality, JSON converters, and auto-registration.
/// </summary>
[Generator]
public class WhizbangIdGenerator : IIncrementalGenerator {
  private const string WHIZBANGID_ATTRIBUTE = "Whizbang.Core.WhizbangIdAttribute";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Phase 2.1: Type-based discovery - [WhizbangId] on struct declarations
    var typeBasedIds = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is StructDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => _extractTypeBasedId(ctx, ct)
    ).Where(static info => info is not null);

    // Phase 2.2: Property-based discovery - [WhizbangId] on property types
    var propertyBasedIds = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is PropertyDeclarationSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => _extractPropertyBasedId(ctx, ct)
    ).Where(static info => info is not null);

    // Phase 2.3: Parameter-based discovery - [WhizbangId] on primary constructor parameters
    var parameterBasedIds = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ParameterSyntax { AttributeLists.Count: > 0 },
        transform: static (ctx, ct) => _extractParameterBasedId(ctx, ct)
    ).Where(static info => info is not null);

    // Combine all discovery sources
    var allIds = typeBasedIds.Collect()
        .Combine(propertyBasedIds.Collect())
        .Combine(parameterBasedIds.Collect());

    // Generate code from discovered IDs and emit diagnostics
    context.RegisterSourceOutput(
        allIds,
        static (ctx, data) => {
          var typeIds = data.Left.Left;
          var propertyIds = data.Left.Right;
          var parameterIds = data.Right;

          // Flatten all IDs into single array
          var builder = ImmutableArray.CreateBuilder<(WhizbangIdInfo?, Location?, string?)?>();
          builder.AddRange(typeIds);
          builder.AddRange(propertyIds);
          builder.AddRange(parameterIds);

          _generateWhizbangIds(ctx, builder.ToImmutable());
        }
    );

    // Generate WhizbangIdJsonContext for JSON serialization
    // Combine compilation with discovered IDs to get assembly name for namespace
    var compilationAndIds = context.CompilationProvider.Combine(allIds);

    context.RegisterSourceOutput(
        compilationAndIds,
        static (ctx, data) => {
          var compilation = data.Left;
          var idsData = data.Right;

          var typeIds = idsData.Left.Left;
          var propertyIds = idsData.Left.Right;
          var parameterIds = idsData.Right;

          // Flatten all IDs into single array
          var builder = ImmutableArray.CreateBuilder<(WhizbangIdInfo?, Location?, string?)?>();
          builder.AddRange(typeIds);
          builder.AddRange(propertyIds);
          builder.AddRange(parameterIds);

          _generateWhizbangIdJsonContext(ctx, compilation, builder.ToImmutable());
        }
    );
  }

  /// <summary>
  /// Extracts WhizbangIdInfo from a struct declaration with [WhizbangId] attribute.
  /// Returns a tuple of (WhizbangIdInfo, DiagnosticInfo) where DiagnosticInfo can be null or an error.
  /// </summary>
  private static (WhizbangIdInfo?, Location?, string?)? _extractTypeBasedId(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var structDecl = (StructDeclarationSyntax)context.Node;
    var structSymbol = context.SemanticModel.GetDeclaredSymbol(structDecl, ct);

    if (structSymbol is null) {
      return null;
    }

    // Check for [WhizbangId] attribute
    var whizbangIdAttr = structSymbol.GetAttributes().FirstOrDefault(a =>
        a.AttributeClass?.Name == "WhizbangIdAttribute" ||
        a.AttributeClass?.Name == "WhizbangId" ||
        a.AttributeClass?.ToDisplayString() == WHIZBANGID_ATTRIBUTE ||
        a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{WHIZBANGID_ATTRIBUTE}");

    if (whizbangIdAttr is null) {
      return null;
    }

    // Check if struct is partial (required for source generation)
    if (!structDecl.Modifiers.Any(m => m.Text == "partial")) {
      // Emit WHIZ021 diagnostic - return location and type name for diagnostic
      return (null, structDecl.GetLocation(), structSymbol.Name);
    }

    // Extract namespace - either from attribute or containing namespace
    string targetNamespace = structSymbol.ContainingNamespace?.ToDisplayString() ?? "Global";

    // Check for Namespace property in attribute
    var namespaceArg = whizbangIdAttr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Namespace");
    if (namespaceArg.Value.Value is string customNamespace && !string.IsNullOrWhiteSpace(customNamespace)) {
      targetNamespace = customNamespace;
    }

    // Check for constructor argument with namespace
    if (whizbangIdAttr.ConstructorArguments.Length > 0 &&
        whizbangIdAttr.ConstructorArguments[0].Value is string constructorNamespace &&
        !string.IsNullOrWhiteSpace(constructorNamespace)) {
      targetNamespace = constructorNamespace;
    }

    // Extract SuppressDuplicateWarning property
    var suppressWarning = false;
    var suppressArg = whizbangIdAttr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "SuppressDuplicateWarning");
    if (suppressArg.Value.Value is bool suppress) {
      suppressWarning = suppress;
    }

    var idInfo = new WhizbangIdInfo(
        TypeName: structSymbol.Name,
        Namespace: targetNamespace,
        Source: DiscoverySource.ExplicitType,
        SuppressDuplicateWarning: suppressWarning
    );

    return (idInfo, null, null);
  }

  /// <summary>
  /// Extracts WhizbangIdInfo from a property with [WhizbangId] attribute.
  /// The type of the property becomes the generated ID type.
  /// </summary>
  private static (WhizbangIdInfo?, Location?, string?)? _extractPropertyBasedId(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
    var propertyDecl = (PropertyDeclarationSyntax)context.Node;

    if (context.SemanticModel.GetDeclaredSymbol(propertyDecl, ct) is not IPropertySymbol propertySymbol) {
      return null;
    }

    // Check for [WhizbangId] attribute
    var whizbangIdAttr = propertySymbol.GetAttributes().FirstOrDefault(a =>
        a.AttributeClass?.Name == "WhizbangIdAttribute" ||
        a.AttributeClass?.Name == "WhizbangId" ||
        a.AttributeClass?.ToDisplayString() == WHIZBANGID_ATTRIBUTE ||
        a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{WHIZBANGID_ATTRIBUTE}");

    if (whizbangIdAttr is null) {
      return null;
    }

    // Extract the property type name (e.g., "ProductId" from "public ProductId Id { get; set; }")
    var typeName = propertySymbol.Type.Name;

    // Extract namespace - either from attribute or containing type's namespace
    var containingType = propertySymbol.ContainingType;
    string targetNamespace = containingType?.ContainingNamespace?.ToDisplayString() ?? "Global";

    // Check for Namespace property in attribute
    var namespaceArg = whizbangIdAttr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Namespace");
    if (namespaceArg.Value.Value is string customNamespace && !string.IsNullOrWhiteSpace(customNamespace)) {
      targetNamespace = customNamespace;
    }

    // Check for constructor argument with namespace
    if (whizbangIdAttr.ConstructorArguments.Length > 0 &&
        whizbangIdAttr.ConstructorArguments[0].Value is string constructorNamespace &&
        !string.IsNullOrWhiteSpace(constructorNamespace)) {
      targetNamespace = constructorNamespace;
    }

    // Extract SuppressDuplicateWarning property
    var suppressWarning = false;
    var suppressArg = whizbangIdAttr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "SuppressDuplicateWarning");
    if (suppressArg.Value.Value is bool suppress) {
      suppressWarning = suppress;
    }

    var idInfo = new WhizbangIdInfo(
        TypeName: typeName,
        Namespace: targetNamespace,
        Source: DiscoverySource.Property,
        SuppressDuplicateWarning: suppressWarning
    );

    return (idInfo, null, null);
  }

  /// <summary>
  /// Extracts WhizbangIdInfo from a parameter with [WhizbangId] attribute.
  /// The type of the parameter becomes the generated ID type.
  /// </summary>
  private static (WhizbangIdInfo?, Location?, string?)? _extractParameterBasedId(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
    var parameterDecl = (ParameterSyntax)context.Node;

    if (context.SemanticModel.GetDeclaredSymbol(parameterDecl, ct) is not IParameterSymbol parameterSymbol) {
      return null;
    }

    // Check for [WhizbangId] attribute
    var whizbangIdAttr = parameterSymbol.GetAttributes().FirstOrDefault(a =>
        a.AttributeClass?.Name == "WhizbangIdAttribute" ||
        a.AttributeClass?.Name == "WhizbangId" ||
        a.AttributeClass?.ToDisplayString() == WHIZBANGID_ATTRIBUTE ||
        a.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == $"global::{WHIZBANGID_ATTRIBUTE}");

    if (whizbangIdAttr is null) {
      return null;
    }

    // Extract the parameter type name (e.g., "ProductId" from "[WhizbangId] ProductId Id")
    var typeName = parameterSymbol.Type.Name;

    // Extract namespace - either from attribute or containing type's namespace
    var containingMethod = parameterSymbol.ContainingSymbol as IMethodSymbol;
    var containingType = containingMethod?.ContainingType;
    string targetNamespace = containingType?.ContainingNamespace?.ToDisplayString() ?? "Global";

    // Check for Namespace property in attribute
    var namespaceArg = whizbangIdAttr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Namespace");
    if (namespaceArg.Value.Value is string customNamespace && !string.IsNullOrWhiteSpace(customNamespace)) {
      targetNamespace = customNamespace;
    }

    // Check for constructor argument with namespace
    if (whizbangIdAttr.ConstructorArguments.Length > 0 &&
        whizbangIdAttr.ConstructorArguments[0].Value is string constructorNamespace &&
        !string.IsNullOrWhiteSpace(constructorNamespace)) {
      targetNamespace = constructorNamespace;
    }

    // Extract SuppressDuplicateWarning property
    var suppressWarning = false;
    var suppressArg = whizbangIdAttr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "SuppressDuplicateWarning");
    if (suppressArg.Value.Value is bool suppress) {
      suppressWarning = suppress;
    }

    var idInfo = new WhizbangIdInfo(
        TypeName: typeName,
        Namespace: targetNamespace,
        Source: DiscoverySource.Parameter,
        SuppressDuplicateWarning: suppressWarning
    );

    return (idInfo, null, null);
  }

  /// <summary>
  /// Generates value object code for all discovered WhizbangIds and emits diagnostics.
  /// Handles deduplication and collision detection.
  /// </summary>
  private static void _generateWhizbangIds(
      SourceProductionContext context,
      ImmutableArray<(WhizbangIdInfo?, Location?, string?)?> results) {

    if (results.IsEmpty) {
      return;
    }

    // Separate valid IDs from errors and report error diagnostics
    var validIds = _separateValidIdsFromErrors(context, results);

    if (validIds.Count == 0) {
      return;
    }

    // Deduplicate and detect/report collisions
    var (deduplicated, collidingTypeNames) = _deduplicateAndDetectCollisions(context, validIds);

    // Generate code for each deduplicated ID
    foreach (var id in deduplicated) {
      // Use namespace-qualified hint name if this type has collisions
      var hintNamePrefix = collidingTypeNames.Contains(id.TypeName)
          ? $"{id.Namespace.Replace(".", "")}."  // e.g., "MyAppDomain.ProductId.g.cs"
          : "";

      // Generate value object
      var valueObjectCode = _generateValueObject(id);
      context.AddSource($"{hintNamePrefix}{id.TypeName}.g.cs", valueObjectCode);

      // Generate JSON converter
      var converterCode = _generateJsonConverter(id);
      context.AddSource($"{hintNamePrefix}{id.TypeName}JsonConverter.g.cs", converterCode);

      // Generate factory for DI scenarios
      var factoryCode = _generateFactory(id);
      context.AddSource($"{hintNamePrefix}{id.TypeName}Factory.g.cs", factoryCode);

      // Generate provider class
      var providerCode = _generateProvider(id);
      context.AddSource($"{hintNamePrefix}{id.TypeName}Provider.g.cs", providerCode);
    }

    // Generate registration class (one per assembly)
    if (deduplicated.Count > 0) {
      _generateProviderRegistration(context, deduplicated);
    }
  }

  /// <summary>
  /// Separates valid WhizbangId info from errors and reports error diagnostics.
  /// Returns list of valid IDs that can proceed to code generation.
  /// </summary>
  private static List<WhizbangIdInfo> _separateValidIdsFromErrors(
      SourceProductionContext context,
      ImmutableArray<(WhizbangIdInfo?, Location?, string?)?> results) {

    var validIds = new List<WhizbangIdInfo>();
    var errors = new List<(Location, string)>();

    // Separate valid IDs from errors
    foreach (var result in results) {
      if (result is null) {
        continue;
      }

      var (idInfo, errorLocation, errorTypeName) = result.Value;

      if (idInfo is null && errorLocation is not null && errorTypeName is not null) {
        errors.Add((errorLocation, errorTypeName));
      } else if (idInfo is not null) {
        validIds.Add(idInfo);
      }
    }

    // Emit error diagnostics
    foreach (var (location, typeName) in errors) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.WhizbangIdMustBePartial,
          location,
          typeName
      ));
    }

    return validIds;
  }

  /// <summary>
  /// Deduplicates WhizbangIds by fully qualified name and detects/reports collisions.
  /// Returns deduplicated list and set of colliding type names (for hint name qualification).
  /// </summary>
  private static (List<WhizbangIdInfo> Deduplicated, HashSet<string> CollidingTypeNames) _deduplicateAndDetectCollisions(
      SourceProductionContext context,
      List<WhizbangIdInfo> validIds) {

    // Group by fully qualified name to deduplicate
    var deduplicated = validIds
        .GroupBy(id => id.FullyQualifiedName)
        .Select(group => group.First())  // Take first occurrence
        .ToList();

    // Detect collisions (same type name in different namespaces)
    var collisionGroups = deduplicated
        .GroupBy(id => id.TypeName)
        .Where(group => group.Count() > 1)
        .ToList();

    // Build set of types that have collisions (need namespace-qualified hint names)
    var collidingTypeNames = new HashSet<string>();
    foreach (var collisionGroup in collisionGroups) {
      collidingTypeNames.Add(collisionGroup.Key);
    }

    // Emit collision warnings
    foreach (var collisionGroup in collisionGroups) {
      _reportCollisionWarnings(context, collisionGroup.ToList());
    }

    return (deduplicated, collidingTypeNames);
  }

  /// <summary>
  /// Reports collision warnings for WhizbangIds with the same type name in different namespaces.
  /// Uses nested loops to report all pairwise collisions (only once per pair).
  /// </summary>
  private static void _reportCollisionWarnings(
      SourceProductionContext context,
      List<WhizbangIdInfo> collidingIds) {

    for (int i = 0; i < collidingIds.Count; i++) {
      for (int j = i + 1; j < collidingIds.Count; j++) {
        var id1 = collidingIds[i];
        var id2 = collidingIds[j];

        // Emit warning unless suppressed by either ID
        if (!id1.SuppressDuplicateWarning && !id2.SuppressDuplicateWarning) {
          context.ReportDiagnostic(Diagnostic.Create(
              DiagnosticDescriptors.WhizbangIdDuplicateName,
              Location.None,
              id1.TypeName,
              id1.Namespace,
              id2.Namespace
          ));
        }
      }
    }
  }

  /// <summary>
  /// Generates the complete value object implementation for a WhizbangId.
  /// </summary>
  private static string _generateValueObject(WhizbangIdInfo id) {
    var sb = new StringBuilder();

    // Header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Generators.WhizbangIdGenerator");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("using System;");
    sb.AppendLine();

    sb.AppendLine($"namespace {id.Namespace};");
    sb.AppendLine();

    // Struct declaration
    sb.AppendLine($"public readonly partial struct {id.TypeName} : IEquatable<{id.TypeName}>, IComparable<{id.TypeName}> {{");

    // Private backing field
    sb.AppendLine("  private readonly Guid _value;");
    sb.AppendLine();

    // Value property
    sb.AppendLine("  /// <summary>Gets the underlying Guid value.</summary>");
    sb.AppendLine("  public Guid Value => _value;");
    sb.AppendLine();

    // Public constructor for EF Core compatibility
    sb.AppendLine("  /// <summary>Creates an instance from a Guid value. Public for EF Core compatibility.</summary>");
    sb.AppendLine($"  public {id.TypeName}(Guid value) => _value = value;");
    sb.AppendLine();

    // From factory method
    sb.AppendLine("  /// <summary>Creates an instance from a Guid value.</summary>");
    sb.AppendLine($"  public static {id.TypeName} From(Guid value) => new(value);");
    sb.AppendLine();

    // New factory method using configurable provider
    sb.AppendLine("  /// <summary>Creates a new instance using the configured WhizbangIdProvider.</summary>");
    sb.AppendLine($"  public static {id.TypeName} New() => new(global::Whizbang.Core.WhizbangIdProvider.NewGuid());");
    sb.AppendLine();

    // Equality members
    sb.AppendLine("  /// <summary>Determines whether two instances are equal.</summary>");
    sb.AppendLine($"  public bool Equals({id.TypeName} other) => _value.Equals(other._value);");
    sb.AppendLine();

    sb.AppendLine("  /// <summary>Determines whether this instance equals the specified object.</summary>");
    sb.AppendLine("  public override bool Equals(object? obj) => obj is " + id.TypeName + " other && Equals(other);");
    sb.AppendLine();

    sb.AppendLine("  /// <summary>Returns the hash code for this instance.</summary>");
    sb.AppendLine("  public override int GetHashCode() => _value.GetHashCode();");
    sb.AppendLine();

    // Comparison
    sb.AppendLine("  /// <summary>Compares this instance to another.</summary>");
    sb.AppendLine($"  public int CompareTo({id.TypeName} other) => _value.CompareTo(other._value);");
    sb.AppendLine();

    // Operators
    sb.AppendLine("  /// <summary>Equality operator.</summary>");
    sb.AppendLine($"  public static bool operator ==({id.TypeName} left, {id.TypeName} right) => left.Equals(right);");
    sb.AppendLine();

    sb.AppendLine("  /// <summary>Inequality operator.</summary>");
    sb.AppendLine($"  public static bool operator !=({id.TypeName} left, {id.TypeName} right) => !left.Equals(right);");
    sb.AppendLine();

    sb.AppendLine("  /// <summary>Less than operator.</summary>");
    sb.AppendLine($"  public static bool operator <({id.TypeName} left, {id.TypeName} right) => left.CompareTo(right) < 0;");
    sb.AppendLine();

    sb.AppendLine("  /// <summary>Less than or equal operator.</summary>");
    sb.AppendLine($"  public static bool operator <=({id.TypeName} left, {id.TypeName} right) => left.CompareTo(right) <= 0;");
    sb.AppendLine();

    sb.AppendLine("  /// <summary>Greater than operator.</summary>");
    sb.AppendLine($"  public static bool operator >({id.TypeName} left, {id.TypeName} right) => left.CompareTo(right) > 0;");
    sb.AppendLine();

    sb.AppendLine("  /// <summary>Greater than or equal operator.</summary>");
    sb.AppendLine($"  public static bool operator >=({id.TypeName} left, {id.TypeName} right) => left.CompareTo(right) >= 0;");
    sb.AppendLine();

    // ToString
    sb.AppendLine("  /// <summary>Returns the string representation of the underlying Guid.</summary>");
    sb.AppendLine("  public override string ToString() => _value.ToString();");
    sb.AppendLine();

    // Implicit conversion to Guid
    sb.AppendLine("  /// <summary>Implicitly converts to Guid.</summary>");
    sb.AppendLine($"  public static implicit operator Guid({id.TypeName} id) => id._value;");
    sb.AppendLine();

    // Explicit conversion from Guid
    sb.AppendLine("  /// <summary>Explicitly converts from Guid.</summary>");
    sb.AppendLine($"  public static explicit operator {id.TypeName}(Guid value) => new(value);");
    sb.AppendLine();

    // Parse method for string deserialization
    sb.AppendLine("  /// <summary>Parses a string representation of a Guid into this ID type.</summary>");
    sb.AppendLine($"  public static {id.TypeName} Parse(string value) => From(Guid.Parse(value));");
    sb.AppendLine();

    // CreateProvider method
    sb.AppendLine("  /// <summary>");
    sb.AppendLine($"  /// Creates a strongly-typed provider for {id.TypeName} instances.");
    sb.AppendLine("  /// Useful for direct instantiation without DI.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine($"  public static global::Whizbang.Core.IWhizbangIdProvider<{id.TypeName}> CreateProvider(");
    sb.AppendLine("      global::Whizbang.Core.IWhizbangIdProvider baseProvider) {");
    sb.AppendLine($"    return new {id.TypeName}Provider(baseProvider);");
    sb.AppendLine("  }");

    sb.AppendLine("}");

    return sb.ToString();
  }

  /// <summary>
  /// Generates a System.Text.Json converter for the WhizbangId.
  /// </summary>
  private static string _generateJsonConverter(WhizbangIdInfo id) {
    var sb = new StringBuilder();

    // Header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Generators.WhizbangIdGenerator");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("using System;");
    sb.AppendLine("using System.Text.Json;");
    sb.AppendLine("using System.Text.Json.Serialization;");
    sb.AppendLine("using Medo;");
    sb.AppendLine();

    sb.AppendLine($"namespace {id.Namespace};");
    sb.AppendLine();

    // Converter class
    sb.AppendLine($"/// <summary>");
    sb.AppendLine($"/// AOT-compatible JSON converter for {id.TypeName}.");
    sb.AppendLine($"/// Serializes {id.TypeName} using Medo.Uuid7 format for time-ordered UUIDs.");
    sb.AppendLine($"/// </summary>");
    sb.AppendLine($"public sealed class {id.TypeName}JsonConverter : JsonConverter<{id.TypeName}> {{");

    // Read method
    sb.AppendLine($"  public override {id.TypeName} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {{");
    sb.AppendLine("    var uuid7String = reader.GetString()!;");
    sb.AppendLine("    var uuid7 = Uuid7.Parse(uuid7String);");
    sb.AppendLine($"    return {id.TypeName}.From(uuid7.ToGuid());");
    sb.AppendLine("  }");
    sb.AppendLine();

    // Write method
    sb.AppendLine($"  public override void Write(Utf8JsonWriter writer, {id.TypeName} value, JsonSerializerOptions options) {{");
    sb.AppendLine("    var uuid7 = new Uuid7(value.Value);");
    sb.AppendLine("    writer.WriteStringValue(uuid7.ToString());");
    sb.AppendLine("  }");

    sb.AppendLine("}");

    return sb.ToString();
  }

  /// <summary>
  /// Generates a factory class for creating WhizbangId instances through dependency injection.
  /// </summary>
  private static string _generateFactory(WhizbangIdInfo id) {
    var sb = new StringBuilder();

    // Header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Generators.WhizbangIdGenerator");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine($"namespace {id.Namespace};");
    sb.AppendLine();

    // Factory class
    sb.AppendLine($"/// <summary>");
    sb.AppendLine($"/// Factory for creating {id.TypeName} instances through dependency injection.");
    sb.AppendLine($"/// Implements <see cref=\"global::Whizbang.Core.IWhizbangIdFactory{{T}}\" /> for {id.TypeName}.");
    sb.AppendLine($"/// </summary>");
    sb.AppendLine($"public sealed class {id.TypeName}Factory : global::Whizbang.Core.IWhizbangIdFactory<{id.TypeName}> {{");

    // Create method
    sb.AppendLine($"  /// <summary>");
    sb.AppendLine($"  /// Creates a new {id.TypeName} instance using the configured WhizbangIdProvider.");
    sb.AppendLine($"  /// </summary>");
    sb.AppendLine($"  /// <returns>A new {id.TypeName} instance.</returns>");
    sb.AppendLine($"  public {id.TypeName} Create() => {id.TypeName}.New();");

    sb.AppendLine("}");

    return sb.ToString();
  }

  /// <summary>
  /// Generates a strongly-typed provider for the WhizbangId.
  /// </summary>
  private static string _generateProvider(WhizbangIdInfo id) {
    var assembly = typeof(WhizbangIdGenerator).Assembly;
    var template = TemplateUtilities.GetEmbeddedTemplate(assembly, "WhizbangIdProviderTemplate.cs");

    // Replace namespace
    template = template.Replace("__NAMESPACE__", id.Namespace);

    // Replace type name
    template = template.Replace("__TYPE_NAME__", id.TypeName);

    // Replace header region
    template = TemplateUtilities.ReplaceHeaderRegion(assembly, template);

    return template;
  }

  /// <summary>
  /// Generates WhizbangIdProviderRegistration class for the assembly.
  /// </summary>
  private static void _generateProviderRegistration(
      SourceProductionContext context,
      List<WhizbangIdInfo> ids) {

    var assembly = typeof(WhizbangIdGenerator).Assembly;
    var template = TemplateUtilities.GetEmbeddedTemplate(
      assembly,
      "WhizbangIdProviderRegistrationTemplate.cs"
    );

    // Determine namespace (use first ID's namespace for the Generated sub-namespace)
    var firstNamespace = ids[0].Namespace;
    template = template.Replace("__NAMESPACE__", firstNamespace);

    // Replace header
    template = TemplateUtilities.ReplaceHeaderRegion(assembly, template);

    // Generate factory registrations
    var factoryRegistrations = new StringBuilder();
    foreach (var id in ids) {
      factoryRegistrations.AppendLine(
        $"    global::Whizbang.Core.WhizbangIdProviderRegistry.RegisterFactory<{id.FullyQualifiedName}>(" +
        $"baseProvider => new {id.TypeName}Provider(baseProvider));"
      );
    }
    template = TemplateUtilities.ReplaceRegion(
      template,
      "FACTORY_REGISTRATIONS",
      factoryRegistrations.ToString()
    );

    // Generate DI registrations
    var diRegistrations = new StringBuilder();
    foreach (var id in ids) {
      diRegistrations.AppendLine(
        $"    services.AddSingleton<global::Whizbang.Core.IWhizbangIdProvider<{id.FullyQualifiedName}>>(" +
        $"sp => new {id.TypeName}Provider(sp.GetRequiredService<global::Whizbang.Core.IWhizbangIdProvider>()));"
      );
    }
    template = TemplateUtilities.ReplaceRegion(
      template,
      "DI_REGISTRATIONS",
      diRegistrations.ToString()
    );

    context.AddSource("WhizbangIdProviderRegistration.g.cs", template);
  }

  /// <summary>
  /// Generates WhizbangIdJsonContext with custom converters for all discovered WhizbangId types.
  /// Always generates the context (even if empty) to ensure consistent availability across all projects.
  /// Uses assembly-specific namespace to avoid conflicts when multiple assemblies use Whizbang.
  /// Generates BOTH non-nullable AND nullable type resolvers for each WhizbangId.
  /// </summary>
  private static void _generateWhizbangIdJsonContext(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<(WhizbangIdInfo?, Location?, string?)?> results) {

    // Extract only valid IDs (filter out errors and nulls)
    var validIds = results.IsDefaultOrEmpty
        ? new List<WhizbangIdInfo>()
        : [.. results
            .Where(r => r.HasValue && r.Value.Item1 is not null)
            .Select(r => r!.Value.Item1!)];

    // Deduplicate by fully qualified name
    var deduplicated = validIds.Count == 0
        ? new List<WhizbangIdInfo>()
        : [.. validIds
            .GroupBy(id => id.FullyQualifiedName)
            .Select(group => group.First())];

    // Determine namespace from assembly name
    var assemblyName = compilation.AssemblyName ?? "Whizbang.Core";
    var namespaceName = $"{assemblyName}.Generated";

    // Load template
    var assembly = typeof(WhizbangIdGenerator).Assembly;
    var template = TemplateUtilities.GetEmbeddedTemplate(assembly, "WhizbangIdJsonContextTemplate.cs");

    // Replace namespace placeholder
    template = template.Replace("__NAMESPACE__", namespaceName);

    // Replace HEADER region with timestamp
    template = TemplateUtilities.ReplaceHeaderRegion(assembly, template);

    // Generate type checks for each WhizbangId
    var typeChecks = new StringBuilder();

    if (deduplicated.Count == 0) {
      // No WhizbangId types discovered - return null for all types
      typeChecks.AppendLine("// No WhizbangId types discovered in this assembly");
      typeChecks.AppendLine("// Return null to let next resolver in chain handle the type");
    } else {
      // Load snippets
      var nonNullableSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          "WhizbangIdSnippets.cs",
          "WHIZBANGID_TYPE_CHECK");

      var nullableSnippet = TemplateUtilities.ExtractSnippet(
          assembly,
          "WhizbangIdSnippets.cs",
          "WHIZBANGID_NULLABLE_TYPE_CHECK");

      typeChecks.AppendLine("// Check for discovered WhizbangId types");
      foreach (var id in deduplicated) {
        // Non-nullable type check
        var nonNullableCheck = nonNullableSnippet
            .Replace("__FULLY_QUALIFIED_NAME__", id.FullyQualifiedName)
            .Replace("__TYPE_NAME__", id.TypeName);
        typeChecks.AppendLine(nonNullableCheck);
        typeChecks.AppendLine();

        // Nullable type check
        var nullableCheck = nullableSnippet
            .Replace("__FULLY_QUALIFIED_NAME__", id.FullyQualifiedName)
            .Replace("__TYPE_NAME__", id.TypeName);
        typeChecks.AppendLine(nullableCheck);
        typeChecks.AppendLine();
      }
    }

    // Replace TYPE_CHECKS region with generated code
    template = TemplateUtilities.ReplaceRegion(template, "TYPE_CHECKS", typeChecks.ToString());

    // ALWAYS generate this context, even if empty, to ensure it's available for reference
    context.AddSource("WhizbangIdJsonContext.g.cs", template);

    // Generate module initializer to register JSON converters
    if (deduplicated.Count > 0) {
      _generateWhizbangIdConverterInitializer(context, namespaceName, deduplicated);
    }
  }

  /// <summary>
  /// Generates module initializer that registers WhizbangId JSON converters with JsonContextRegistry.
  /// Module initializers run before Main() to ensure converters are available for serialization.
  /// </summary>
  private static void _generateWhizbangIdConverterInitializer(
      SourceProductionContext context,
      string namespaceName,
      List<WhizbangIdInfo> ids) {

    var sb = new StringBuilder();

    // Header
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("// Generated by Whizbang.Generators.WhizbangIdGenerator");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();

    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using Whizbang.Core.Serialization;");
    sb.AppendLine();

    sb.AppendLine($"namespace {namespaceName};");
    sb.AppendLine();

    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Module initializer to register WhizbangId JSON converters with JsonContextRegistry.");
    sb.AppendLine("/// Runs before Main() to ensure converters are available for serialization.");
    sb.AppendLine("/// Public to allow explicit initialization in test assemblies where module initializers may not run.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public static class WhizbangIdConverterInitializer {");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  public static void Initialize() {");

    // Register JSON context
    sb.AppendLine("    // Register JSON context for WhizbangId types");
    sb.AppendLine("    JsonContextRegistry.RegisterContext(WhizbangIdJsonContext.Default);");
    sb.AppendLine();

    // Register individual converters
    sb.AppendLine("    // Register individual converters for runtime resolution");
    sb.AppendLine("    // This allows InfrastructureJsonContext's TryGetTypeInfoForRuntimeCustomConverter");
    sb.AppendLine("    // to find them when deserializing MessageHop properties (MessageId?, CorrelationId?).");
    foreach (var id in ids) {
      sb.AppendLine($"    JsonContextRegistry.RegisterConverter(new {id.FullyQualifiedName}JsonConverter());");
    }

    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("WhizbangIdConverterInitializer.g.cs", sb.ToString());
  }
}
