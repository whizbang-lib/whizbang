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
/// <tests>Whizbang.Generators.Tests/AutoPopulateDiscoveryGeneratorTests.cs</tests>
[Generator]
public class AutoPopulateDiscoveryGenerator : IIncrementalGenerator {
  // Attribute full names for discovery
  private const string POPULATE_TIMESTAMP_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateTimestampAttribute";
  private const string POPULATE_FROM_CONTEXT_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromContextAttribute";
  private const string POPULATE_FROM_SERVICE_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromServiceAttribute";
  private const string POPULATE_FROM_IDENTIFIER_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromIdentifierAttribute";

  private const string POPULATE_KIND_TIMESTAMP = "Timestamp";
  private const string POPULATE_KIND_CONTEXT = "Context";
  private const string POPULATE_KIND_SERVICE = "Service";
  private const string POPULATE_KIND_IDENTIFIER = "Identifier";

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Discover types with auto-populate attributes on properties
    var populatedProperties = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is TypeDeclarationSyntax,
        transform: static (ctx, ct) => _extractAutoPopulateInfos(ctx, ct)
    ).SelectMany(static (infos, _) => infos);

    // Combine with assembly name to generate unique class names per assembly
    var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Unknown");

    // Generate registry with unique class name per assembly
    context.RegisterSourceOutput(
        populatedProperties.Collect().Combine(assemblyName),
        static (ctx, data) => _generateRegistry(ctx, data.Left!, data.Right)
    );

    // Generate populator (typed record population using 'with' expressions)
    context.RegisterSourceOutput(
        populatedProperties.Collect().Combine(assemblyName),
        static (ctx, data) => _generatePopulator(ctx, data.Left!, data.Right)
    );
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
          _ => null
        };

        if (info is not null) {
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
        IsRecord: isRecord
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
        IsRecord: isRecord
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
        IsRecord: isRecord
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
        IsRecord: isRecord
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

    // Add the specific kind based on PopulateKind
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

    sb.AppendLine("    },");
  }

  private static void _generatePopulator(
      SourceProductionContext context,
      ImmutableArray<AutoPopulateInfo?> infos,
      string assemblyName) {

    // Only generate populator for record types
    var recordInfos = infos.Where(i => i?.IsRecord == true).Select(i => i!).ToList();
    if (recordInfos.Count == 0) {
      return;
    }

    var sanitizedAssemblyName = _sanitizeIdentifier(assemblyName);
    var className = $"GeneratedAutoPopulatePopulator_{sanitizedAssemblyName}";
    var initializerClassName = $"AutoPopulatePopulatorInitializer_{sanitizedAssemblyName}";

    // Group by type for switch expressions
    var typeGroups = recordInfos.GroupBy(i => i.TypeFullName).ToList();

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
    sb.AppendLine("/// Auto-generated populator that sets auto-populate properties directly on message records.");
    sb.AppendLine("/// Uses record 'with' expressions for AOT-compatible, zero-reflection population.");
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
    _generateContextHelpers(sb);

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
    sb.AppendLine("    return message switch {");

    foreach (var group in typeGroups) {
      // SentAt phase: TimestampKind.SentAt + all Context + all Service + all Identifier
      var sentProperties = group.Where(i =>
          (i.PopulateKind == POPULATE_KIND_TIMESTAMP && i.SpecificKind == "TimestampKind.SentAt") ||
          i.PopulateKind == POPULATE_KIND_CONTEXT ||
          i.PopulateKind == POPULATE_KIND_SERVICE ||
          i.PopulateKind == POPULATE_KIND_IDENTIFIER
      ).ToList();

      if (sentProperties.Count == 0) {
        continue;
      }

      sb.AppendLine($"      {group.Key} m => m with {{");
      foreach (var prop in sentProperties) {
        var valueExpr = _getValueExpression(prop);
        sb.AppendLine($"        {prop.PropertyName} = {valueExpr},");
      }
      sb.AppendLine("      },");
    }

    sb.AppendLine("      _ => null");
    sb.AppendLine("    };");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static void _generateTimestampPhaseMethod(
      StringBuilder sb,
      List<IGrouping<string, AutoPopulateInfo>> typeGroups,
      string methodName,
      string timestampKindName) {

    sb.AppendLine($"  public object? {methodName}(object message, DateTimeOffset timestamp) {{");
    sb.AppendLine("    return message switch {");

    foreach (var group in typeGroups) {
      var timestampProperties = group.Where(i =>
          i.PopulateKind == POPULATE_KIND_TIMESTAMP && i.SpecificKind == $"TimestampKind.{timestampKindName}"
      ).ToList();

      if (timestampProperties.Count == 0) {
        continue;
      }
      sb.AppendLine($"      {group.Key} m => m with {{");
      foreach (var prop in timestampProperties) {
        sb.AppendLine($"        {prop.PropertyName} = timestamp,");
      }
      sb.AppendLine("      },");
    }

    sb.AppendLine("      _ => null");

    sb.AppendLine("    };");
    sb.AppendLine("  }");
    sb.AppendLine();
  }

  private static string _getValueExpression(AutoPopulateInfo info) {
    return info.PopulateKind switch {
      POPULATE_KIND_TIMESTAMP when info.SpecificKind == "TimestampKind.SentAt" => "hop.Timestamp",
      POPULATE_KIND_CONTEXT when info.SpecificKind == "ContextKind.UserId" => "_extractUserId(hop)",
      POPULATE_KIND_CONTEXT when info.SpecificKind == "ContextKind.TenantId" => "_extractTenantId(hop)",
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.ServiceName" => "hop.ServiceInstance.ServiceName",
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.InstanceId" => "hop.ServiceInstance.InstanceId",
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.HostName" => "hop.ServiceInstance.HostName",
      POPULATE_KIND_SERVICE when info.SpecificKind == "ServiceKind.ProcessId" => "hop.ServiceInstance.ProcessId",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.MessageId" => "messageId.Value",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.CorrelationId" => "hop.CorrelationId?.Value.Value",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.CausationId" => "hop.CausationId?.Value.Value",
      POPULATE_KIND_IDENTIFIER when info.SpecificKind == "IdentifierKind.StreamId" => "hop.StreamId",
      _ => "default"
    };
  }

  private static void _generateContextHelpers(StringBuilder sb) {
    sb.AppendLine("  private static string? _extractUserId(MessageHop hop) {");
    sb.AppendLine("    return _extractScopeValue(hop, \"UserId\");");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  private static string? _extractTenantId(MessageHop hop) {");
    sb.AppendLine("    return _extractScopeValue(hop, \"TenantId\");");
    sb.AppendLine("  }");
    sb.AppendLine();
    sb.AppendLine("  private static string? _extractScopeValue(MessageHop hop, string propertyName) {");
    sb.AppendLine("    if (hop.Scope?.Values == null) {");
    sb.AppendLine("      return null;");
    sb.AppendLine("    }");
    sb.AppendLine();
    sb.AppendLine("    // Look for the Scope key which contains UserId/TenantId");
    sb.AppendLine("    foreach (var kvp in hop.Scope.Values) {");
    sb.AppendLine("      if (kvp.Value.ValueKind == JsonValueKind.Object) {");
    sb.AppendLine("        if (kvp.Value.TryGetProperty(propertyName, out var propValue) && propValue.ValueKind == JsonValueKind.String) {");
    sb.AppendLine("          return propValue.GetString();");
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
    bool IsRecord
);
