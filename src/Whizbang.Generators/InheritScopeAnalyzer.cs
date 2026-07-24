using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer that catches misuse of <c>[InheritScope]</c>. Without analyzers, the
/// attribute has silent failure modes — placed on a non-perspective type, the runtime
/// ignores it; declared with all-None flags, it's a no-op that's more confusing than
/// omitting the attribute. The analyzer surfaces both at compile time.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class InheritScopeAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Security";
  private const string INHERIT_SCOPE_ATTR = "Whizbang.Core.Lenses.InheritScopeAttribute";
  private const string PERSPECTIVE_INTERFACE_PREFIX = "Whizbang.Core.Perspectives.IPerspectiveFor";

  /// <summary>
  /// WHIZ300: Warning — <c>[InheritScope]</c> applied to a non-perspective type.
  /// The attribute is read at projection time; on a class that isn't a perspective
  /// participant it has no effect.
  /// </summary>
  public static readonly DiagnosticDescriptor InheritScopeOnNonPerspective = new(
    id: "WHIZ400",
    title: "[InheritScope] on a non-perspective type has no effect",
    messageFormat: "Class '{0}' is decorated with [InheritScope] but does not implement IPerspectiveFor<...>. The attribute will be ignored at runtime.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "[InheritScope] is read by the perspective projection runner. Apply it only to types that participate in projections via IPerspectiveFor<...>."
  );

  /// <summary>
  /// WHIZ301: Info — <c>[InheritScope(OnCreate = None, Always = None)]</c> is
  /// equivalent to no inheritance at all and is more confusing than omitting the attribute.
  /// </summary>
  public static readonly DiagnosticDescriptor InheritScopeAllNoneIsRedundant = new(
    id: "WHIZ401",
    title: "[InheritScope] with all-None flags is redundant",
    messageFormat: "[InheritScope(OnCreate = None, Always = None)] on '{0}' is equivalent to declaring no inheritance at all. Either omit the attribute or specify at least one inherited field.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Info,
    isEnabledByDefault: true,
    description: "When both OnCreate and Always are ScopeFields.None, the attribute conveys 'inherit nothing' — but the framework default already inherits Tenant only. If you intend tenant-only inheritance, omit the attribute. If you intend zero inheritance, add a comment explaining why."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [
    InheritScopeOnNonPerspective,
    InheritScopeAllNoneIsRedundant,
  ];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    if (context.Symbol is not INamedTypeSymbol symbol) {
      return;
    }
    if (symbol.TypeKind != TypeKind.Class) {
      return;
    }

    var attrs = symbol.GetAttributes();

    AttributeData? inheritScopeAttr = null;
    foreach (var attr in attrs) {
      var name = attr.AttributeClass?.Name;
      if (name == "InheritScopeAttribute" || name == "InheritScope") {
        inheritScopeAttr = attr;
        break;
      }
    }

    if (inheritScopeAttr is null) {
      return;
    }

    var location = symbol.Locations.FirstOrDefault() ?? Location.None;

    // WB-AUTH-010: applied to non-perspective type?
    if (!_implementsPerspectiveFor(symbol)) {
      context.ReportDiagnostic(Diagnostic.Create(
        InheritScopeOnNonPerspective,
        location,
        symbol.Name));
    }

    // WB-AUTH-011: both OnCreate and Always set to None?
    var onCreate = _readEnumProp(inheritScopeAttr, "OnCreate", defaultValue: 1L /* Tenant */);
    var always = _readEnumProp(inheritScopeAttr, "Always", defaultValue: 0L /* None */);
    if (onCreate == 0 && always == 0) {
      context.ReportDiagnostic(Diagnostic.Create(
        InheritScopeAllNoneIsRedundant,
        location,
        symbol.Name));
    }
  }

  private static bool _implementsPerspectiveFor(INamedTypeSymbol symbol) {
    foreach (var iface in symbol.AllInterfaces) {
      if (iface.ToDisplayString().StartsWith(PERSPECTIVE_INTERFACE_PREFIX, System.StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }

  private static long _readEnumProp(AttributeData attr, string name, long defaultValue) {
    foreach (var named in attr.NamedArguments) {
      if (named.Key == name && named.Value.Value is { } v) {
        return System.Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
      }
    }
    return defaultValue;
  }
}
