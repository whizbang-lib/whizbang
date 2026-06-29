using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Emits a static class <c>WhizbangReceptorRegistryQuery</c> that the receive boundary uses to
/// decide whether a message has any consumer registered in the compilation, and which lifecycle
/// stages have receptors. Compile-time-known via source generation. Zero runtime reflection in
/// the consumer code — the generated class is HashSet lookups against fully-qualified type-name strings.
/// </summary>
/// <remarks>
/// <para>Slice 1 of plans/pump-then-process.md. Independent of the existing
/// <see cref="ReceptorDiscoveryGenerator"/> + <see cref="PerspectiveDiscoveryGenerator"/> per the
/// "Multiple Independent Generators" pattern (cache isolation).</para>
///
/// <para>Three discovery pipelines feed into the generated lookup:</para>
/// <list type="bullet">
///   <item>Receptors with their lifecycle stages (from <c>[FireAt(LifecycleStage.X)]</c>) — gives both per-stage lookup and the inbox-handler set</item>
///   <item>Perspectives — gives event types that are consumed by projections even when no receptor is registered</item>
///   <item>Notification-tag attributes (<c>[NotificationTag]</c> / <c>[NotificationIdTag]</c>) — gives event types that drive UI tag notifications</item>
/// </list>
///
/// <para>The receive boundary uses <c>HasAnyConsumer(messageType)</c> to drop unsubscribed
/// messages immediately (no INSERT, no deserialize) and <c>HasReceptors(stage, messageType)</c>
/// to skip lifecycle deserialize when no receptors are registered for that stage+type.</para>
/// </remarks>
[Generator]
public class ReceptorRegistryQueryGenerator : IIncrementalGenerator {

  private const string IRECEPTOR_PREFIX = "global::Whizbang.Core.IReceptor";
  private const string ISYNCRECEPTOR_PREFIX = "global::Whizbang.Core.ISyncReceptor";
  private const string IPERSPECTIVE_PREFIX = "global::Whizbang.Core.Perspectives.IPerspectiveFor";
  private const string IPERSPECTIVE_WITH_ACTIONS_PREFIX = "global::Whizbang.Core.Perspectives.IPerspectiveWithActionsFor";
  private const string ICOMPOSITE_EVENT_INTERFACE = "global::Whizbang.Core.Messaging.ICompositeEvent";
  private const string ICOLLECTIVE_EVENT_INTERFACE = "global::Whizbang.Core.Messaging.ICollectiveEvent";
  private const string FIREAT_ATTRIBUTE = "Whizbang.Core.Messaging.FireAtAttribute";
  private const string NOTIFICATION_TAG_ATTRIBUTE = "Whizbang.Core.NotificationTagAttribute";
  private const string NOTIFICATION_ID_TAG_ATTRIBUTE = "Whizbang.Core.NotificationIdTagAttribute";

