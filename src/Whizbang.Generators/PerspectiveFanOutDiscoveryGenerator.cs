// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Discovers every perspective fan-out (<c>IPerspectiveFanOutFor&lt;TModel, TEvent&gt;</c>) at compile time, reads the
/// key column from its <c>Targets</c>, and emits the assembly's contribution to the fan-out registry the host serves:
/// one entry per (model, foreign event), with a typed invoker that calls <c>Apply</c> (no reflection). A declaration that
/// breaks a fan-out rule is left out, and <c>PerspectiveFanOutAnalyzer</c> reports it (#2618).
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveFanOutDiscoveryGeneratorTests.cs</tests>
[Generator]
public class PerspectiveFanOutDiscoveryGenerator : IIncrementalGenerator {
  private const string TEMPLATE_FILE = "PerspectiveFanOutRegistryTemplate.cs";
  private const string SNIPPETS_FILE = "PerspectiveFanOutSnippets.cs";

  /// <summary>One discovered fan-out. Sealed record for the incremental pipeline's value equality.</summary>
  internal sealed record FanOutInfo(string PerspectiveFqn, string ModelFqn, string EventFqn, string KeyProperty, string KeyColumn);

  /// <summary>
  /// Syntax predicate: admits a type declaration whose base list names <c>IPerspectiveFanOutFor&lt;,&gt;</c>.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsFanOutCandidate(SyntaxNode node) =>
    node is TypeDeclarationSyntax { BaseList: { } baseList } && baseList.Types.Any(static t => FanOutShape.NamesTheInterface(t.Type));

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var fanOuts = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: static (node, _) => IsFanOutCandidate(node),
      transform: static (ctx, ct) => _extract(ctx, ct)
    ).SelectMany(static (infos, _) => infos);

    var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName);

    context.RegisterSourceOutput(
      fanOuts.Collect().Combine(assemblyName),
      static (ctx, data) => _emit(ctx, data.Left, data.Right));
  }

  private static EquatableArray<FanOutInfo> _extract(GeneratorSyntaxContext context, CancellationToken ct) {
    if (context.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)context.Node, ct) is not INamedTypeSymbol { IsAbstract: false, TypeParameters.Length: 0 } type) {
      return new EquatableArray<FanOutInfo>([]);
    }
    var found = new List<FanOutInfo>();
    foreach (var fanOut in FanOutShape.Interfaces(type)) {
      if (_validKey(FanOutShape.TargetsOf(type, fanOut), context.SemanticModel, ct) is { } key) {
        found.Add(new FanOutInfo(
          PerspectiveFqn: TypeNameUtilities.FullyQualified(type),
          ModelFqn: TypeNameUtilities.FullyQualified(fanOut.TypeArguments[0]),
          EventFqn: TypeNameUtilities.FullyQualified(fanOut.TypeArguments[1]),
          KeyProperty: key.Name,
          KeyColumn: FanOutShape.ColumnName(key)));
      }
    }
    return new EquatableArray<FanOutInfo>([.. found]);
  }

  /// <summary>
  /// The key <paramref name="targets"/> names when it passes every fan-out rule, or null. A <c>Targets</c> with no source
  /// (declared in a referenced assembly) is left out: its key cannot be read here.
  /// </summary>
  private static IPropertySymbol? _validKey(IPropertySymbol? targets, SemanticModel model, CancellationToken ct) {
    if (targets?.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference) {
      return null;
    }
    // Targets is a property, so the syntax that declares it is a property declaration.
    var declaration = (PropertyDeclarationSyntax)reference.GetSyntax(ct);
    // A partial type can declare Targets in another file, whose own semantic model binds it.
    var declarationModel = declaration.SyntaxTree == model.SyntaxTree ? model : model.Compilation.GetSemanticModel(declaration.SyntaxTree);
    return FanOutShape.ReadKey(declaration, declarationModel, ct).Key is { } key && FanOutShape.IsValidKey(key) ? key : null;
  }

  private static void _emit(SourceProductionContext ctx, ImmutableArray<FanOutInfo> found, string? compilationAssemblyName) {
    // A partial perspective whose parts each list the interface is discovered once per part.
    var fanOuts = found.Distinct().ToList();
    if (fanOuts.Count == 0) {
      return;
    }
    var assemblyName = compilationAssemblyName ?? "UnknownAssembly";
    var assembly = typeof(PerspectiveFanOutDiscoveryGenerator).Assembly;
    var entrySnippet = TemplateUtilities.ExtractSnippet(assembly, SNIPPETS_FILE, "FAN_OUT_ENTRY");
    var entries = string.Join("\n", fanOuts.Select(info => entrySnippet
      .Replace("__PERSPECTIVE__", info.PerspectiveFqn)
      .Replace("__MODEL__", info.ModelFqn)
      .Replace("__EVENT__", info.EventFqn)
      .Replace("__KEY_PROPERTY__", ServiceAssemblyFactsGenerator.Literal(info.KeyProperty))
      .Replace("__KEY_COLUMN__", ServiceAssemblyFactsGenerator.Literal(info.KeyColumn))));

    var source = TemplateUtilities.GetEmbeddedTemplate(assembly, TEMPLATE_FILE);
    source = TemplateUtilities.ReplaceHeaderRegion(assembly, source);
    source = source
      .Replace("__NAMESPACE__", $"{assemblyName}.Generated")
      .Replace("__ASSEMBLY__", ServiceAssemblyFactsGenerator.Literal(assemblyName));
    source = TemplateUtilities.ReplaceRegion(source, "FAN_OUT_ENTRIES", entries);
    ctx.AddSource("PerspectiveFanOutRegistry.g.cs", source);
  }
}
