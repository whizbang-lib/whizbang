using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators;

/// <summary>
/// Roslyn analyzer for stream deletion groups (WHIZ140-142). Groups control retention COHERENCE —
/// a stream evicted from one member leaves the others — so their common failure modes are all
/// silences: the forgotten member whose rows quietly linger (drift), the group nothing ever
/// triggers (inert), and the bridge with nothing to cross into (meaningless). Each check makes the
/// silence loud at build time.
/// </summary>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Generators.Tests/StreamGroupAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class StreamGroupAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Retention";
  private const string PERSPECTIVE_BASE = "Whizbang.Core.Perspectives.IPerspectiveBase";

  /// <summary>
  /// WHIZ140: Warning — a perspective applies event types that also feed a stream-group member,
  /// but joins no group and carries no <c>[StreamGroupIsolated]</c>. Its rows will linger after
  /// siblings evict — the exact half-dead-stream shape groups exist to kill. The associations are
  /// the drift ORACLE; the attribute stays the explicit opt-in authority.
  /// </summary>
  public static readonly DiagnosticDescriptor GroupDrift = new(
    id: "WHIZ140",
    title: "Perspective shares stream event types with a group but joins none",
    messageFormat: "Perspective '{0}' applies event type(s) also feeding stream-group member(s) ({1}), "
      + "but joins no group — its rows will linger after siblings evict. Join the group, or mark "
      + "[StreamGroupIsolated] to state the exclusion is deliberate.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "Explicit group keys can drift: a new perspective over the same streams silently "
      + "reintroduces lingering rows. The shared event types are the oracle that catches it.",
    customTags: [WellKnownDiagnosticTags.CompilationEnd]);

  /// <summary>
  /// WHIZ141: Warning — no announcing member of the group carries a row evictor
  /// (<c>[RowTtl]</c>/<c>[RowCap]</c>). The group controls togetherness, not whether: something
  /// must still evict first, or the cascade never fires.
  /// </summary>
  public static readonly DiagnosticDescriptor InertGroup = new(
    id: "WHIZ141",
    title: "Stream group has no announcing member with a row evictor",
    messageFormat: "Stream group '{0}' has no announcing member with [RowTtl] or [RowCap] — nothing "
      + "ever triggers its cascade. Give at least one announcing member a real evictor.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "A cascade-only group with no trigger is inert; every member should also keep its "
      + "own backstop retention.",
    customTags: [WellKnownDiagnosticTags.CompilationEnd]);

  /// <summary>
  /// WHIZ142: Warning — <c>Bridge</c> on a perspective's ONLY membership. Bridging re-announces a
  /// RECEIVED eviction into the member's OTHER groups; with one membership there is nothing to
  /// cross into.
  /// </summary>
  public static readonly DiagnosticDescriptor MeaninglessBridge = new(
    id: "WHIZ142",
    title: "Bridge on a sole stream-group membership",
    messageFormat: "Perspective '{0}' sets Bridge on its only [StreamGroup] membership. Bridging "
      + "crosses between a member's groups; there is nothing to cross into.",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true,
    description: "Bridge governs whether an eviction received through one group re-announces into "
      + "the member's other groups; it is meaningless with a single membership.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [GroupDrift, InertGroup, MeaninglessBridge];

  private sealed record PerspectiveGroupInfo(
    string Name,
    Location Location,
    ImmutableArray<(string Key, bool Announce, bool Bridge)> Memberships,
    bool HasEvictor,
    bool Isolated,
    ImmutableArray<string> EventTypes);

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(start => {
      var collected = new ConcurrentBag<PerspectiveGroupInfo>();
      start.RegisterSymbolAction(ctx => _collect(ctx, collected), SymbolKind.NamedType);
      start.RegisterCompilationEndAction(ctx => _evaluate(ctx, collected));
    });
  }

  private static void _collect(SymbolAnalysisContext context, ConcurrentBag<PerspectiveGroupInfo> collected) {
    if (context.Symbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Class) {
      return;
    }
    var eventTypes = _appliedEventTypes(symbol);
    var memberships = _memberships(symbol);
    if (eventTypes.Length == 0 && memberships.Length == 0) {
      return; // neither a perspective nor a group declarer — irrelevant.
    }

    var hasEvictor = symbol.GetAttributes().Any(static a =>
      a.AttributeClass?.Name is "RowTtlAttribute" or "RowTtl" or "RowCapAttribute" or "RowCap");
    var isolated = symbol.GetAttributes().Any(static a =>
      a.AttributeClass?.Name is "StreamGroupIsolatedAttribute" or "StreamGroupIsolated");

    // WHIZ142 is local: Bridge on the sole membership.
    if (memberships.Length == 1 && memberships[0].Bridge) {
      context.ReportDiagnostic(Diagnostic.Create(
        MeaninglessBridge, symbol.Locations.FirstOrDefault(), symbol.Name));
    }

    collected.Add(new PerspectiveGroupInfo(
      symbol.Name, symbol.Locations.FirstOrDefault() ?? Location.None,
      memberships, hasEvictor, isolated, eventTypes));
  }

  private static void _evaluate(CompilationAnalysisContext context, ConcurrentBag<PerspectiveGroupInfo> collected) {
    var all = collected.ToList();
    var grouped = all.Where(p => p.Memberships.Length > 0).ToList();
    if (grouped.Count == 0) {
      return;
    }

    // WHIZ141: per group key, at least one announcing member must carry an evictor.
    foreach (var group in grouped
        .SelectMany(p => p.Memberships.Select(m => (Perspective: p, Membership: m)))
        .GroupBy(x => x.Membership.Key)) {
      if (group.Any(x => x.Membership.Announce && x.Perspective.HasEvictor)) {
        continue;
      }
      foreach (var member in group) {
        context.ReportDiagnostic(Diagnostic.Create(
          InertGroup, member.Perspective.Location, group.Key));
      }
    }

    // WHIZ140: an ungrouped, non-isolated perspective sharing event types with grouped members.
    var groupedEventTypes = new Dictionary<string, List<string>>();
    foreach (var member in grouped) {
      foreach (var eventType in member.EventTypes) {
        if (!groupedEventTypes.TryGetValue(eventType, out var owners)) {
          groupedEventTypes[eventType] = owners = [];
        }
        owners.Add(member.Name);
      }
    }
    foreach (var perspective in all.Where(p => p.Memberships.Length == 0 && !p.Isolated && p.EventTypes.Length > 0)) {
      var sharers = perspective.EventTypes
        .Where(groupedEventTypes.ContainsKey)
        .SelectMany(t => groupedEventTypes[t])
        .Distinct()
        .OrderBy(n => n, System.StringComparer.Ordinal)
        .ToList();
      if (sharers.Count > 0) {
        context.ReportDiagnostic(Diagnostic.Create(
          GroupDrift, perspective.Location, perspective.Name, string.Join(", ", sharers)));
      }
    }
  }

  private static ImmutableArray<(string Key, bool Announce, bool Bridge)> _memberships(INamedTypeSymbol symbol) {
    var memberships = ImmutableArray.CreateBuilder<(string, bool, bool)>();
    foreach (var attribute in symbol.GetAttributes().Where(static a =>
        a.AttributeClass?.Name is "StreamGroupAttribute" or "StreamGroup")) {
      if (attribute.ConstructorArguments.Length == 0 ||
          attribute.ConstructorArguments[0].Value is not string key || key.Length == 0) {
        continue;
      }
      var announce = true;
      var bridge = false;
      foreach (var named in attribute.NamedArguments) {
        if (named.Key == "Announce" && named.Value.Value is bool announceValue) {
          announce = announceValue;
        } else if (named.Key == "Bridge" && named.Value.Value is bool bridgeValue) {
          bridge = bridgeValue;
        }
      }
      memberships.Add((key, announce, bridge));
    }
    return memberships.ToImmutable();
  }

  private static ImmutableArray<string> _appliedEventTypes(INamedTypeSymbol perspective) {
    var events = ImmutableArray.CreateBuilder<string>();
    foreach (var iface in perspective.AllInterfaces) {
      if (!iface.IsGenericType) {
        continue;
      }
      var definition = iface.ConstructedFrom.ToDisplayString();
      if (!definition.StartsWith(PERSPECTIVE_BASE, System.StringComparison.Ordinal)) {
        continue;
      }
      for (var i = 1; i < iface.TypeArguments.Length; i++) {
        events.Add(iface.TypeArguments[i].ToDisplayString());
      }
    }
    return events.ToImmutable();
  }
}
