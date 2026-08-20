using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer for the minted-composite construction rule (WHIZ150, first of the minting
/// block WHIZ150-159). Minted composite types (<c>RedeliveryComposite</c>,
/// <c>CoalescedEventsComposite</c>, <c>AuditEventsComposite</c>, and any consumer subclass of
/// <c>CompositeEventBase</c>) are meant to be constructed by the mint — inside a
/// <c>CompositeMintRequest&lt;T&gt;.BuildComposite</c> builder (reached via
/// <c>IEventMint.Composites</c>) or a coalesce binding's <c>CompositeFactory</c> — so the
/// group-key/count/byte splitting invariants are enforced at mint time. Direct construction
/// elsewhere bypasses every one of them: a hand-built composite can mix destinations, blow the
/// broker message-size ceiling, or exceed the receiver's expansion cap.
/// </summary>
/// <remarks>
/// Warning (not error) — the right first signal for a guidance rule, same idiom as
/// <see cref="EphemeralAnalyzer"/>'s WHIZ130; escalate via <c>.editorconfig</c> /
/// <c>TreatWarningsAsErrors</c>. Exemptions: code inside the <c>Whizbang.Core.Minting</c>
/// namespace (the mint's own internals), the <c>Whizbang.Core</c> assembly (the framework's
/// registered producers ARE the factory path), and lambdas assigned to the sanctioned builder
/// seams. Test projects suppress the id via <c>NoWarn</c> (the established WHIZ110/WHIZ111
/// idiom) — test fixtures construct composites directly by design.
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/MintedCompositeConstructionAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class MintedCompositeConstructionAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Minting";
  private const string COMPOSITE_EVENT_BASE = "Whizbang.Core.Minting.CompositeEventBase";
  private const string MINTING_NAMESPACE_PREFIX = "Whizbang.Core.Minting";
  private const string CORE_ASSEMBLY = "Whizbang.Core";
  private const string MINT_REQUEST_TYPE = "Whizbang.Core.Minting.CompositeMintRequest";
  private const string MINT_REQUEST_BUILDER = "BuildComposite";
  private const string COALESCE_POLICY_OPTIONS_TYPE = "Whizbang.Core.Tags.CoalescePolicyOptions";
  private const string COALESCE_POLICY_BUILDER = "CompositeFactory";

  /// <summary>
  /// WHIZ150: Warning — a minted composite type is constructed outside the mint's internals and
  /// outside a registered factory builder. Composites carry splitting invariants (one destination
  /// per composite, sender-side count cap, byte budget) that only the mint enforces.
  /// </summary>
  public static readonly DiagnosticDescriptor DirectMintedCompositeConstruction = new(
    id: "WHIZ150",
    title: "Minted composite constructed outside the mint",
    messageFormat: "'{0}' is a minted composite — construct it inside a composite-factory builder "
      + "(CompositeMintRequest<T>.BuildComposite via IEventMint.Composites, or a coalesce binding's "
      + "CompositeFactory) so the mint enforces grouping, count caps, and byte budgets",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "Minted composite families (CompositeEventBase subclasses) are wire-only bundles whose "
      + "splitting invariants — same group key ⇔ same destination, sender-side count cap, byte budget — "
      + "are enforced by ICompositeFactory at mint time. A directly-constructed composite bypasses all of "
      + "them: it can mix destinations, exceed broker message-size limits, or blow the receiver's "
      + "expansion cap.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [DirectMintedCompositeConstruction];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterOperationAction(_analyzeObjectCreation, OperationKind.ObjectCreation);
  }

  private static void _analyzeObjectCreation(OperationAnalysisContext context) {
    var creation = (IObjectCreationOperation)context.Operation;
    if (creation.Type is not INamedTypeSymbol constructedType || !_isMintedComposite(constructedType)) {
      return;
    }

    // Exemption 1 — the mint's own internals (namespace Whizbang.Core.Minting[.*]).
    if (_isInMintingNamespace(context.ContainingSymbol)) {
      return;
    }

    // Exemption 2 — the Whizbang.Core assembly: the framework's registered producers
    // (CoalesceShipWorker's default factory, RedeliveryPump's builder) ARE the factory path.
    if (string.Equals(context.ContainingSymbol.ContainingAssembly?.Name, CORE_ASSEMBLY, System.StringComparison.Ordinal)) {
      return;
    }

    // Exemption 3 — lexically inside a lambda assigned to a sanctioned builder seam:
    // CompositeMintRequest<T>.BuildComposite or CoalescePolicyOptions.CompositeFactory.
    if (_isInsideRegisteredBuilder(creation)) {
      return;
    }

    context.ReportDiagnostic(Diagnostic.Create(
      DirectMintedCompositeConstruction,
      creation.Syntax.GetLocation(),
      constructedType.Name));
  }

  /// <summary>True when the constructed type derives from <c>Whizbang.Core.Minting.CompositeEventBase</c>.</summary>
  private static bool _isMintedComposite(INamedTypeSymbol constructedType) {
    for (var baseType = constructedType.BaseType; baseType is not null; baseType = baseType.BaseType) {
      if (baseType.ToDisplayString() == COMPOSITE_EVENT_BASE) {
        return true;
      }
    }
    return false;
  }

  /// <summary>True when the constructing code lives in <c>Whizbang.Core.Minting</c> or a sub-namespace.</summary>
  private static bool _isInMintingNamespace(ISymbol containingSymbol) {
    var ns = containingSymbol.ContainingNamespace;
    if (ns is null) {
      return false;
    }
    var display = ns.ToDisplayString();
    return display == MINTING_NAMESPACE_PREFIX
      || display.StartsWith(MINTING_NAMESPACE_PREFIX + ".", System.StringComparison.Ordinal);
  }

  /// <summary>
  /// True when <paramref name="creation"/> sits inside an anonymous function that is assigned to a
  /// sanctioned builder seam — a <c>BuildComposite</c> property on
  /// <c>CompositeMintRequest&lt;T&gt;</c> or a <c>CompositeFactory</c> property on
  /// <c>CoalescePolicyOptions</c> (object-initializer or plain assignment). Every enclosing
  /// anonymous function in the ancestry is considered, so a helper lambda nested inside a builder
  /// stays exempt.
  /// </summary>
  private static bool _isInsideRegisteredBuilder(IOperation creation) {
    for (var operation = creation.Parent; operation is not null; operation = operation.Parent) {
      if (operation is not IAnonymousFunctionOperation) {
        continue;
      }
      // The lambda's conversion/delegate-creation wrapper sits between it and the assignment.
      for (var outer = operation.Parent; outer is not null; outer = outer.Parent) {
        if (outer is IAssignmentOperation assignment
            && assignment.Target is IPropertyReferenceOperation property
            && _isBuilderSeam(property.Property)) {
          return true;
        }
        // Stop climbing once past the expression that consumes the lambda.
        if (outer is IAnonymousFunctionOperation or IBlockOperation) {
          break;
        }
      }
    }
    return false;
  }

  /// <summary>True when <paramref name="property"/> is one of the sanctioned builder seams.</summary>
  private static bool _isBuilderSeam(IPropertySymbol property) {
    var containingType = property.ContainingType?.OriginalDefinition;
    if (containingType is null) {
      return false;
    }
    var containingDisplay = $"{containingType.ContainingNamespace.ToDisplayString()}.{containingType.Name}";
    return (property.Name == MINT_REQUEST_BUILDER && containingDisplay == MINT_REQUEST_TYPE)
        || (property.Name == COALESCE_POLICY_BUILDER && containingDisplay == COALESCE_POLICY_OPTIONS_TYPE);
  }
}
