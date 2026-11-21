// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Records, in every assembly compiled with the generators, the build-time facts a host decides its service from
/// (#2243): the assembly's name, whether it is a service root (an executable, or an assembly that declares a
/// <c>[WhizbangDbContext]</c>), and the generated assemblies it was compiled against.
/// </summary>
/// <remarks>
/// <para>
/// Emits, from <c>Templates/ServiceAssemblyFactsTemplate.cs</c>, <c>[assembly: WhizbangServiceAssembly]</c>, the marker that lets an assembly compiled against this one know
/// it is a generated assembly, and a module initializer that calls <c>WhizbangServiceAssemblies.Register</c>. The
/// referenced assemblies are read from the compilation's references, so the set is fixed at build time and the
/// runtime reads names, never metadata.
/// </para>
/// <para>
/// Nothing is emitted for a compilation without a name, or one that does not reference Whizbang.Core (it has nothing
/// to register into).
/// </para>
/// <para>
/// #2502: the module initializer also calls the registrar of every referenced package marked
/// <c>[assembly: WhizbangTurnkeyPackage(typeof(...))]</c>, so a package a host references and configures activates
/// without any code naming its types (an assembly no code touches is never loaded, and its own module initializer never
/// runs). A registrar without a public static, parameterless <c>Register()</c> is skipped.
/// </para>
/// </remarks>
/// <docs>extending/extensibility/replaceable-services#service-scope</docs>
/// <tests>tests/Whizbang.Generators.Tests/ServiceAssemblyFactsGeneratorTests.cs</tests>
[Generator]
public sealed class ServiceAssemblyFactsGenerator : IIncrementalGenerator {
  internal const string MARKER_ATTRIBUTE = "Whizbang.Core.WhizbangServiceAssemblyAttribute";
  internal const string REGISTRY = "Whizbang.Core.WhizbangServiceAssemblies";
  internal const string DB_CONTEXT_ATTRIBUTE = "Whizbang.Data.EFCore.Custom.WhizbangDbContextAttribute";
  internal const string TURNKEY_ATTRIBUTE = "Whizbang.Core.WhizbangTurnkeyPackageAttribute";
  private const string TEMPLATE_FILE = "ServiceAssemblyFactsTemplate.cs";
  private const string SNIPPETS_FILE = "ServiceAssemblyFactsSnippets.cs";

  /// <summary>The facts of one compilation, equatable so an edit that changes none of them regenerates nothing.</summary>
  private sealed record Facts(
    string? Assembly, bool IsExecutable, bool HasRegistry, EquatableArray<string> References, EquatableArray<string> Turnkeys);

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var declaresDbContext = context.SyntaxProvider
      .ForAttributeWithMetadataName(
        DB_CONTEXT_ATTRIBUTE,
        predicate: static (node, _) => node is ClassDeclarationSyntax,
        transform: static (_, _) => true)
      .Collect()
      .Select(static (found, _) => !found.IsDefaultOrEmpty);

    var facts = context.CompilationProvider.Select(static (compilation, _) => _facts(compilation));

    context.RegisterSourceOutput(facts.Combine(declaresDbContext), static (ctx, data) => _emit(ctx, data.Left, data.Right));
  }

  private static Facts _facts(Compilation compilation) {
    // The marker and the registry ship together in Whizbang.Core; without the marker no reference matches.
    var marker = compilation.GetTypeByMetadataName(MARKER_ATTRIBUTE);
    var references = compilation.SourceModule.ReferencedAssemblySymbols
      .Where(assembly => assembly.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, marker)))
      .Select(TypeNameUtilities.AssemblyIdentityName)
      .Distinct()
      .OrderBy(name => name, System.StringComparer.Ordinal)
      .ToArray();
    var isExecutable = compilation.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication;
    var hasRegistry = compilation.GetTypeByMetadataName(REGISTRY) is not null;
    return new Facts(compilation.AssemblyName, isExecutable, hasRegistry, new EquatableArray<string>([.. references]),
      new EquatableArray<string>([.. _turnkeys(compilation)]));
  }

  // The registrars of the referenced turnkey packages (#2502), fully qualified and sorted: each a public type with a
  // public static, parameterless Register().
  private static string[] _turnkeys(Compilation compilation) {
    var turnkey = compilation.GetTypeByMetadataName(TURNKEY_ATTRIBUTE);
    if (turnkey is null) {
      return [];
    }
    return compilation.SourceModule.ReferencedAssemblySymbols
      .SelectMany(assembly => assembly.GetAttributes())
      .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, turnkey))
      .Select(a => a.ConstructorArguments.Length == 1 ? a.ConstructorArguments[0].Value as INamedTypeSymbol : null)
      .Where(registrar => registrar is { DeclaredAccessibility: Accessibility.Public }
        && registrar.GetMembers("Register").OfType<IMethodSymbol>()
          .Any(m => m.IsStatic && m.Parameters.Length == 0 && m.DeclaredAccessibility == Accessibility.Public))
      .Select(registrar => TypeNameUtilities.FullyQualified(registrar!))
      .Distinct()
      .OrderBy(name => name, System.StringComparer.Ordinal)
      .ToArray();
  }

  private static void _emit(SourceProductionContext context, Facts facts, bool declaresDbContext) {
    if (facts.Assembly is null || !facts.HasRegistry) {
      return;
    }
    var assembly = typeof(ServiceAssemblyFactsGenerator).Assembly;
    var turnkeySnippet = TemplateUtilities.ExtractSnippet(assembly, SNIPPETS_FILE, "TURNKEY_PACKAGE").Trim();
    var turnkeys = string.Join("\n", facts.Turnkeys.Select(registrar => turnkeySnippet.Replace("__REGISTRAR__", registrar)));

    var source = TemplateUtilities.GetEmbeddedTemplate(assembly, TEMPLATE_FILE);
    source = TemplateUtilities.ReplaceHeaderRegion(assembly, source);
    source = source
      .Replace("__ASSEMBLY__", Literal(facts.Assembly))
      .Replace("__IS_ROOT__", facts.IsExecutable || declaresDbContext ? "true" : "false")
      .Replace("__REFERENCES__", string.Join(", ", facts.References.Select(Literal)));
    source = TemplateUtilities.ReplaceRegion(source, "TURNKEY_PACKAGES", turnkeys);
    context.AddSource("WhizbangServiceAssemblyFacts.g.cs", source);
  }

  /// <summary>A C# string literal for an assembly name.</summary>
  public static string Literal(string value) =>
    "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
