using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer for the command-ownership rule (WHIZ151, topology arc phase 5): one SERVICE
/// owns a command type. Two DIFFERENT receptor classes registering inbox handlers for the same
/// COMMAND type is a modeling error — under per-namespace command inboxes both would claim the
/// command's inbox entity, and every command would be handled twice.
/// </summary>
/// <remarks>
/// <para>At analyzer scope a "service" is the compilation: cross-service visibility does not
/// exist at build time, so this rule enforces the intra-compilation invariant (duplicate
/// command inbox receptors across service registration units compiled together). The
/// cross-service case is covered by the census-mandated RUNTIME topology-drift check — the
/// provisioning path flags (health Degraded + structured error log) a second service's
/// subscription already existing on a command inbox entity it is about to own.</para>
/// <para>Exemptions: lifecycle-only receptors (<c>[FireAt(...)]</c> hooks are observers, not
/// inbox handlers), non-Command kinds (events fan out by design; System-kind framework
/// commands are broadcast traffic every service handles), abstract classes (not registration
/// units), and one class implementing multiple receptor surfaces for the same command (one
/// registration unit). Error severity per the arc owner's decision; N instances of the same
/// service are inherently fine — they compile once.</para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#ownership-drift</docs>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/CommandOwnershipAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class CommandOwnershipAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Routing";
  private const string IRECEPTOR_PREFIX = "global::Whizbang.Core.IReceptor";
  private const string ISYNCRECEPTOR_PREFIX = "global::Whizbang.Core.ISyncReceptor";

  /// <summary>
  /// WHIZ151: Error — the same command type has inbox receptors in two or more different
  /// classes within one compilation. One service owns a command type; a duplicate claim means
  /// every command on the namespace inbox would be handled more than once.
  /// </summary>
  public static readonly DiagnosticDescriptor DuplicateCommandOwnership = new(
    id: "WHIZ151",
    title: "Command type has more than one inbox receptor",
    messageFormat: "Command '{0}' has inbox receptors in multiple classes ({1}) — one service "
      + "owns a command type; move the command's handling into a single receptor (lifecycle "
      + "observers should use [FireAt(...)] stages instead)",
    category: CATEGORY,
    defaultSeverity: DiagnosticSeverity.Error,
    isEnabledByDefault: true,
    customTags: [WellKnownDiagnosticTags.CompilationEnd],
    description: "One service owns each command type (single-handler command semantics). Under "
      + "per-namespace command inboxes, every inbox receptor's service subscribes to the "
      + "command namespace's inbox entity — a second receptor class for the same command means "
      + "a second claim on that entity, and every command would be delivered and handled twice. "
      + "Cross-service duplicates are caught at runtime by the provisioning topology-drift "
      + "check; this rule catches the duplicate registration units visible at build time.");

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [DuplicateCommandOwnership];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterCompilationStartAction(static compilationContext => {
      // command type name -> claims (receptor class name, location), deduped per class.
      var claims = new ConcurrentDictionary<string, ConcurrentDictionary<string, Location>>();

      compilationContext.RegisterSymbolAction(
        symbolContext => _collectReceptorClaims(symbolContext, claims),
        SymbolKind.NamedType);

      compilationContext.RegisterCompilationEndAction(endContext => {
        foreach (var kvp in claims) {
          if (kvp.Value.Count < 2) {
            continue;
          }
          var receptorNames = string.Join(", ",
            kvp.Value.Keys.OrderBy(n => n, System.StringComparer.Ordinal).Select(n => $"'{n}'"));
          foreach (var claim in kvp.Value) {
            endContext.ReportDiagnostic(Diagnostic.Create(
              DuplicateCommandOwnership,
              claim.Value,
              kvp.Key,
              receptorNames));
          }
        }
      });
    });
  }

  private static void _collectReceptorClaims(
      SymbolAnalysisContext context,
      ConcurrentDictionary<string, ConcurrentDictionary<string, Location>> claims) {
    if (context.Symbol is not INamedTypeSymbol classSymbol
        || classSymbol.TypeKind != TypeKind.Class
        || classSymbol.IsAbstract) {
      return;
    }

    // Lifecycle-only receptors are observers, not inbox handlers — no ownership claim.
    var stages = CompileTimeMessageClassification.FireAtStagesOf(classSymbol);
    if (!CompileTimeMessageClassification.IsInboxHandler(stages)) {
      return;
    }

    foreach (var iface in classSymbol.AllInterfaces) {
      var display = iface.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      if ((!display.StartsWith(IRECEPTOR_PREFIX, System.StringComparison.Ordinal)
           && !display.StartsWith(ISYNCRECEPTOR_PREFIX, System.StringComparison.Ordinal))
          || iface.TypeArguments.Length == 0) {
        continue;
      }

      var messageType = iface.TypeArguments[0];
      // Ownership applies to Commands only: events fan out by design, System-kind framework
      // commands are broadcast traffic every service handles, queries are read-side.
      if (CompileTimeMessageClassification.DetectMessageKind(messageType) != "Command") {
        continue;
      }

      var commandName = messageType
        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        .Replace("global::", "");
      var receptorName = classSymbol
        .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        .Replace("global::", "");
      var location = classSymbol.Locations.FirstOrDefault() ?? Location.None;

      // Dedupe per receptor class: IReceptor<T> + ISyncReceptor<T> on ONE class is one
      // registration unit, not a duplicate claim.
      claims
        .GetOrAdd(commandName, static _ => new ConcurrentDictionary<string, Location>())
        .TryAdd(receptorName, location);
    }
  }
}
