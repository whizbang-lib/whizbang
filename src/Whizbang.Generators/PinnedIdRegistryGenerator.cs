// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates an AOT-safe, zero-reflection <c>IPinnedIdRegistry</c> implementation by
/// discovering <c>[PinnedId]</c> attributes on concrete <c>IMessage</c> and
/// <c>IPerspectiveFor&lt;&gt;</c> types, and pinned strong ids (<c>[WhizbangId]</c>, #2604).
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/PinnedStrongIdTests.cs</tests>
[Generator]
public class PinnedIdRegistryGenerator : IIncrementalGenerator {
  /// <summary>
  /// Syntax predicate: admits a class, record or struct that can implement an interface and carry an
  /// attribute (each from its own syntax, or partial). The transform takes only a message or perspective with [PinnedId].
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <tests>tests/Whizbang.Generators.Component.Tests/BuildCost/SyntaxCandidateGuardTests.cs</tests>
  public static bool IsPinnedTypeCandidate(SyntaxNode node) =>
      node is (ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax) and TypeDeclarationSyntax type &&
      SyntaxCandidates.MayHaveBaseTypes(type) && SyntaxCandidates.MayHaveAttributes(type);

  /// <summary>Whether a node can declare a strong id by a [WhizbangId] struct (#2604); named on its syntax, so ordinary attributed code costs no symbol lookup.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/BuildCost/NarrowedCandidatePredicateTests.cs</tests>
  public static bool IsStrongIdStructCandidate(SyntaxNode node) =>
      node is StructDeclarationSyntax declaration && WhizbangIdDeclaration.NamesWhizbangId(declaration.AttributeLists);

  /// <summary>Whether a node can declare a strong id by a [WhizbangId] property (#2604); named on its syntax, so ordinary attributed code costs no symbol lookup.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/BuildCost/NarrowedCandidatePredicateTests.cs</tests>
  public static bool IsStrongIdPropertyCandidate(SyntaxNode node) =>
      node is PropertyDeclarationSyntax property && WhizbangIdDeclaration.NamesWhizbangId(property.AttributeLists);

  /// <summary>Whether a node can declare a strong id by a [WhizbangId] parameter (#2604); named on its syntax, so ordinary attributed code costs no symbol lookup.</summary>
  /// <tests>tests/Whizbang.Generators.Tests/BuildCost/NarrowedCandidatePredicateTests.cs</tests>
  public static bool IsStrongIdParameterCandidate(SyntaxNode node) =>
      node is ParameterSyntax parameter && WhizbangIdDeclaration.NamesWhizbangId(parameter.AttributeLists);

  /// <summary>
  /// Syntax predicate for the one discovery pass: a pinned message or perspective candidate, or a struct, property or
  /// parameter that names <c>[WhizbangId]</c>. One provider, not one per route, because every provider runs its
  /// predicate on every node of every file (#2604 build cost).
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/BuildCost/NarrowedCandidatePredicateTests.cs</tests>
  public static bool IsPinnedIdCandidate(SyntaxNode node) =>
      IsPinnedTypeCandidate(node) || IsStrongIdStructCandidate(node) || IsStrongIdPropertyCandidate(node) ||
      IsStrongIdParameterCandidate(node);

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Pinned messages and perspectives, and pinned strong ids (#2604) from each route a [WhizbangId] can be declared by.
    // A declaration in its own compilation always has its symbol.
    var allPinned = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsPinnedIdCandidate(node),
        transform: static (ctx, ct) => _discover(ctx, ct)
    ).Where(static info => info is not null).Collect();

    var combined = context.CompilationProvider.Combine(allPinned);