  /// <summary>The lifecycle stages we expose lookups for. Mirror of <c>Whizbang.Core.Messaging.LifecycleStage</c> values
  /// that the receive boundary cares about (PreInbox + PostInbox).</summary>
  private static readonly string[] _exposedStages = [
    "PreInboxDetached",
    "PreInboxInline",
    "PostInboxDetached",
    "PostInboxInline",
  ];

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var receptors = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractReceptorEntry(ctx, ct)
    ).Where(static info => info is not null);

    var perspectives = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractPerspectiveEntry(ctx, ct)
    ).Where(static info => info is not null);

    var taggedTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
          (node is ClassDeclarationSyntax c && c.AttributeLists.Count > 0)
          || (node is RecordDeclarationSyntax r && r.AttributeLists.Count > 0),
        transform: static (ctx, ct) => _extractTaggedMessageEntry(ctx, ct)
    ).Where(static name => name is not null);

    // Composite events are consumed by the dispatch-time fan-out seam (no receptor / perspective /
    // tag needed). They must register as consumers so the receive-boundary drop-gate keeps them
    // alive long enough to fan out. See plans/composite-events-turnkey.md, Phase A.
    var compositeTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
          node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 }
          || node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractCompositeEntry(ctx, ct)
    ).Where(static name => name is not null);

    // Collective events are consumed by the perspective worker's __collective__ sink (via a
    // [CollectiveApplyFor] handler), not by a receptor or perspective. Like composites, they must register
    // as consumers so the inbox/receive drop-gate keeps them alive long enough to be stored and routed to
    // the sink (migration 061). Without this the inbox row is discarded ("no consumer registered") before
    // storage and the collective apply never runs.
    var collectiveTypes = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) =>
          node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 }
          || node is RecordDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extractCollectiveEntry(ctx, ct)
    ).Where(static name => name is not null);

    var combined = receptors.Collect()
      .Combine(perspectives.Collect())
      .Combine(taggedTypes.Collect())
      .Combine(compositeTypes.Collect())
      .Combine(collectiveTypes.Collect());

    context.RegisterSourceOutput(combined, static (ctx, data) => {
      var receptorEntries = data.Left.Left.Left.Left;
      var perspectiveEntries = data.Left.Left.Left.Right;
      var taggedEntries = data.Left.Left.Right;
      var compositeEntries = data.Left.Right;
      var collectiveEntries = data.Right;
      _emitRegistryQuery(ctx, receptorEntries, perspectiveEntries, taggedEntries, compositeEntries, collectiveEntries);
    });
  }

  // ===== Discovery: receptors =====

  /// <summary>
  /// One entry per (receptor class × stage) — so a receptor with two FireAt attributes produces
  /// two entries. Receptors with no FireAt fall back to ImmediateDetached (which we don't expose
  /// in the gate; it's not Pre/PostInbox), but we still record them as inbox handlers.
  /// </summary>
  private sealed record ReceptorRegistryEntry(
      string MessageType,
      ImmutableArray<string> Stages,
      bool IsInboxHandler
  );

  private static ReceptorRegistryEntry? _extractReceptorEntry(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken ct) {
    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
    if (classSymbol is null || classSymbol.IsAbstract) {
      return null;
    }

    var receptorInterface = classSymbol.AllInterfaces.FirstOrDefault(i => {
      var s = i.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      return s.StartsWith(IRECEPTOR_PREFIX, System.StringComparison.Ordinal)
          || s.StartsWith(ISYNCRECEPTOR_PREFIX, System.StringComparison.Ordinal);
    });
    if (receptorInterface is null || receptorInterface.TypeArguments.Length == 0) {
      return null;
    }

    var messageType = receptorInterface.TypeArguments[0]
      .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
      .Replace("global::", "");

    var stages = ImmutableArray.CreateBuilder<string>();
    foreach (var attr in classSymbol.GetAttributes()) {
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

    // A receptor with no [FireAt] is a direct inbox handler that fires at PostInboxDetached/
    // PostInboxInline by default (transport path) or LocalImmediateDetached (local path).
    // One with [FireAt(PreInboxInline)] is a lifecycle receptor at that stage.
    // Receptors at PreInbox/PostInbox stages are NOT inbox handlers — they're lifecycle hooks.
    // Receptors with no stages OR with stages unrelated to PreInbox/PostInbox count as inbox handlers.
    var hasOnlyLifecycleStages = stages.Count > 0
      && stages.All(s => s.StartsWith("PreInbox", System.StringComparison.Ordinal)
                      || s.StartsWith("PostInbox", System.StringComparison.Ordinal)
                      || s.StartsWith("PrePerspective", System.StringComparison.Ordinal)
                      || s.StartsWith("PostPerspective", System.StringComparison.Ordinal)
                      || s.StartsWith("PostAllPerspectives", System.StringComparison.Ordinal)
                      || s.StartsWith("PostLifecycle", System.StringComparison.Ordinal));
    var isInboxHandler = !hasOnlyLifecycleStages;

    return new ReceptorRegistryEntry(messageType, stages.ToImmutable(), isInboxHandler);
  }

  // ===== Discovery: perspectives =====

  /// <summary>One entry per (perspective × event type). Perspectives consume events even when
  /// no receptor is registered for that type.</summary>
  private sealed record PerspectiveRegistryEntry(string EventType);

  private static PerspectiveRegistryEntry[]? _extractPerspectiveEntry(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken ct) {
    var classDeclaration = (ClassDeclarationSyntax)context.Node;
    var classSymbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) as INamedTypeSymbol;
    if (classSymbol is null || classSymbol.IsAbstract) {
      return null;
    }

    var entries = ImmutableArray.CreateBuilder<PerspectiveRegistryEntry>();
    foreach (var iface in classSymbol.AllInterfaces) {
      var prefix = iface.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
      if (!prefix.StartsWith(IPERSPECTIVE_PREFIX, System.StringComparison.Ordinal)
          && !prefix.StartsWith(IPERSPECTIVE_WITH_ACTIONS_PREFIX, System.StringComparison.Ordinal)) {
        continue;
      }
      // IPerspectiveFor<TModel, TEvent1, TEvent2, ...> — first type arg is the model, rest are events
      for (var i = 1; i < iface.TypeArguments.Length; i++) {
        var eventType = iface.TypeArguments[i]
          .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
          .Replace("global::", "");
        entries.Add(new PerspectiveRegistryEntry(eventType));
      }
    }
    return entries.Count == 0 ? null : entries.ToArray();
  }

  // ===== Discovery: tagged messages =====

  /// <summary>
  /// Type names of message types decorated with [NotificationTag] or [NotificationIdTag].
  /// These have a downstream consumer (the SignalR tagged-notification dispatcher) even without
  /// a receptor or perspective.
  /// </summary>
  private static string? _extractTaggedMessageEntry(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken ct) {
    var typeDecl = context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
    if (symbol is null || symbol.IsAbstract) {
      return null;
    }
    var hasTag = symbol.GetAttributes().Any(a => {
      var name = a.AttributeClass?.ToDisplayString();
      return name == NOTIFICATION_TAG_ATTRIBUTE || name == NOTIFICATION_ID_TAG_ATTRIBUTE;
    });
    if (!hasTag) {
      return null;
    }
    return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
  }

  // ===== Discovery: composite events =====

  /// <summary>
  /// Type names of concrete (non-abstract) message types implementing <c>ICompositeEvent</c>.
  /// A composite is consumed by the dispatch-time fan-out seam — it has no receptor / perspective /
  /// tag of its own, so without this it would be dropped at the receive-boundary drop-gate before
  /// it could fan out. The abstract <c>CompositeEventBase</c> itself is skipped (never dispatched).
  /// </summary>
  private static string? _extractCompositeEntry(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken ct) {
    var typeDecl = context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
    if (symbol is null || symbol.IsAbstract) {
      return null;
    }
    var implementsComposite = symbol.AllInterfaces.Any(i =>
      i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == ICOMPOSITE_EVENT_INTERFACE);
    if (!implementsComposite) {
      return null;
    }
    return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
  }

  // ===== Discovery: collective events =====

  /// <summary>
  /// Type names of concrete (non-abstract) message types implementing <c>ICollectiveEvent</c>.
  /// A collective event is consumed by the perspective worker's <c>__collective__</c> sink (a
  /// <c>[CollectiveApplyFor]</c> handler), not a receptor or perspective — so without this it would be
  /// dropped at the inbox/receive drop-gate before it could be stored and routed (migration 061). The
  /// abstract <c>CollectiveEventBase</c> itself is skipped (never dispatched).
  /// </summary>
  private static string? _extractCollectiveEntry(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken ct) {
    var typeDecl = context.Node;
    var symbol = context.SemanticModel.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
    if (symbol is null || symbol.IsAbstract) {
      return null;
    }
    var implementsCollective = symbol.AllInterfaces.Any(i =>
      i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == ICOLLECTIVE_EVENT_INTERFACE);
    if (!implementsCollective) {
      return null;
    }
    return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "");
  }

  // ===== Emission =====

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Smell", "S3776:Cognitive Complexity of methods should not be too high", Justification = "Generator emission walks every (receptor, perspective) pair and renders the routing branches inline; splitting would require building intermediate models for each branch shape, which costs incremental-cache parity.")]
  private static void _emitRegistryQuery(
      SourceProductionContext context,
      ImmutableArray<ReceptorRegistryEntry?> receptors,
      ImmutableArray<PerspectiveRegistryEntry[]?> perspectives,
      ImmutableArray<string?> taggedTypes,
      ImmutableArray<string?> compositeTypes,
      ImmutableArray<string?> collectiveTypes) {

    // Per-stage type sets (Pre/PostInbox only — those are what the receive boundary gates on)
    var stageTypes = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(System.StringComparer.Ordinal);
    foreach (var stage in _exposedStages) {
      stageTypes[stage] = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
    }

    var inboxHandlerTypes = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

    // Every receptor message type goes here regardless of which stage(s) it fires at.
    // Critical for HasAnyConsumer correctness: a receptor [FireAt(PostAllPerspectivesDetached)]
    // would otherwise be missed by anyConsumerTypes (since PostAllPerspectives is NOT an
    // exposed stage AND a FireAt receptor is not an inbox handler), causing the slice 3
    // drop-gate to silently drop messages whose only consumer is at that stage.
    var allReceptorMessageTypes = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

    foreach (var entry in receptors) {
      if (entry is null) {
        continue;
      }
      allReceptorMessageTypes.Add(entry.MessageType);
      foreach (var stage in entry.Stages) {
        if (stageTypes.TryGetValue(stage, out var set)) {
          set.Add(entry.MessageType);
        }
      }
      if (entry.IsInboxHandler) {
        inboxHandlerTypes.Add(entry.MessageType);
      }
    }

    // Default-stage void receptors (no [FireAt]) fire at PostInboxDetached/PostInboxInline
    // when messages arrive via transport. Register them in the per-stage lookup so the
    // InboxDispatchWorker gate (HasReceptors check) doesn't skip their invocation.
    stageTypes["PostInboxDetached"].UnionWith(inboxHandlerTypes);
    stageTypes["PostInboxInline"].UnionWith(inboxHandlerTypes);

    var anyConsumerTypes = new System.Collections.Generic.HashSet<string>(inboxHandlerTypes, System.StringComparer.Ordinal);
    anyConsumerTypes.UnionWith(allReceptorMessageTypes);
    foreach (var set in stageTypes.Values) {
      anyConsumerTypes.UnionWith(set);
    }
    foreach (var perspectiveEntries in perspectives) {
      if (perspectiveEntries is null) {
        continue;
      }
      foreach (var p in perspectiveEntries) {
        anyConsumerTypes.Add(p.EventType);
      }
    }
    foreach (var taggedType in taggedTypes) {
      if (taggedType is not null) {
        anyConsumerTypes.Add(taggedType);
      }
    }
    foreach (var compositeType in compositeTypes) {
      if (compositeType is not null) {
        anyConsumerTypes.Add(compositeType);
      }
    }
    foreach (var collectiveType in collectiveTypes) {
      if (collectiveType is not null) {
        anyConsumerTypes.Add(collectiveType);
      }
    }

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("// Generated by Whizbang.Generators.ReceptorRegistryQueryGenerator");
    sb.AppendLine("// DO NOT EDIT - regenerated each build.");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using System.Collections.Generic;");
    sb.AppendLine("using System.Runtime.CompilerServices;");
    sb.AppendLine("using Whizbang.Core.Messaging;");
    sb.AppendLine("using Whizbang.Core.Registry;");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Per-assembly registration of receptor / lifecycle-stage / handler / consumer info");
    sb.AppendLine("/// into the shared <see cref=\"AssemblyRegistry{T}\"/> of");
    sb.AppendLine("/// <see cref=\"ReceptorRegistryContribution\"/>. Runs at assembly load time via");
    sb.AppendLine("/// <see cref=\"ModuleInitializerAttribute\"/>. The aggregating");
    sb.AppendLine("/// <see cref=\"WhizbangReceptorRegistryQuery\"/> static reads the merged set across");
    sb.AppendLine("/// every loaded assembly.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("/// <remarks>");
    sb.AppendLine("/// Originally the generator emitted a duplicate <c>WhizbangReceptorRegistryQuery</c>");
    sb.AppendLine("/// static class with HashSets baked into every consuming assembly. Whizbang.Core's");
    sb.AppendLine("/// in-Core adapter bound at compile time to Whizbang.Core's empty copy — silently");
    sb.AppendLine("/// dropped every message at the receive-boundary drop-gate in production. Now uses");
    sb.AppendLine("/// the same <c>AssemblyRegistry&lt;T&gt;</c> primitive as <c>AutoPopulatePopulatorRegistry</c>,");
    sb.AppendLine("/// <c>JsonContextRegistry</c>, and <c>WhizbangIdProviderRegistry</c>.");
    sb.AppendLine("/// </remarks>");
    sb.AppendLine("internal static class WhizbangReceptorRegistryQueryRegistration {");
    sb.AppendLine("#pragma warning disable CA2255");
    sb.AppendLine("  [ModuleInitializer]");
    sb.AppendLine("#pragma warning restore CA2255");
    sb.AppendLine("  internal static void Register() {");
    sb.AppendLine();
    sb.AppendLine("    var stageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>>();");
    foreach (var stage in _exposedStages) {
      sb.AppendLine($"    stageTypes[LifecycleStage.{stage}] = new string[] {{");
      foreach (var type in stageTypes[stage].OrderBy(s => s, System.StringComparer.Ordinal)) {
        sb.AppendLine($"      \"{type}\",");
      }
      sb.AppendLine("    };");
    }
    sb.AppendLine();
    sb.AppendLine("    var contribution = new ReceptorRegistryContribution {");
    sb.AppendLine("      AnyConsumerTypes = new string[] {");
    foreach (var type in anyConsumerTypes.OrderBy(s => s, System.StringComparer.Ordinal)) {
      sb.AppendLine($"        \"{type}\",");
    }
    sb.AppendLine("      },");
    sb.AppendLine("      InboxHandlerTypes = new string[] {");
    foreach (var type in inboxHandlerTypes.OrderBy(s => s, System.StringComparer.Ordinal)) {
      sb.AppendLine($"        \"{type}\",");
    }
    sb.AppendLine("      },");
    sb.AppendLine("      StageTypes = stageTypes,");
    sb.AppendLine("    };");
    sb.AppendLine();
    sb.AppendLine("    AssemblyRegistry<ReceptorRegistryContribution>.Register(contribution);");
    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("WhizbangReceptorRegistryQueryRegistration.g.cs", sb.ToString());
  }
}
