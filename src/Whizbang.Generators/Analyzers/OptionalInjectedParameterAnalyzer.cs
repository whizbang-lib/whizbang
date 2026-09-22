using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports an optional injected constructor parameter where it is declared.
/// </summary>
/// <remarks>
/// <para>
/// An optional interface-typed parameter is the declaration that lets a construction site drop a
/// dependency without saying so. WHIZ500 catches the omission at the call site; this catches the
/// shape that makes omission possible, at the moment it is written, which is the cheapest point to
/// fix it. One stops today's surface causing harm, the other stops the surface growing.
/// </para>
/// <para>
/// The fix is to make the parameter required and register a default with <c>TryAdd</c>. Optionality
/// then lives in the registration, where it is explicit and where the container guarantees
/// something is always present, rather than in the constructor, where absence is invisible.
/// </para>
/// <para>
/// Reported as information deliberately. The existing surface runs to roughly a hundred and fifty
/// parameters, and a rule that turns an established codebase red on first build gets suppressed
/// globally, after which it catches nothing at all. Growth is held by a separate ratchet test; this
/// rule exists to put the reason in front of whoever is editing the constructor.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz501</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/OptionalInjectedParameterAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class OptionalInjectedParameterAnalyzer : DiagnosticAnalyzer {

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [DiagnosticDescriptors.OptionalInjectedParameter];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyze, SymbolKind.Method);
  }

  private static bool _isBclDataInterface(ITypeSymbol type) {
    // System.Collections and everything beneath it (Generic, Immutable, ...).
    for (var ns = type.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace) {
      if (ns.Name == "Collections" && ns.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true }) {
        return true;
      }
    }
    return false;
  }

  private static void _analyze(SymbolAnalysisContext context) {
    if (context.Symbol is not IMethodSymbol method || method.MethodKind != MethodKind.Constructor) {
      return;
    }
    if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) {
      return;
    }
    // A positional record is a data carrier, not a service. Its parameters are values the caller
    // supplies at the point of creation, so there is no container to register a default in and
    // "make it required" would only push a null literal to every construction site.
    if (method.ContainingType.IsRecord) {
      return;
    }

    foreach (var p in method.Parameters) {
      if (!p.IsOptional) {
        continue;
      }
      // Only interfaces are container-resolved services. A retry count or a name with a sensible
      // default is not a dependency, and flagging it would bury the real signal under noise.
      if (p.Type.TypeKind != TypeKind.Interface) {
        continue;
      }
      // Collection and comparison abstractions from the BCL, such as IReadOnlyList<T> or
      // IComparer<T>, describe a value the caller shapes, not a collaborator the container resolves,
      // so the rule's remedy, a TryAdd default, has no meaning for them.
      if (_isBclDataInterface(p.Type)) {
        continue;
      }

      var location = p.Locations.Length > 0 ? p.Locations[0] : method.ContainingType.Locations[0];
      context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.OptionalInjectedParameter,
        location,
        p.Name,
        method.ContainingType.Name));
    }
  }
}
