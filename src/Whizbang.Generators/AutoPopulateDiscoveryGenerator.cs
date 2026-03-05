using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Source generator that discovers properties with auto-populate attributes
/// and generates an IAutoPopulateRegistry for AOT-compatible property population.
/// </summary>
/// <docs>attributes/auto-populate</docs>
/// <tests>Whizbang.Generators.Tests/AutoPopulateDiscoveryGeneratorTests.cs</tests>
[Generator]
public class AutoPopulateDiscoveryGenerator : IIncrementalGenerator {
  // Attribute full names for discovery
  private const string POPULATE_TIMESTAMP_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateTimestampAttribute";
  private const string POPULATE_FROM_CONTEXT_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromContextAttribute";
  private const string POPULATE_FROM_SERVICE_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromServiceAttribute";
  private const string POPULATE_FROM_IDENTIFIER_ATTRIBUTE = "Whizbang.Core.Attributes.PopulateFromIdentifierAttribute";

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

    // Get all properties including inherited ones
    var properties = _getAllProperties(typeSymbol);

    foreach (var property in properties) {
      foreach (var attribute in property.GetAttributes()) {
        var attributeName = attribute.AttributeClass?.ToDisplayString();
        if (attributeName is null) {
          continue;
        }

        AutoPopulateInfo? info = attributeName switch {
          POPULATE_TIMESTAMP_ATTRIBUTE => _extractTimestampInfo(typeFullName, property, attribute),
          POPULATE_FROM_CONTEXT_ATTRIBUTE => _extractContextInfo(typeFullName, property, attribute),
          POPULATE_FROM_SERVICE_ATTRIBUTE => _extractServiceInfo(typeFullName, property, attribute),
          POPULATE_FROM_IDENTIFIER_ATTRIBUTE => _extractIdentifierInfo(typeFullName, property, attribute),
          _ => null
        };

        if (info is not null) {
          yield return info;
        }
      }
    }
  }

  private static IEnumerable<IPropertySymbol> _getAllProperties(INamedTypeSymbol typeSymbol) {
    // Get properties from this type and all base types
    var current = typeSymbol;
    while (current is not null) {
      foreach (var member in current.GetMembers().OfType<IPropertySymbol>()) {
        if (member.DeclaredAccessibility == Accessibility.Public && !member.IsStatic) {
          yield return member;
        }
      }
      current = current.BaseType;
    }
  }

  private static AutoPopulateInfo? _extractTimestampInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute) {

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
        PopulateKind: "Timestamp",
        SpecificKind: $"TimestampKind.{kindName}"
    );
  }

  private static AutoPopulateInfo? _extractContextInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute) {

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
        PopulateKind: "Context",
        SpecificKind: $"ContextKind.{kindName}"
    );
  }

  private static AutoPopulateInfo? _extractServiceInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute) {

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
        PopulateKind: "Service",
        SpecificKind: $"ServiceKind.{kindName}"
    );
  }

  private static AutoPopulateInfo? _extractIdentifierInfo(
      string typeFullName,
      IPropertySymbol property,
      AttributeData attribute) {

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
        PopulateKind: "Identifier",
        SpecificKind: $"IdentifierKind.{kindName}"
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
    sb.AppendLine($"    new AutoPopulateRegistration {{");
    sb.AppendLine($"      MessageType = typeof({info.TypeFullName}),");
    sb.AppendLine($"      PropertyName = \"{info.PropertyName}\",");
    sb.AppendLine($"      PropertyType = typeof({info.PropertyTypeFullName}),");
    sb.AppendLine($"      PopulateKind = PopulateKind.{info.PopulateKind},");

    // Add the specific kind based on PopulateKind
    var specificKindProperty = info.PopulateKind switch {
      "Timestamp" => "TimestampKind",
      "Context" => "ContextKind",
      "Service" => "ServiceKind",
      "Identifier" => "IdentifierKind",
      _ => null
    };

    if (specificKindProperty is not null) {
      sb.AppendLine($"      {specificKindProperty} = {info.SpecificKind},");
    }

    sb.AppendLine($"    }},");
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
    string SpecificKind
);