    context.RegisterSourceOutput(
        combined,
        static (ctx, data) => {
          var compilation = data.Left;
          var infos = data.Right;
          _generateRegistry(ctx, compilation, infos!);
        }
    );
  }

  /// <summary>
  /// A pinned message or perspective, else the pinned strong id the node declares. A <c>[WhizbangId]</c> struct is
  /// often partial, so it can pass the type test too and is read as a strong id when it is not a message.
  /// </summary>
  private static PinnedIdInfo? _discover(GeneratorSyntaxContext context, System.Threading.CancellationToken ct) =>
    (IsPinnedTypeCandidate(context.Node) ? _extractPinnedInfo(context, ct) : null)
      ?? (context.Node is TypeDeclarationSyntax and not StructDeclarationSyntax
        ? null
        : _strongIdOf(context.SemanticModel.GetDeclaredSymbol(context.Node, ct)!, ct));

  private static PinnedIdInfo? _extractPinnedInfo(
      GeneratorSyntaxContext context,
      System.Threading.CancellationToken cancellationToken) {
    var typeDeclaration = (TypeDeclarationSyntax)context.Node;
    var semanticModel = context.SemanticModel;

    // The bind guard shares the abstract-type exit: neither an unbound declaration nor an abstract
    // type can carry a pinned identity the registry maps to a constructible type.
    if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol
        || typeSymbol.IsAbstract) {
      return null;
    }

    if (typeSymbol.DeclaredAccessibility != Accessibility.Public) {
      return null;
    }

    var isMessage =
        TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_EVENT) ||
        TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_COMMAND) ||
        TypeNameHelper.ImplementsInterface(typeSymbol, StandardInterfaceNames.I_MESSAGE);

    var isPerspective = TypeNameHelper.ImplementsGenericInterface(
        typeSymbol,
        StandardInterfaceNames.I_PERSPECTIVE_FOR) ||
      TypeNameHelper.ImplementsGenericInterface(
        typeSymbol,
        StandardInterfaceNames.I_PERSPECTIVE_WITH_ACTIONS_FOR);

    if (!isMessage && !isPerspective) {
      return null;
    }

    var pinnedIdAttribute = typeSymbol.GetAttributes()
        .FirstOrDefault(attr =>
            TypeNameUtilities.IsFullyQualifiedNamed(attr.AttributeClass, StandardInterfaceNames.PINNED_ID_ATTRIBUTE));

    if (pinnedIdAttribute is null) {
      return null;
    }

    if (pinnedIdAttribute.ConstructorArguments.Length == 0 ||
        pinnedIdAttribute.ConstructorArguments[0].Value is not string pinnedId ||
        string.IsNullOrWhiteSpace(pinnedId)) {
      return null;
    }

    return new PinnedIdInfo(
        TypeName: TypeNameHelper.GetFullyQualifiedName(typeSymbol),
        PinnedId: pinnedId
    );
  }

  /// <summary>
  /// The pinned strong id <paramref name="symbol"/> declares, as the type the strong id generator emits, or null when it
  /// declares none or leaves it unpinned (#2604).
  /// </summary>
  private static PinnedIdInfo? _strongIdOf(ISymbol symbol, System.Threading.CancellationToken cancellationToken) =>
    WhizbangIdDeclaration.Read(symbol, cancellationToken) is { PinnedId: { } pinnedId } declared
      ? new PinnedIdInfo(TypeName: $"global::{declared.FullyQualifiedName}", PinnedId: pinnedId)
      : null;

  private static void _generateRegistry(
      SourceProductionContext context,
      Compilation compilation,
      ImmutableArray<PinnedIdInfo> infos) {

    if (infos.IsEmpty) {
      return;
    }

    var assemblyName = compilation.AssemblyName ?? "UnknownAssembly";
    var namespaceName = $"{assemblyName}.Generated";

    // An id declared twice (a property and a parameter) is one id
    var ordered = infos.Distinct().OrderBy(i => i.TypeName, System.StringComparer.Ordinal).ToList();

    var source = new StringBuilder();
    source.AppendLine("// <auto-generated/>");
    source.AppendLine($"// Generated by PinnedIdRegistryGenerator ({Whizbang.Generators.Shared.Utilities.TemplateUtilities.GetDeterministicBuildStamp(typeof(PinnedIdRegistryGenerator).Assembly)})");
    source.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    source.AppendLine("#nullable enable");
    source.AppendLine();
    source.AppendLine("using System;");
    source.AppendLine("using System.Collections.Generic;");
    source.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    source.AppendLine("using Whizbang.Core;");
    source.AppendLine();
    source.AppendLine($"namespace {namespaceName};");
    source.AppendLine();

    source.AppendLine("/// <summary>");
    source.AppendLine($"/// Auto-generated pinned-id registry for {ordered.Count} type(s).");
    source.AppendLine("/// Provides zero-reflection lookup for the stable type identity system.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public sealed class GeneratedPinnedIdRegistry : IPinnedIdRegistry {");
    source.AppendLine();

    source.AppendLine("  private static readonly IReadOnlyList<(Type Type, string PinnedId)> _all = new (Type, string)[] {");
    foreach (var info in ordered) {
      source.AppendLine($"    (typeof({info.TypeName}), \"{info.PinnedId}\"),");
    }
    source.AppendLine("  };");
    source.AppendLine();

    source.AppendLine("  /// <inheritdoc/>");
    source.AppendLine("  public string? GetPinnedId(Type type) {");
    foreach (var info in ordered) {
      source.AppendLine($"    if (type == typeof({info.TypeName})) return \"{info.PinnedId}\";");
    }
    source.AppendLine("    return null;");
    source.AppendLine("  }");
    source.AppendLine();

    source.AppendLine("  /// <inheritdoc/>");
    source.AppendLine("  public IReadOnlyList<(Type Type, string PinnedId)> GetAll() => _all;");
    source.AppendLine("}");
    source.AppendLine();

    source.AppendLine("/// <summary>");
    source.AppendLine("/// Extension methods for registering the generated pinned-id registry.");
    source.AppendLine("/// </summary>");
    source.AppendLine("public static class PinnedIdRegistryExtensions {");
    source.AppendLine("  /// <summary>");
    source.AppendLine($"  /// Registers GeneratedPinnedIdRegistry with {ordered.Count} type(s) as a singleton IPinnedIdRegistry.");
    source.AppendLine("  /// Called automatically by AddWhizbang() via a module initializer, but may be invoked");
    source.AppendLine("  /// directly when configuring DI without calling AddWhizbang().");
    source.AppendLine("  /// </summary>");
    source.AppendLine("  public static IServiceCollection AddPinnedIdRegistry(");
    source.AppendLine("      this IServiceCollection services) {");
    // #2198: a contribution to the one registry the host serves, never the registry itself. Whizbang.Core pins types
    // too, so every host has at least two of these registrations, and a plain AddSingleton kept only one.
    source.AppendLine("    global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<GeneratedPinnedIdRegistry>(services);");
    source.AppendLine("    global::Whizbang.Core.PinnedIdRegistryServiceCollectionExtensions.AddPinnedIdRegistryContribution<GeneratedPinnedIdRegistry>(services);");
    source.AppendLine("    return services;");
    source.AppendLine("  }");
    source.AppendLine("}");
    source.AppendLine();

    source.AppendLine("/// <summary>");
    source.AppendLine("/// Module initializer that wires GeneratedPinnedIdRegistry into AddWhizbang().");
    source.AppendLine("/// Fires when the consumer assembly loads, so AddWhizbang() registers IPinnedIdRegistry");
    source.AppendLine("/// automatically with no explicit call from the consumer.");
    source.AppendLine("/// </summary>");
    source.AppendLine("internal static class PinnedIdRegistryModuleInitializer {");
    source.AppendLine("  [global::System.Runtime.CompilerServices.ModuleInitializer]");
    source.AppendLine("  internal static void Initialize() {");
    // #2243: named by this assembly, so a host replays only its own service's registrations
    source.AppendLine($"    ServiceRegistrationCallbacks.RegisterPinnedIdRegistry({ServiceAssemblyFactsGenerator.Literal(assemblyName)}, static services =>");
    source.AppendLine("      PinnedIdRegistryExtensions.AddPinnedIdRegistry(services));");
    source.AppendLine("  }");
    source.AppendLine("}");

    context.AddSource("PinnedIdRegistry.g.cs", source.ToString());
  }
}

/// <summary>
/// Value type containing information about a discovered pinned type.
/// Uses value equality for incremental generator caching.
/// </summary>
internal sealed record PinnedIdInfo(
    string TypeName,
    string PinnedId
);
