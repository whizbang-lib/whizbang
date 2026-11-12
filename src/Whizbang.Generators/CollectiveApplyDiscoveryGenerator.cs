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
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsAttributedMethodCandidate(SyntaxNode node) =>
      node is MethodDeclarationSyntax { AttributeLists.Count: > 0 };

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Syntactic predicate: method with at least one attribute.
    // Cheap to evaluate, filters out 95%+ of nodes.
    var infos = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => IsAttributedMethodCandidate(node),
      transform: static (ctx, ct) => _extract(ctx, ct)
    ).Where(static info => info is not null)
      .Select(static (info, _) => info!);

    // The assembly name keys the contribution's namespace and its startup slot (#2238, #2243); a name change is rare, so
    // combining it costs no incremental reuse in practice.
    var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName);

    context.RegisterSourceOutput(
      infos.Collect().Combine(assemblyName),
      static (ctx, data) => _emit(ctx, data.Left, data.Right)
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

  /// <summary>
  /// Emits the assembly's handler table. With handlers, the table is the assembly's contribution to the one
  /// <c>ICollectiveApplyRegistry</c> the host serves, registered by a module initializer named by the assembly, so the
  /// collective dispatcher knows every assembly's handlers and the consumer passes nothing by hand (#2238). The legacy
  /// <c>Whizbang.Core.Generated.CollectiveApplyRegistry.Entries</c> stays, reading the same table, so a consumer that
  /// still passes it keeps compiling.
  /// </summary>
  private static void _emit(SourceProductionContext ctx, ImmutableArray<CollectiveApplyInfo> found, string? compilationAssemblyName) {
    var infos = found.ToList();
    var assemblyName = compilationAssemblyName ?? "UnknownAssembly";
    var contributionNamespace = $"{assemblyName}.Generated";
    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine("// Source: Whizbang.Generators.CollectiveApplyDiscoveryGenerator (Slice 5 of collective-events)");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    if (infos.Count > 0) {
      _appendContribution(sb, infos, assemblyName, contributionNamespace);
    }
    _appendLegacyTable(sb, infos.Count > 0 ? $"global::{contributionNamespace}.GeneratedCollectiveApplyRegistry.All" : null);
    ctx.AddSource("CollectiveApplyRegistry.g.cs", sb.ToString());
  }

  /// <summary>
  /// The contribution: the table, its registration as a contribution, and the module initializer that hands the
  /// registration to the assembly's startup slot.
  /// </summary>
  private static void _appendContribution(StringBuilder sb, List<CollectiveApplyInfo> infos, string assemblyName, string contributionNamespace) {
    sb.AppendLine($"namespace {contributionNamespace} {{");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// This assembly's methods marked with <see cref=\"global::Whizbang.Core.Perspectives.CollectiveApplyForAttribute\"/>:");
    sb.AppendLine("/// its contribution to the collective apply registry the host serves. Reflection-free; the invokers cast statically.");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public sealed class GeneratedCollectiveApplyRegistry : global::Whizbang.Core.Perspectives.ICollectiveApplyRegistry {");
    sb.AppendLine("  /// <summary>The discovered handlers.</summary>");
    sb.AppendLine("  internal static readonly global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Perspectives.CollectiveApplyEntry> All = new global::Whizbang.Core.Perspectives.CollectiveApplyEntry[] {");
    foreach (var info in infos) {
      _appendEntry(sb, info);
    }
    sb.AppendLine("  };");
    sb.AppendLine();
    sb.AppendLine("  /// <inheritdoc/>");
    sb.AppendLine("  public global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Perspectives.CollectiveApplyEntry> Entries => All;");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("/// <summary>Registers this assembly's collective apply handlers.</summary>");
    sb.AppendLine("public static class CollectiveApplyRegistryExtensions {");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Adds this assembly's handlers to the collective apply registry the container serves. Called by AddWhizbang()");
    sb.AppendLine("  /// through the module initializer below; call it directly only when configuring DI without AddWhizbang().");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddCollectiveApplyRegistry(");
    sb.AppendLine("      this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) {");
    sb.AppendLine("    global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<GeneratedCollectiveApplyRegistry>(services);");
    sb.AppendLine("    global::Whizbang.Core.Perspectives.CollectiveApplyRegistryServiceCollectionExtensions.AddCollectiveApplyRegistryContribution<GeneratedCollectiveApplyRegistry>(services);");
    sb.AppendLine("    return services;");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("/// <summary>Hands this assembly's handler registration to AddWhizbang(), named by the assembly.</summary>");
    sb.AppendLine("internal static class CollectiveApplyRegistryModuleInitializer {");
    sb.AppendLine("  [global::System.Runtime.CompilerServices.ModuleInitializer]");
    sb.AppendLine("  internal static void Initialize() {");
    // #2243: named by this assembly, so a host anchored to its service registers only its own service's handlers
    sb.AppendLine($"    global::Whizbang.Core.ServiceRegistrationCallbacks.RegisterCollectiveApplyRegistry({ServiceAssemblyFactsGenerator.Literal(assemblyName)}, static services =>");
    sb.AppendLine("      CollectiveApplyRegistryExtensions.AddCollectiveApplyRegistry(services));");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("}");
    sb.AppendLine();
  }

  private static void _appendEntry(StringBuilder sb, CollectiveApplyInfo info) {
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

  /// <summary>
  /// The legacy table a consumer used to pass by hand: the contribution's table when the assembly has handlers
  /// (<paramref name="contributionTable"/>), otherwise empty.
  /// </summary>
  private static void _appendLegacyTable(StringBuilder sb, string? contributionTable) {
    sb.AppendLine("namespace Whizbang.Core.Generated {");
    sb.AppendLine();
    sb.AppendLine("/// <summary>");
    sb.AppendLine("/// This assembly's methods marked with");
    sb.AppendLine("/// <see cref=\"global::Whizbang.Core.Perspectives.CollectiveApplyForAttribute\"/>. The collective dispatcher no");
    sb.AppendLine("/// longer needs it passed by hand: every assembly's handlers are registered automatically (#2238).");
    sb.AppendLine("/// </summary>");
    sb.AppendLine("public static class CollectiveApplyRegistry {");
    sb.AppendLine("  /// <summary>The discovered handlers. Order is undefined; consumers index by (ModelType, EventType).</summary>");
    sb.Append("  public static readonly global::System.Collections.Generic.IReadOnlyList<global::Whizbang.Core.Perspectives.CollectiveApplyEntry> Entries = ");
    sb.AppendLine(contributionTable is null ? "new global::Whizbang.Core.Perspectives.CollectiveApplyEntry[] { };" : $"{contributionTable};");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("}");
  }
}
