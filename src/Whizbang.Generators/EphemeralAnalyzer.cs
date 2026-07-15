using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer for the ephemeral virality rules (WHIZ130-139). Ephemeral is <em>viral</em>: a
/// perspective fed by an ephemeral event is authoritative state, not a rebuildable cache. Nobody in the
/// industry <em>enforces</em> that — Whizbang does, safe-by-construction at build time. Each diagnostic
/// is an ordinary <see cref="DiagnosticAnalyzer"/> diagnostic, so it surfaces in both the IDE (squiggle)
/// and the compiler build (<c>dotnet build</c>).
/// </summary>
/// <remarks>This file lands WHIZ130 (mixed-mode Apply). WHIZ131-134 follow.</remarks>
/// <docs>fundamentals/events/ephemeral-events</docs>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class EphemeralAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Ephemeral";

  // IPerspectiveBase<TModel, TEvent1, ...> is the unified marker extended by IPerspectiveFor and
  // IPerspectiveWithActionsFor; the events are the type arguments from index 1 on.
  private const string PERSPECTIVE_BASE = "Whizbang.Core.Perspectives.IPerspectiveBase";
  private const string DANGEROUS_MIX_OPT_IN = "DangerouslyAllowMixedEphemeralAndSourcedEventsAttribute";

  /// <summary>
  /// WHIZ130: Warning — a perspective whose <c>Apply</c> methods span both modes (a viral ephemeral
  /// event AND a normal Sourced event). It cannot be both authoritative state and a rebuildable cache.
  /// A Warning (not error): the right first signal for an opt-in feature, backstopped by the runtime
  /// guard; escalate via <c>.editorconfig</c> / <c>TreatWarningsAsErrors</c>.
  /// </summary>
  public static readonly DiagnosticDescriptor MixedModePerspective = new(
    id: "WHIZ130",
    title: "Perspective mixes ephemeral and Sourced Apply methods",
    messageFormat: "Perspective '{0}' applies both ephemeral event(s) ({1}) and Sourced event(s) ({2}). "
      + "A perspective cannot be both authoritative state (ephemeral) and a rebuildable cache (Sourced) — "
      + "split it, or make the stream homogeneous.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "Ephemeral is viral: a perspective fed by an ephemeral event is authoritative state that "
      + "is never rebuilt from a log. Mixing a Sourced event into the same perspective is a contradiction.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MixedModePerspective];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolAction(_analyzeType, SymbolKind.NamedType);
  }

  private static void _analyzeType(SymbolAnalysisContext context) {
    if (context.Symbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Class) {
      return;
    }

    var events = _appliedEventTypes(symbol);
    if (events.Count == 0) {
      return;   // not a perspective
    }

    var ephemeral = new List<string>();
    var sourced = new List<string>();
    foreach (var evt in events) {
      (EphemeralResolver.IsEphemeral(evt) ? ephemeral : sourced).Add(evt.Name);
    }

    if (ephemeral.Count > 0 && sourced.Count > 0 && !_hasDangerousMixOptIn(symbol)) {
      context.ReportDiagnostic(Diagnostic.Create(
        MixedModePerspective,
        symbol.Locations.FirstOrDefault() ?? Location.None,
        symbol.Name,
        string.Join(", ", ephemeral),
        string.Join(", ", sourced)));
    }
  }

  /// <summary>
  /// True when the perspective carries <c>[DangerouslyAllowMixedEphemeralAndSourcedEvents]</c> — the
  /// developer's explicit, in-code acknowledgement of the mixed-mode path. Matched by simple name so it
  /// works even when the source compilation can't fully bind the attribute.
  /// </summary>
  private static bool _hasDangerousMixOptIn(INamedTypeSymbol perspective) =>
    perspective.GetAttributes().Any(a =>
      a.AttributeClass?.Name == DANGEROUS_MIX_OPT_IN
      || a.AttributeClass?.Name == "DangerouslyAllowMixedEphemeralAndSourcedEvents");

  /// <summary>
  /// The distinct event types a perspective applies — the type arguments (from index 1) of every
  /// <c>IPerspectiveBase&lt;TModel, TEvent…&gt;</c> it implements (single interface with many events, or
  /// many single-event interfaces; the unified base covers both <c>IPerspectiveFor</c> and
  /// <c>IPerspectiveWithActionsFor</c>).
  /// </summary>
  private static List<INamedTypeSymbol> _appliedEventTypes(INamedTypeSymbol perspective) {
    var events = new List<INamedTypeSymbol>();
    var seen = new HashSet<string>(System.StringComparer.Ordinal);
    foreach (var iface in perspective.AllInterfaces) {
      var def = iface.OriginalDefinition.ToDisplayString();
      if (!def.StartsWith(PERSPECTIVE_BASE + "<TModel, TEvent", System.StringComparison.Ordinal)
          || iface.TypeArguments.Length < 2) {
        continue;
      }
      foreach (var arg in iface.TypeArguments.Skip(1)) {
        if (arg is INamedTypeSymbol evt && seen.Add(evt.ToDisplayString())) {
          events.Add(evt);
        }
      }
    }
    return events;
  }
}
