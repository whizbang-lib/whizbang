// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Discovers methods marked with <c>[CollectiveApplyFor]</c> at compile
/// time and emits an AOT-clean static dispatch table the projection
/// runner (Slice 7) walks at apply time. The emitted code is
/// reflection-free by construction — typed invoker lambdas perform the
/// handler/event casts statically.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/CollectiveApplyDiscoveryGeneratorTests.cs</tests>
[Generator]
public class CollectiveApplyDiscoveryGenerator : IIncrementalGenerator {

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

  /// <summary>
  /// Syntax predicate: admits a method declaration with at least one attribute.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsAttributedMethodCandidate(SyntaxNode node) =>
      node is MethodDeclarationSyntax { AttributeLists.Count: > 0 };

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Syntactic predicate: method with at least one attribute.
    // Cheap to evaluate, filters out 95%+ of nodes.
    var infos = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => IsAttributedMethodCandidate(node),
      transform: static (ctx, ct) => _extract(ctx, ct)
    ).Where(static info => info is not null);

    context.RegisterSourceOutput(
      infos.Collect(),
      static (ctx, items) => _emit(ctx, items!)
    );
  }

  private static CollectiveApplyInfo? _extract(GeneratorSyntaxContext context, CancellationToken ct) {
    var methodDecl = (MethodDeclarationSyntax)context.Node;
    // Find [CollectiveApplyFor] by FQN. The bind guard shares this exit: a method Roslyn cannot bind
    // exposes no attributes, so it fails the attribute test without ever being dereferenced.
    if (context.SemanticModel.GetDeclaredSymbol(methodDecl, ct) is not IMethodSymbol methodSymbol
        || CollectiveApplyShape.Attribute(methodSymbol) is not { } attr
        || CollectiveApplyShape.BoundModel(methodSymbol) is not { } modelType) {
      return null;
    }

    var modelFqn = TypeNameUtilities.FullyQualified(modelType);
    var eventFqn = TypeNameUtilities.FullyQualified(methodSymbol.Parameters[0].Type);
    var takesQuery = methodSymbol.Parameters.Length == 2;
    var handlerFqn = TypeNameUtilities.FullyQualified(methodSymbol.ContainingType);
    var (scopeHandling, specKind, batchSize, statementTimeoutSeconds) = _readNamedArguments(attr);

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

  /// <summary>
  /// Reads the attribute's named arguments. Enums come through as int (the underlying value); an argument whose
  /// value is not an int is ignored, leaving the default.
  /// </summary>
  private static (string ScopeHandling, string SpecKind, int BatchSize, int StatementTimeoutSeconds) _readNamedArguments(AttributeData attr) {
    var scopeHandling = "Framework";
    var specKind = "Linq";
    var batchSize = 0;
    var statementTimeoutSeconds = 0;
    foreach (var na in attr.NamedArguments) {
      if (na.Value.Value is not int value) {
        continue;
      }
      switch (na.Key) {
        case "ScopeHandling":
          scopeHandling = value == 1 ? "Custom" : "Framework";
          break;
        case "SpecKind":
          specKind = value == 1 ? "RawSql" : "Linq";
          break;
        case "BatchSize":
          batchSize = value;
          break;
        case "StatementTimeoutSeconds":
          statementTimeoutSeconds = value;
          break;
      }
    }

    return (scopeHandling, specKind, batchSize, statementTimeoutSeconds);
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
