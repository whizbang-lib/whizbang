using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators;

/// <summary>
/// Discovers methods marked with <c>[CollectiveApplyFor]</c> at compile
/// time and emits an AOT-clean static dispatch table the projection
/// runner (Slice 7) walks at apply time. The emitted code is
/// reflection-free by construction — typed invoker lambdas perform the
/// handler/event casts statically.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>Whizbang.Generators.Tests/CollectiveApplyDiscoveryGeneratorTests.cs</tests>
[Generator]
public class CollectiveApplyDiscoveryGenerator : IIncrementalGenerator {
  private const string ATTRIBUTE_FQN = "Whizbang.Core.Perspectives.CollectiveApplyForAttribute";
  private const string COLLECTIVE_EVENT_FQN = "Whizbang.Core.Messaging.ICollectiveEvent";
  private const string COLLECTIVE_QUERY_FQN = "Whizbang.Core.Perspectives.ICollectiveQuery";
  private const string SPEC_TYPE_NAME = "ICollectiveSpec";
  private const string SPEC_NAMESPACE = "Whizbang.Core.Perspectives";

  /// <summary>
  /// Value-equality record of a discovered handler. Sealed + record so the
  /// incremental pipeline's caching works across rebuilds.
  /// </summary>
  internal sealed record CollectiveApplyInfo(
    string ModelTypeFqn,
    string EventTypeFqn,
    string HandlerTypeFqn,
    string MethodName,
    string ScopeHandlingEnumName,
    string SpecKindEnumName,
    bool TakesQuery,
    int BatchSizeOverride,
    int StatementTimeoutSecondsOverride
  );

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Syntactic predicate: method with at least one attribute.
    // Cheap to evaluate, filters out 95%+ of nodes.
    var infos = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
      transform: static (ctx, ct) => _extract(ctx, ct)
    ).Where(static info => info is not null);

    context.RegisterSourceOutput(
      infos.Collect(),
      static (ctx, items) => _emit(ctx, items!)
    );
  }

  private static CollectiveApplyInfo? _extract(GeneratorSyntaxContext context, CancellationToken ct) {
    var methodDecl = (MethodDeclarationSyntax)context.Node;
    if (context.SemanticModel.GetDeclaredSymbol(methodDecl, ct) is not IMethodSymbol methodSymbol) {
      return null;
    }

    // Find [CollectiveApplyFor] by FQN.
    var attr = methodSymbol.GetAttributes()
      .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ATTRIBUTE_FQN);
    if (attr is null) {
      return null;
    }

    // Return type must be ICollectiveSpec<TModel>. Reject other shapes
    // silently so a misuse of the attribute doesn't crash the generator —
    // an analyzer slice could diagnose this later.
    if (methodSymbol.ReturnType is not INamedTypeSymbol returnType ||
        !returnType.IsGenericType ||
        returnType.OriginalDefinition.Name != SPEC_TYPE_NAME ||
        returnType.OriginalDefinition.ContainingNamespace?.ToDisplayString() != SPEC_NAMESPACE) {
      return null;
    }

    if (returnType.TypeArguments.Length != 1) {
      return null;
    }
    var modelType = returnType.TypeArguments[0];
    var modelFqn = modelType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // First parameter implements ICollectiveEvent; an optional second parameter is the
    // ICollectiveQuery context (handlers that scope by a sibling perspective take it; others omit it).
    if (methodSymbol.Parameters.Length is < 1 or > 2) {
      return null;
    }
    var eventType = methodSymbol.Parameters[0].Type;
    var implementsCollectiveEvent = eventType.AllInterfaces
      .Any(i => i.ToDisplayString() == COLLECTIVE_EVENT_FQN);
    if (!implementsCollectiveEvent) {
      return null;
    }
    var eventFqn = eventType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    var takesQuery = methodSymbol.Parameters.Length == 2;
    if (takesQuery) {
      var queryParam = methodSymbol.Parameters[1].Type;
      var isCollectiveQuery = queryParam.ToDisplayString() == COLLECTIVE_QUERY_FQN
        || queryParam.AllInterfaces.Any(i => i.ToDisplayString() == COLLECTIVE_QUERY_FQN);
      if (!isCollectiveQuery) {
        return null;
      }
    }

    var handlerFqn = methodSymbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    // Attribute named-argument enums come through as int (the underlying value).
    var scopeHandling = "Framework";
    var specKind = "Linq";
    var batchSize = 0;
    var statementTimeoutSeconds = 0;
    foreach (var na in attr.NamedArguments) {
      if (na.Key == "ScopeHandling" && na.Value.Value is int sh) {
        scopeHandling = sh == 1 ? "Custom" : "Framework";
      } else if (na.Key == "SpecKind" && na.Value.Value is int sk) {
        specKind = sk == 1 ? "RawSql" : "Linq";
      } else if (na.Key == "BatchSize" && na.Value.Value is int bs) {
        batchSize = bs;
      } else if (na.Key == "StatementTimeoutSeconds" && na.Value.Value is int st) {
        statementTimeoutSeconds = st;
      }
    }

    return new CollectiveApplyInfo(
      ModelTypeFqn: modelFqn,
      EventTypeFqn: eventFqn,
      HandlerTypeFqn: handlerFqn,
      MethodName: methodSymbol.Name,
      ScopeHandlingEnumName: scopeHandling,
      SpecKindEnumName: specKind,
      TakesQuery: takesQuery,
      BatchSizeOverride: batchSize,
      StatementTimeoutSecondsOverride: statementTimeoutSeconds
    );
  }

  private static void _emit(SourceProductionContext ctx, ImmutableArray<CollectiveApplyInfo> infos) {
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("// Source: Whizbang.Generators.CollectiveApplyDiscoveryGenerator (Slice 5 of collective-events)");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Core.Generated;");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// Auto-generated registry of methods marked with");
    sb.AppendLine("/// <see cref=\"global::Whizbang.Core.Perspectives.CollectiveApplyForAttribute\"/>. The");
    sb.AppendLine("/// projection runner walks this table at apply time — no reflection,");
    sb.AppendLine("/// AOT-clean by construction.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public static class CollectiveApplyRegistry {");
    sb.AppendLine("  /// <summary>The discovered handlers. Order is undefined; consumers index by (ModelType, EventType).</summary>");
    sb.Append("  public static readonly global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Perspectives.CollectiveApplyEntry> Entries = new global::Whizbang.Core.Perspectives.CollectiveApplyEntry[] {");

    if (infos.IsEmpty || infos.All(i => i is null)) {
      sb.AppendLine(" };");
    } else {
      sb.AppendLine();
      foreach (var info in infos) {
        if (info is null) {
          continue;
        }
        sb.AppendLine("    new global::Whizbang.Core.Perspectives.CollectiveApplyEntry(");
        sb.AppendLine($"      ModelType: typeof({info.ModelTypeFqn}),");
        sb.AppendLine($"      EventType: typeof({info.EventTypeFqn}),");
        sb.AppendLine($"      HandlerType: typeof({info.HandlerTypeFqn}),");
        sb.AppendLine($"      MethodName: \"{info.MethodName}\",");
        sb.AppendLine($"      ScopeHandling: global::Whizbang.Core.Perspectives.CollectiveScopeHandling.{info.ScopeHandlingEnumName},");
        sb.AppendLine($"      SpecKind: global::Whizbang.Core.Perspectives.CollectiveSpecKind.{info.SpecKindEnumName},");
        var invokeArgs = info.TakesQuery
          ? $"(({info.EventTypeFqn})evt, query)"
          : $"(({info.EventTypeFqn})evt)";
        sb.AppendLine($"      Invoker: static (handler, evt, query) => (({info.HandlerTypeFqn})handler).{info.MethodName}{invokeArgs},");
        sb.AppendLine($"      BatchSizeOverride: {info.BatchSizeOverride},");
        sb.AppendLine($"      StatementTimeoutSecondsOverride: {info.StatementTimeoutSecondsOverride}");
        sb.AppendLine("    ),");
      }
      sb.AppendLine("  };");
    }

    sb.AppendLine("}");

    ctx.AddSource("CollectiveApplyRegistry.g.cs", sb.ToString());
  }
}
