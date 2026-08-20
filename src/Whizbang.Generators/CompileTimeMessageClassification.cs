using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Compile-time message classification shared by <see cref="ReceptorRegistryQueryGenerator"/>
/// and the ownership analyzer (WHIZ151) — ONE mirror of
/// <c>Whizbang.Core.Routing.MessageKindDetector</c>'s priority rules and of the
/// inbox-handler-vs-lifecycle-hook determination, so the generator's registry and the
/// analyzer's enforcement can never disagree about what a receptor claims.
/// </summary>
internal static class CompileTimeMessageClassification {
  private const string FIREAT_ATTRIBUTE = "Whizbang.Core.Messaging.FireAtAttribute";
  private const string MESSAGE_KIND_ATTRIBUTE = "Whizbang.Core.Routing.MessageKindAttribute";
  private const string ICOMMAND_INTERFACE = "global::Whizbang.Core.ICommand";
  private const string IEVENT_INTERFACE = "global::Whizbang.Core.IEvent";
  private const string IQUERY_INTERFACE = "global::Whizbang.Core.IQuery";

  /// <summary>The framework system namespace subtree whose types classify as MessageKind.System.
  /// Mirror of <c>Whizbang.Core.Routing.MessageKindDetector</c>'s framework-system tier.</summary>
  private const string FRAMEWORK_SYSTEM_NAMESPACE = "Whizbang.Core.Commands.System";

  /// <summary>
  /// The message type's contract namespace, lowercase-invariant to match routing-key
  /// conventions (OwnDomains patterns, broker routing keys). Empty for global-namespace types.
  /// </summary>
  internal static string ContractNamespaceOf(ITypeSymbol messageType) {
    var ns = messageType.ContainingNamespace;
    return ns is null || ns.IsGlobalNamespace
      ? string.Empty
      : ns.ToDisplayString().ToLowerInvariant();
  }

  /// <summary>
  /// Compile-time mirror of <c>Whizbang.Core.Routing.MessageKindDetector</c>'s priority
  /// rules: [MessageKind] attribute, framework system namespace, marker interface,
  /// namespace convention, type-name suffix. Returns the MessageKind member NAME.
  /// </summary>
  internal static string DetectMessageKind(ITypeSymbol messageType) {
    // Priority 1: [MessageKind] attribute (explicit override)
    foreach (var attr in messageType.GetAttributes()) {
      if (attr.AttributeClass?.ToDisplayString() != MESSAGE_KIND_ATTRIBUTE
          || attr.ConstructorArguments.Length == 0) {
        continue;
      }
      var arg = attr.ConstructorArguments[0];
      if (arg.Type is INamedTypeSymbol enumType && arg.Value is int v) {
        var member = enumType.GetMembers().OfType<IFieldSymbol>()
          .FirstOrDefault(f => f.HasConstantValue && System.Convert.ToInt32(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) == v);
        if (member is not null) {
          return member.Name;
        }
      }
    }

    // Priority 2: framework system namespace subtree (outranks interfaces — framework
    // system commands implement ICommand yet are broadcast/run-control traffic)
    var ns = messageType.ContainingNamespace is { IsGlobalNamespace: false } containing
      ? containing.ToDisplayString()
      : string.Empty;
    if (ns == FRAMEWORK_SYSTEM_NAMESPACE
        || ns.StartsWith(FRAMEWORK_SYSTEM_NAMESPACE + ".", System.StringComparison.Ordinal)) {
      return "System";
    }

    // Priority 3: marker interfaces
    foreach (var iface in messageType.AllInterfaces) {
      var display = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      if (display == ICOMMAND_INTERFACE) {
        return "Command";
      }
      if (display == IEVENT_INTERFACE) {
        return "Event";
      }
      if (display == IQUERY_INTERFACE) {
        return "Query";
      }
    }

    // Priority 4: namespace convention segments
    foreach (var segment in ns.Split('.')) {
      if (string.Equals(segment, "Commands", System.StringComparison.OrdinalIgnoreCase)) {
        return "Command";
      }
      if (string.Equals(segment, "Events", System.StringComparison.OrdinalIgnoreCase)) {
        return "Event";
      }
      if (string.Equals(segment, "Queries", System.StringComparison.OrdinalIgnoreCase)) {
        return "Query";
      }
    }

    // Priority 5: type-name suffix
    var name = messageType.Name;
    if (name.EndsWith("Command", System.StringComparison.Ordinal)) {
      return "Command";
    }
    if (name.EndsWith("Query", System.StringComparison.Ordinal)) {
      return "Query";
    }
    if (name.EndsWith("Event", System.StringComparison.Ordinal)
        || name.EndsWith("Created", System.StringComparison.Ordinal)
        || name.EndsWith("Updated", System.StringComparison.Ordinal)
        || name.EndsWith("Deleted", System.StringComparison.Ordinal)) {
      return "Event";
    }

    return "Unknown";
  }

  /// <summary>
  /// Extracts the [FireAt] lifecycle stage NAMES declared on a receptor class (one entry per
  /// attribute).
  /// </summary>
  internal static ImmutableArray<string> FireAtStagesOf(INamedTypeSymbol receptorClass) {
    var stages = ImmutableArray.CreateBuilder<string>();
    foreach (var attr in receptorClass.GetAttributes()) {
      if (attr.AttributeClass?.ToDisplayString() != FIREAT_ATTRIBUTE) {
        continue;
      }
      if (attr.ConstructorArguments.Length == 0) {
        continue;
      }
      var arg = attr.ConstructorArguments[0];
      if (arg.Type is INamedTypeSymbol enumType
          && enumType.Name == "LifecycleStage"
          && arg.Value is int v) {
        var member = enumType.GetMembers().OfType<IFieldSymbol>()
          .FirstOrDefault(f => f.HasConstantValue && System.Convert.ToInt32(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) == v);
        if (member is not null) {
          stages.Add(member.Name);
        }
      }
    }
    return stages.ToImmutable();
  }

  /// <summary>
  /// A receptor with no [FireAt] is a direct inbox handler that fires at PostInboxDetached/
  /// PostInboxInline by default (transport path) or LocalImmediateDetached (local path).
  /// One with [FireAt(PreInboxInline)] is a lifecycle receptor at that stage.
  /// Receptors at PreInbox/PostInbox stages are NOT inbox handlers — they're lifecycle hooks.
  /// Receptors with no stages OR with stages unrelated to PreInbox/PostInbox count as inbox handlers.
  /// </summary>
  internal static bool IsInboxHandler(ImmutableArray<string> stages) {
    var hasOnlyLifecycleStages = stages.Length > 0
      && stages.All(s => s.StartsWith("PreInbox", System.StringComparison.Ordinal)
                      || s.StartsWith("PostInbox", System.StringComparison.Ordinal)
                      || s.StartsWith("PrePerspective", System.StringComparison.Ordinal)
                      || s.StartsWith("PostPerspective", System.StringComparison.Ordinal)
                      || s.StartsWith("PostAllPerspectives", System.StringComparison.Ordinal)
                      || s.StartsWith("PostLifecycle", System.StringComparison.Ordinal));
    return !hasOnlyLifecycleStages;
  }
}
