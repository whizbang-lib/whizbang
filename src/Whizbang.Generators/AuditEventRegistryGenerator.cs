// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Registers every type in the assembly that carries <c>[AuditEvent]</c>, directly or through a base class, with the
/// attribute's values, into <c>AuditEventRegistry</c> from a <c>[ModuleInitializer]</c>. Audit decisions read the
/// registry, so Whizbang.Core reads no attribute by reflection (#1580). A type generated code cannot name is reported
/// with WHIZ180 instead of being dropped silently.
/// </summary>
/// <docs>fundamentals/events/system-events#audit-decisions</docs>
/// <tests>tests/Whizbang.Generators.Tests/AuditEventRegistryGeneratorTests.cs</tests>
[Generator]
public class AuditEventRegistryGenerator : IIncrementalGenerator {
  private const string AUDIT_EVENT_ATTRIBUTE = "Whizbang.Core.Attributes.AuditEventAttribute";

  /// <summary>
  /// Syntax predicate: admits a type declaration with an attribute or a base list.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsAttributedOrDerivedTypeCandidate(SyntaxNode node) =>
      node is TypeDeclarationSyntax t && (t.AttributeLists.Any() || t.BaseList is not null);

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var audited = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsAttributedOrDerivedTypeCandidate(node),
        transform: static (ctx, ct) => _extract(ctx, ct)
    ).Where(static info => info is not null);

    context.RegisterSourceOutput(audited.Collect(), static (ctx, infos) => _generate(ctx, infos));
  }

  private static AuditedTypeInfo? _extract(GeneratorSyntaxContext context, System.Threading.CancellationToken cancellationToken) {
    if (context.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)context.Node, cancellationToken) is not INamedTypeSymbol symbol
        || (symbol.IsGenericType && symbol.TypeParameters.Length > 0)) {
      return null;
    }

    // The nearest declaration in the chain wins, as GetCustomAttributes(inherit: true) found it.
    AttributeData? attribute = null;
    for (var current = symbol; current is not null && attribute is null; current = current.BaseType) {
      attribute = current.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, AUDIT_EVENT_ATTRIBUTE));
    }
    if (attribute is null) {
      return null;
    }

    var name = TypeNameUtilities.FullyQualified(symbol);
    return _isReachable(symbol)
      ? new AuditedTypeInfo(name, _initializers(attribute), Unreachable: false)
      : new AuditedTypeInfo(name, "", Unreachable: true);
  }

  // Whether generated code in this assembly can name the type: it and every type containing it are public or internal.
  private static bool _isReachable(INamedTypeSymbol symbol) {
    for (var current = symbol; current is not null; current = current.ContainingType) {
      if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)) {
        return false;
      }
    }
    return true;
  }

  // The named arguments the audit decision reads, rendered as an object initializer.
  private static string _initializers(AttributeData attribute) {
    var parts = new System.Collections.Generic.List<string>();
    foreach (var argument in attribute.NamedArguments) {
      var value = argument.Value;
      switch (argument.Key) {
        case "Tag" or "Reason" when value.Value is string text:
          parts.Add($"{argument.Key} = \"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
          break;
        case "Level" when value.Value is not null:
          parts.Add($"Level = (global::Whizbang.Core.Audit.AuditLevel){Convert.ToInt32(value.Value, CultureInfo.InvariantCulture)}");
          break;
        case "Exclude" or "IncludeBody" when value.Value is bool flag:
          parts.Add($"{argument.Key} = {(flag ? "true" : "false")}");
          break;
      }
    }
    return parts.Count == 0 ? "" : $" {{ {string.Join(", ", parts)} }}";
  }

  private static void _generate(SourceProductionContext context, ImmutableArray<AuditedTypeInfo?> infos) {
    var all = infos.Where(static i => i is not null).Select(static i => i!).Distinct().ToList();
    foreach (var unreachable in all.Where(static i => i.Unreachable)) {
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.AuditEventTypeNotRegistrable, Location.None, unreachable.FullyQualifiedName));
    }

    var registrable = all.Where(static i => !i.Unreachable)
      .OrderBy(static i => i.FullyQualifiedName, StringComparer.Ordinal).ToList();
    if (registrable.Count == 0) {
      return;
    }

    var source = new StringBuilder();
    source.AppendLine("// <auto-generated/>");
    source.AppendLine("#nullable enable");
    source.AppendLine();
    source.AppendLine("namespace Whizbang.Generated;");
    source.AppendLine();
    source.AppendLine("/// <summary>Registers this assembly's [AuditEvent] settings into the AuditEventRegistry at load.</summary>");
    source.AppendLine("internal static class GeneratedAuditEventRegistrations {");
    source.AppendLine("  // CA2255: intentional ModuleInitializer for AOT-compatible cross-assembly registration");
    source.AppendLine("#pragma warning disable CA2255");
    source.AppendLine("  [global::System.Runtime.CompilerServices.ModuleInitializer]");
    source.AppendLine("#pragma warning restore CA2255");
    source.AppendLine("  internal static void Register() {");
    foreach (var info in registrable) {
      source.AppendLine(
        $"    global::Whizbang.Core.SystemEvents.AuditEventRegistry.Register(typeof({info.FullyQualifiedName}), new global::Whizbang.Core.Attributes.AuditEventAttribute{(info.Initializers.Length == 0 ? "()" : info.Initializers)});");
    }
    source.AppendLine("  }");
    source.AppendLine("}");

    context.AddSource("AuditEventRegistry.g.cs", source.ToString());
  }

  /// <summary>Cache value-type for one audited type (value equality for incremental caching).</summary>
  internal sealed record AuditedTypeInfo(string FullyQualifiedName, string Initializers, bool Unreachable);
}
