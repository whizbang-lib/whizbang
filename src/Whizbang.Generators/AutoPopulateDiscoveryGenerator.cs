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
/// Source generator that discovers properties with auto-populate attributes
/// and generates an IAutoPopulateRegistry for AOT-compatible property population.
/// </summary>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Generators.Tests/AutoPopulateDiscoveryGeneratorTests.cs</tests>
[Generator]
public class AutoPopulateDiscoveryGenerator : IIncrementalGenerator {
  // Attribute full names for discovery
  private const string POPULATE_TIMESTAMP_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateTimestampAttribute";
  private const string POPULATE_FROM_CONTEXT_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromContextAttribute";
  private const string POPULATE_FROM_SERVICE_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromServiceAttribute";
  private const string POPULATE_FROM_IDENTIFIER_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromIdentifierAttribute";
  private const string POPULATE_FROM_HTTP_HEADER_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromHttpHeaderAttribute";

  private const string POPULATE_KIND_TIMESTAMP = "Timestamp";
  private const string POPULATE_KIND_CONTEXT = "Context";
  private const string POPULATE_KIND_SERVICE = "Service";
  private const string POPULATE_KIND_IDENTIFIER = "Identifier";
  private const string POPULATE_KIND_HEADER = "Header";

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with auto-populate attributes on properties
    var populatedProperties = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is TypeDeclarationSyntax,
        transform: static (ctx, ct) => _extractAutoPopulateInfos(ctx, ct)
    ).SelectMany(static (infos, _) => infos);

    // Combine with assembly name to generate unique class names per assembly
    var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Unknown");

    // Resolve the JSON field aliases the scope serializes with (e.g. UserId -> "u") from
    // PerspectiveScope's [JsonPropertyName] attributes, so the context extractor reads the real key
    // rather than a hard-coded one.
    var scopeAliases = context.CompilationProvider.Select(static (c, _) => _resolveScopeAliases(c));

    // Generate registry with unique class name per assembly
    context.RegisterSourceOutput(
        populatedProperties.Collect().Combine(assemblyName),
        static (ctx, data) => _generateRegistry(ctx, data.Left!, data.Right)
    );

    // Generate populator (record 'with' + class in-place population)
    context.RegisterSourceOutput(
        populatedProperties.Collect().Combine(assemblyName).Combine(scopeAliases),
        static (ctx, data) => _generatePopulator(ctx, data.Left.Left!, data.Left.Right, data.Right)
    );
  }

  /// <summary>
  /// Resolves the JSON field names the scope serializes with (from <c>PerspectiveScope</c>'s
  /// <c>[JsonPropertyName]</c> attributes) so context values are read by their real serialized key.
  /// </summary>
  private static ScopeAliases _resolveScopeAliases(Compilation compilation) {
    var scopeType = compilation.GetTypeByMetadataName("Whizbang.Core.Lenses.PerspectiveScope");
    return new ScopeAliases(
        _resolveJsonAlias(scopeType, "UserId", "UserId"),
        _resolveJsonAlias(scopeType, "TenantId", "TenantId"));
  }

  private static string _resolveJsonAlias(INamedTypeSymbol? type, string propertyName, string fallback) {
    var property = type?.GetMembers(propertyName).OfType<IPropertySymbol>().FirstOrDefault();
    if (property is null) {
      return fallback;
    }

    foreach (var attribute in property.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute"
          && attribute.ConstructorArguments.FirstOrDefault().Value is string alias
          && !string.IsNullOrEmpty(alias)) {
        return alias;
      }
    }

    return fallback;
  }

  private static int _computeInheritanceDepth(INamedTypeSymbol type) {
    var depth = 0;
    for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType) {
      depth++;
    }
    return depth;
  }

  /// <summary>
  /// Extracts AutoPopulateInfo for all auto-populate attributes on a type's properties.
  /// </summary>
  private static IEnumerable<AutoPopulateInfo> _extractAutoPopulateInfos(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var typeDecl = (TypeDeclarationSyntax)context.Node;
    var typeSymbol = context.SemanticModel.GetDeclaredSymbol(typeDecl, ct);

    if (typeSymbol is null) {
      yield break;
    }

    // Only process public types
    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      yield break;
    }

    var typeFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    var isRecord = typeSymbol.IsRecord;
    var inheritanceDepth = _computeInheritanceDepth(typeSymbol);

    // Get all properties including inherited ones (uses shared utility)
    var properties = typeSymbol.GetAllProperties();

    foreach (var property in properties) {
      foreach (var attribute in property.GetAttributes()) {
        var attributeName = attribute.AttributeClass?.ToDisplayString();
        if (attributeName is null) {
          continue;
        }

        AutoPopulateInfo? info = attributeName switch {
          POPULATE_TIMESTAMP_ATTRIBUTE => _extractTimestampInfo(typeFullName, property, attribute, isRecord),
          POPULATE_FROM_CONTEXT_ATTRIBUTE => _extractContextInfo(typeFullName, property, attribute, isRecord),
          POPULATE_FROM_SERVICE_ATTRIBUTE => _extractServiceInfo(typeFullName, property, attribute, isRecord),
          POPULATE_FROM_IDENTIFIER_ATTRIBUTE => _extractIdentifierInfo(typeFullName, property, attribute, isRecord),
          POPULATE_FROM_HTTP_HEADER_ATTRIBUTE => _extractHttpHeaderInfo(typeFullName, property, attribute, isRecord),
          _ => null
        };

        if (info is not null) {
          info = info with {
            IsInherited = !SymbolEqualityComparer.Default.Equals(property.ContainingType, typeSymbol),
            InheritanceDepth = inheritanceDepth
          };
          yield return info;
        }
      }
    }
  }

  private static AutoPopulateInfo? _extractTimestampInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute,
      bool isRecord) {

    var kindArg = attribute.ConstructorArguments.FirstOrDefault();
    if (kindArg.Value is null) {
      return null;
    }

    var kindValue = (int)kindArg.Value;
    var kindName = kindValue switch {
      0 => "SentAt",
      1 => "QueuedAt",
      2 => "DeliveredAt",
      _ => "SentAt"
    };

    return new AutoPopulateInfo(
        TypeFullName: typeFullName,
        PropertyName: property.Name,
        PropertyTypeFullName: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PopulateKind: POPULATE_KIND_TIMESTAMP,
        SpecificKind: $"TimestampKind.{kindName}",
        IsRecord: isRecord,
        IsSettable: property.SetMethod is not null && !property.SetMethod.IsInitOnly
    );
  }

  private static AutoPopulateInfo? _extractContextInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute,
      bool isRecord) {

    var kindArg = attribute.ConstructorArguments.FirstOrDefault();
    if (kindArg.Value is null) {
      return null;
    }

    var kindValue = (int)kindArg.Value;
    var kindName = kindValue switch {
      0 => "UserId",
      1 => "TenantId",
      _ => "UserId"
    };

    return new AutoPopulateInfo(
        TypeFullName: typeFullName,
        PropertyName: property.Name,
        PropertyTypeFullName: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PopulateKind: POPULATE_KIND_CONTEXT,
        SpecificKind: $"ContextKind.{kindName}",
        IsRecord: isRecord,
        IsSettable: property.SetMethod is not null && !property.SetMethod.IsInitOnly
    );
  }

  private static AutoPopulateInfo? _extractServiceInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute,
      bool isRecord) {

    var kindArg = attribute.ConstructorArguments.FirstOrDefault();
    if (kindArg.Value is null) {
      return null;
    }

    var kindValue = (int)kindArg.Value;
    var kindName = kindValue switch {
      0 => "ServiceName",
      1 => "InstanceId",
      2 => "HostName",
      3 => "ProcessId",
      _ => "ServiceName"
    };

    return new AutoPopulateInfo(
        TypeFullName: typeFullName,
        PropertyName: property.Name,
        PropertyTypeFullName: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PopulateKind: POPULATE_KIND_SERVICE,
        SpecificKind: $"ServiceKind.{kindName}",
        IsRecord: isRecord,
        IsSettable: property.SetMethod is not null && !property.SetMethod.IsInitOnly
    );
  }

  private static AutoPopulateInfo? _extractIdentifierInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute,
      bool isRecord) {

    var kindArg = attribute.ConstructorArguments.FirstOrDefault();
    if (kindArg.Value is null) {
      return null;
    }

    var kindValue = (int)kindArg.Value;
    var kindName = kindValue switch {
      0 => "MessageId",
      1 => "CorrelationId",
      2 => "CausationId",
      3 => "StreamId",
      _ => "MessageId"
    };

    return new AutoPopulateInfo(
        TypeFullName: typeFullName,
        PropertyName: property.Name,
        PropertyTypeFullName: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PopulateKind: POPULATE_KIND_IDENTIFIER,
        SpecificKind: $"IdentifierKind.{kindName}",
        IsRecord: isRecord,
        IsSettable: property.SetMethod is not null && !property.SetMethod.IsInitOnly
    );
  }

  private static AutoPopulateInfo? _extractHttpHeaderInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute,
      bool isRecord) {

    // The single ctor arg is the header / scope-extension key. SpecificKind carries it verbatim (it is
    // emitted as a string literal in the extractor call and as HttpHeaderName in the registration).
    if (attribute.ConstructorArguments.FirstOrDefault().Value is not string headerName
        || string.IsNullOrEmpty(headerName)) {
      return null;
    }

    return new AutoPopulateInfo(
        TypeFullName: typeFullName,
        PropertyName: property.Name,
        PropertyTypeFullName: property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        PopulateKind: POPULATE_KIND_HEADER,
        SpecificKind: headerName,
        IsRecord: isRecord,
        IsSettable: property.SetMethod is not null && !property.SetMethod.IsInitOnly
    );
  }

  private static void _generateRegistry(
      SourceProductionContext context,
      ImmutableArray<AutoPopulateInfo?> infos,
      string assemblyName) {

    var validInfos = infos.Where(i => i is not null).Select(i => i!).ToList();

    // Create unique class name based on assembly (sanitize for C# identifier)
    var sanitizedAssemblyName = _sanitizeIdentifier(assemblyName);
    var className = $"GeneratedAutoPopulateRegistry_{sanitizedAssemblyName}";
    var initializerClassName = $"AutoPopulateRegistryInitializer_{sanitizedAssemblyName}";

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Collections.Generic;");
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using Whizbang.Core.AutoPopulate;");
    sb.AppendLine("using Whizbang.Core.Attributes;");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-generated registry of message types with auto-populate attributes.");
    sb.AppendLine("/// Implements <see cref=\"IAutoPopulateRegistry\"/> for AOT-compatible property population.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("/// <remarks>");
    sb.AppendLine("/// This registry is automatically registered via [ModuleInitializer] before Main() runs.");
    sb.AppendLine("/// No manual registration is required.");
    sb.AppendLine("/// </remarks>");
    sb.AppendLine($"internal sealed class {className} : IAutoPopulateRegistry {{");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Singleton instance of the generated registry.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine($"  internal static readonly {className} Instance = new();");
    sb.AppendLine();
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// All registered auto-populate entries.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  private static readonly AutoPopulateRegistration[] _registrations = new AutoPopulateRegistration[] {");

    foreach (var info in validInfos) {
      _generateRegistration(sb, info);
    }

    sb.AppendLine("  };");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) {");
    sb.AppendLine("    foreach (var registration in _registrations) {");
    sb.AppendLine("      if (registration.MessageType == messageType) {");
    sb.AppendLine("        yield return registration;");
    sb.AppendLine("      }");
    sb.AppendLine("    }");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc />");
    sb.AppendLine("  public IEnumerable<AutoPopulateRegistration> GetAllRegistrations() {");
    sb.AppendLine("    return _registrations;");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-registers the generated auto-populate registry with the assembly registry.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine($"internal static class {initializerClassName} {{");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Module initializer that registers the auto-populate registry.");
    sb.AppendLine("  /// Called automatically before any code in the assembly runs.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine("    // Register with priority 100 (contracts assemblies are tried first)");
    sb.AppendLine($"    Whizbang.Core.AutoPopulate.AutoPopulateRegistry.Register({className}.Instance, priority: 100);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("AutoPopulateRegistry.g.cs", sb.ToString());
  }

  private static void _generateRegistration(StringBuilder sb, AutoPopulateInfo info) {
    sb.AppendLine("    new AutoPopulateRegistration {");
    sb.AppendLine($"      MessageType = typeof({info.TypeFullName}),");
    sb.AppendLine($"      PropertyName = \"{info.PropertyName}\",");
    sb.AppendLine($"      PropertyType = typeof({info.PropertyTypeFullName}),");
    sb.AppendLine($"      PopulateKind = PopulateKind.{info.PopulateKind},");

    // Add the specific kind based on PopulateKind. Header carries a string key (not an enum).
    if (info.PopulateKind == POPULATE_KIND_HEADER) {
      sb.AppendLine($"      HttpHeaderName = \"{info.SpecificKind}\",");
    } else {
      var specificKindProperty = info.PopulateKind switch {
        POPULATE_KIND_TIMESTAMP => "TimestampKind",
        POPULATE_KIND_CONTEXT => "ContextKind",
        POPULATE_KIND_SERVICE => "ServiceKind",
        POPULATE_KIND_IDENTIFIER => "IdentifierKind",
        _ => null
      };

      if (specificKindProperty is not null) {
        sb.AppendLine($"      {specificKindProperty} = {info.SpecificKind},");
      }
    }

    sb.AppendLine("    },");
  }

  private static void _generatePopulator(
      SourceProductionContext context,
      ImmutableArray<AutoPopulateInfo?> infos,
      string assemblyName,
      ScopeAliases scopeAliases) {

    // Generate a populator for records (immutable 'with' expression) AND for classes with settable
    // properties (in-place assignment). Class properties that are init-only/read-only cannot be assigned
    // outside an object initializer, so they are excluded (records reach init-only members via 'with').
    var populatableInfos = infos
        .Where(i => i is not null && (i.IsRecord || i.IsSettable))
        .Select(i => i!)
        .ToList();
    if (populatableInfos.Count == 0) {
      return;
    }

    var sanitizedAssemblyName = _sanitizeIdentifier(assemblyName);
    var className = $"GeneratedAutoPopulatePopulator_{sanitizedAssemblyName}";
    var initializerClassName = $"AutoPopulatePopulatorInitializer_{sanitizedAssemblyName}";

    // Group by type; each group is emitted as a record ('with') or class (in-place) switch arm.
    // A class that only INHERITS its populate props needs no case of its own — the declaring base type's case
    // populates it in place; emitting one anyway makes the derived case unreachable behind the base's (CS8120)
    // and bloats the output. Records keep a per-concrete-type case so the 'with' yields the concrete type.
    // Emit most-derived-first so a derived case is never shadowed by its base type's case.
    var typeGroups = populatableInfos.GroupBy(i => i.TypeFullName)
        .Where(g => g.First().IsRecord || g.Any(i => !i.IsInherited))
        .OrderByDescending(g => g.First().InheritanceDepth)
        .ToList();

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System;");
    sb.AppendLine("using System.Linq;");
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using System.Text.Json;");
    sb.AppendLine("using Whizbang.Core.AutoPopulate;");
    sb.AppendLine("using Whizbang.Core.Attributes;");
    sb.AppendLine("using Whizbang.Core.Observability;");
    sb.AppendLine("using Whizbang.Core.ValueObjects;");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-generated populator that sets auto-populate properties on message records and classes.");
    sb.AppendLine("/// Records use immutable 'with' expressions; classes are mutated in place — both AOT-compatible, zero-reflection.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine($"internal sealed class {className} : IAutoPopulatePopulator {{");
    sb.AppendLine($"  internal static readonly {className} Instance = new();");
    sb.AppendLine();

    // Generate TryPopulateSent
    _generateSentMethod(sb, typeGroups);

    // Generate TryPopulateQueued
    _generateTimestampPhaseMethod(sb, typeGroups, "TryPopulateQueued", "QueuedAt");

    // Generate TryPopulateDelivered
    _generateTimestampPhaseMethod(sb, typeGroups, "TryPopulateDelivered", "DeliveredAt");

    // Context helper methods
    _generateContextHelpers(sb, scopeAliases);

    sb.AppendLine("}");
    sb.AppendLine();

    // Module initializer
    sb.AppendLine($"internal static class {initializerClassName} {{");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("  internal static void Initialize() {");
    sb.AppendLine($"    Whizbang.Core.AutoPopulate.AutoPopulatePopulatorRegistry.Register({className}.Instance, priority: 100);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("AutoPopulatePopulator.g.cs", sb.ToString());
  }

  private static void _generateSentMethod(
      StringBuilder sb,
      List<IGrouping<string, AutoPopulateInfo>> typeGroups) {

    sb.AppendLine("  public object? TryPopulateSent(object message, MessageHop hop, MessageId messageId) {");
    sb.AppendLine("    switch (message) {");

    foreach (var group in typeGroups) {
      // SentAt phase: TimestampKind.SentAt + all Context + all Service + all Identifier
      var sentProperties = group.Where(i =>
          (i.PopulateKind == POPULATE_KIND_TIMESTAMP && i.SpecificKind == "TimestampKind.SentAt") ||
          i.PopulateKind == POPULATE_KIND_CONTEXT ||
          i.PopulateKind == POPULATE_KIND_SERVICE ||
          i.PopulateKind == POPULATE_KIND_IDENTIFIER ||
          i.PopulateKind == POPULATE_KIND_HEADER
      ).ToList();

      _emitPopulateArm(sb, group.Key, group.First().IsRecord, sentProperties, _getValueExpression);
    }

    sb.AppendLine("      default:");
    sb.AppendLine("        return null;");
    sb.AppendLine("    }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _generateTimestampPhaseMethod(
      StringBuilder sb,
      List<IGrouping<string, AutoPopulateInfo>> typeGroups,
      string methodName,
      string timestampKindName) {

    sb.AppendLine($"  public object? {methodName}(object message, DateTimeOffset timestamp) {{");
    sb.AppendLine("    switch (message) {");

    foreach (var group in typeGroups) {
      var timestampProperties = group.Where(i =>
          i.PopulateKind == POPULATE_KIND_TIMESTAMP && i.SpecificKind == $"TimestampKind.{timestampKindName}"
      ).ToList();

      _emitPopulateArm(sb, group.Key, group.First().IsRecord, timestampProperties, static _ => "timestamp");
    }

    sb.AppendLine("      default:");
    sb.AppendLine("        return null;");
    sb.AppendLine("    }");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  /// <summary>
  /// Emits one <c>switch</c> arm that populates <paramref name="props"/> on a message of type
  /// <paramref name="typeName"/>. Records are populated immutably via a <c>with</c> expression; classes are
  /// mutated in place and the same instance is returned. An arm with no properties to set is skipped, so the
  /// message falls through to the <c>default</c> (returns null → the registry leaves the message unchanged).
  /// </summary>
  private static void _emitPopulateArm(
      StringBuilder sb,
      string typeName,
      bool isRecord,
      List<AutoPopulateInfo> props,
      System.Func<AutoPopulateInfo, string> valueSelector) {

    if (props.Count == 0) {
      return;
    }

    sb.AppendLine($"      case {typeName} m:");
    if (isRecord) {
      sb.AppendLine("        return m with {");
      foreach (var prop in props) {
        sb.AppendLine($"          {prop.PropertyName} = {valueSelector(prop)},");
      }
      sb.AppendLine("        };");
    } else {
      foreach (var prop in props) {
        sb.AppendLine($"        m.{prop.PropertyName} = {valueSelector(prop)};");
      }
      sb.AppendLine("        return m;");
    }
  }

  private static string _getValueExpression(AutoPopulateInfo info) {
    // A WhizbangId's `.Value` IS the Guid (e.g. hop.CorrelationId?.Value is a Guid?), so the identifier
    // expressions use a single `.Value`. When the target property is a string (the standard-conforming
    // correlation-id type), append a null-safe `.ToString()` so the Guid source assigns to it.
    var isStringTarget = info.PropertyTypeFullName is "string" or "string?";
    return info.PopulateKind switch {
      POPULATE_KIND_TIMESTAMP when info.SpecificKind == "TimestampKind.SentAt" => "hop.Timestamp",
      // Context kinds FILL, never CLOBBER: an explicitly-stamped identity (e.g. an
      // anonymous-request endpoint attributing the action to a resolved account) must survive —
      // the ambient context on such a dispatch is null and would silently erase the attribution.
      // Dispatch-authority stamps (timestamps, service identity, correlation flow) below stay
      // unconditional: the hop is the source of truth for those.
      POPULATE_KIND_CONTEXT when info.SpecificKind == "ContextKind.UserId" =>
          _fillIfEmpty($"m.{info.PropertyName}", _coerceStringSource("_extractUserId(hop)", info), info),
      POPULATE_KIND_CONTEXT when info.SpecificKind == "ContextKind.TenantId" =>
          _fillIfEmpty($"m.{info.PropertyName}", _coerceStringSource("_extractTenantId(hop)", info), info),
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.ServiceName" => "hop.ServiceInstance.ServiceName",
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.InstanceId" =>
          isStringTarget ? "hop.ServiceInstance.InstanceId.ToString()" : "hop.ServiceInstance.InstanceId",
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.HostName" => "hop.ServiceInstance.HostName",
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.ProcessId" =>
          isStringTarget ? "hop.ServiceInstance.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)" : "hop.ServiceInstance.ProcessId",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.MessageId" =>
          isStringTarget ? "messageId.Value.ToString()" : "messageId.Value",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.CorrelationId" =>
          isStringTarget ? "hop.CorrelationId?.Value.ToString()" : "hop.CorrelationId?.Value",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.CausationId" =>
          isStringTarget ? "hop.CausationId?.Value.ToString()" : "hop.CausationId?.Value",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.StreamId" => "hop.StreamId",
      POPULATE_KIND_HEADER => $"_extractExtensionValue(hop, \"{info.SpecificKind}\")",
      _ => "default"
    };
  }

  /// <summary>
  /// Coerces a string-typed source expression (context values like UserId/TenantId, which live in scope as
  /// strings) to the target property type. A string target takes the value as-is; a Guid target parses it
  /// (e.g. a consumer's <c>Guid? AccountId</c> populated from the string UserId context).
  /// </summary>
  private static string _coerceStringSource(string stringExpr, AutoPopulateInfo info) {
    return info.PropertyTypeFullName switch {
      "global::System.Guid?" => $"_parseGuid({stringExpr})",
      "global::System.Guid" => $"(_parseGuid({stringExpr}) ?? System.Guid.Empty)",
      _ => stringExpr
    };
  }

  /// <summary>
  /// Wraps a populate source so it only applies when the property's CURRENT value is absent
  /// (null / Guid.Empty / null-or-empty string). Works in both populate arms — the class arm
  /// (<c>m.X = …</c>) and the record arm (<c>m with { X = … }</c>) — since <c>m</c> is the
  /// pre-populate instance in both.
  /// </summary>
  private static string _fillIfEmpty(string currentExpr, string sourceExpr, AutoPopulateInfo info) {
    return info.PropertyTypeFullName switch {
      "global::System.Guid?" => $"({currentExpr} ?? {sourceExpr})",
      "global::System.Guid" => $"({currentExpr} == System.Guid.Empty ? {sourceExpr} : {currentExpr})",
      "string" or "string?" => $"(string.IsNullOrEmpty({currentExpr}) ? {sourceExpr} : {currentExpr})",
      _ => sourceExpr
    };
  }

  private static void _generateContextHelpers(StringBuilder sb, ScopeAliases scopeAliases) {
    // The scope serializes with short JSON keys (e.g. "u"/"t"), resolved from PerspectiveScope's
    // [JsonPropertyName]. Try the resolved alias first, then the long property name as a fallback.
    sb.AppendLine("  private static string? _extractUserId(MessageHop hop) {");
    sb.AppendLine($"    return _extractScopeValue(hop, \"{scopeAliases.UserId}\", \"UserId\");");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  private static string? _extractTenantId(MessageHop hop) {");
    sb.AppendLine($"    return _extractScopeValue(hop, \"{scopeAliases.TenantId}\", \"TenantId\");");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  private static string? _extractScopeValue(MessageHop hop, params string[] aliases) {");
    sb.AppendLine("    if (hop.Scope?.Values == null) {");
    sb.AppendLine("      return null;");
    sb.AppendLine("    }");
    sb.AppendLine();
    sb.AppendLine("    // Look inside each scope object for the value under any of its known JSON aliases.");
    sb.AppendLine("    foreach (var kvp in hop.Scope.Values) {");
    sb.AppendLine("      if (kvp.Value.ValueKind == JsonValueKind.Object) {");
    sb.AppendLine("        foreach (var alias in aliases) {");
    sb.AppendLine("          if (kvp.Value.TryGetProperty(alias, out var propValue) && propValue.ValueKind == JsonValueKind.String) {");
    sb.AppendLine("            return propValue.GetString();");
    sb.AppendLine("          }");
    sb.AppendLine("        }");
    sb.AppendLine("      }");
    sb.AppendLine("    }");
    sb.AppendLine();
    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  private static System.Guid? _parseGuid(string? value) {");
    sb.AppendLine("    return System.Guid.TryParse(value, out var parsed) ? parsed : (System.Guid?)null;");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  private static string? _extractExtensionValue(MessageHop hop, string headerKey) {");
    sb.AppendLine("    if (hop.Scope?.Values == null) {");
    sb.AppendLine("      return null;");
    sb.AppendLine("    }");
    sb.AppendLine();
    sb.AppendLine("    // Header values ride the scope 'ex' extension list ([{\"k\":key,\"v\":value}]).");
    sb.AppendLine("    foreach (var kvp in hop.Scope.Values) {");
    sb.AppendLine("      if (kvp.Value.ValueKind == JsonValueKind.Object");
    sb.AppendLine("          && kvp.Value.TryGetProperty(\"ex\", out var ex) && ex.ValueKind == JsonValueKind.Array) {");
    sb.AppendLine("        foreach (var item in ex.EnumerateArray()) {");
    sb.AppendLine("          if (item.ValueKind == JsonValueKind.Object");
    sb.AppendLine("              && item.TryGetProperty(\"k\", out var k) && k.ValueKind == JsonValueKind.String");
    sb.AppendLine("              && string.Equals(k.GetString(), headerKey, StringComparison.OrdinalIgnoreCase)");
    sb.AppendLine("              && item.TryGetProperty(\"v\", out var v) && v.ValueKind == JsonValueKind.String) {");
    sb.AppendLine("            return v.GetString();");
    sb.AppendLine("          }");
    sb.AppendLine("        }");
    sb.AppendLine("      }");
    sb.AppendLine("    }");
    sb.AppendLine();
    sb.AppendLine("    return null;");
    sb.AppendLine("  }");
    sb.AppendLine();
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
/// Value type record for caching discovered auto-populate information.
/// </summary>
internal sealed record AutoPopulateInfo(
    string TypeFullName,
    string PropertyName,
    string PropertyTypeFullName,
    string PopulateKind,
    string SpecificKind,
    bool IsRecord,
    bool IsSettable
) {
  /// <summary>True when the property is inherited from a base type rather than declared on this type.</summary>
  public bool IsInherited { get; init; }

  /// <summary>Number of base types (used to order populator cases most-derived-first).</summary>
  public int InheritanceDepth { get; init; }
}

/// <summary>
/// The JSON field names the scope serializes context values with (resolved from PerspectiveScope's
/// <c>[JsonPropertyName]</c>). An equatable value type so it participates in incremental-generator caching.
/// </summary>
internal readonly record struct ScopeAliases(string UserId, string TenantId);
