using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Models;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Generates a way to address a perspective model's properties as expressions, keyed by the path
/// that names them.
/// </summary>
/// <remarks>
/// <para>
/// Anything that builds a predicate from text rather than from code needs to turn a property path
/// into an expression: a filter language, a saved search, a rules engine. Reflection is the obvious
/// way and is the one thing this framework does not allow itself, because a trimmer cannot see
/// through it and native compilation cannot resolve it at all.
/// </para>
/// <para>
/// So the lookup is generated. Every path a consumer can filter on is a case in a switch, which the
/// compiler can see and the trimmer can keep, and a path that was never generated is simply absent
/// rather than a reflection failure at run time.
/// </para>
/// <para>
/// Nested paths resolve as well, because a model with structure is exactly the one that attracts
/// dynamic filtering. The walk stops at a fixed depth and at any type that is not a plain object,
/// which keeps a model that refers back to itself from generating forever. Depth is what bounds it,
/// rather than refusing a type already seen: a model holding a reference to its own kind is
/// ordinary, and one step through it is a path a filter would really write.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/typed-accessors</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveAccessorGeneratorTests.cs</tests>
[Generator]
public class PerspectiveAccessorGenerator : IIncrementalGenerator {
  /// <summary>How far into a model's own objects a path is generated.</summary>
  /// <remarks>
  /// Three is what the models that attract dynamic filtering actually use, and a bound is what stops
  /// a model holding a reference to its own type from generating an unbounded set of paths.
  /// </remarks>
  private const int MAX_DEPTH = 3;

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var models = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
        transform: static (ctx, ct) => _extract(ctx, ct)
    ).Where(static model => model is not null);

    context.RegisterSourceOutput(
        models.Collect(),
        static (ctx, candidates) => _emit(ctx, candidates));
  }

  /// <summary>The model behind a perspective declaration, with the paths it can be filtered on.</summary>
  private static AccessorModel? _extract(GeneratorSyntaxContext context, System.Threading.CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();

    if (context.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)context.Node, cancellationToken)
        is not INamedTypeSymbol declared) {
      return null;
    }

    var perspective = declared.AllInterfaces.FirstOrDefault(
      i => i.Name == "IPerspectiveFor" && i.TypeArguments.Length > 0);

    if (perspective?.TypeArguments[0] is not INamedTypeSymbol model) {
      return null;
    }

    var paths = new List<AccessorPath>();
    _walk(model, prefix: "", depth: 0, paths);

    if (paths.Count == 0) {
      return null;
    }

    var containing = TypeNameUtilities.NamespaceName(model.ContainingNamespace);

    // A model nested in another type shares its namespace with every other model nested in a
    // sibling, and they may share a simple name. The declaring types are part of the name here for
    // the same reason they are part of the model's own.
    var declaring = new List<string>();
    for (var outer = model.ContainingType; outer is not null; outer = outer.ContainingType) {
      declaring.Insert(0, outer.Name);
    }

    declaring.Add(model.Name);
    var name = string.Join("_", declaring);

    // A model no more visible than its assembly cannot be handed out by a public property. The
    // accessors live beside the model, so matching it is enough.
    var visible = model.DeclaredAccessibility == Accessibility.Public;
    for (var outer = model.ContainingType; visible && outer is not null; outer = outer.ContainingType) {
      visible = outer.DeclaredAccessibility == Accessibility.Public;
    }

    return new AccessorModel(
      name,
      string.IsNullOrEmpty(containing) ? null : containing,
      TypeNameUtilities.FullyQualified(model),
      visible,
      paths.ToImmutableArray());
  }

  /// <summary>Collects the paths of a model and, to a bounded depth, of the objects it holds.</summary>
  private static void _walk(INamedTypeSymbol type, string prefix, int depth, List<AccessorPath> paths) {
    if (depth >= MAX_DEPTH) {
      return;
    }

    foreach (var property in type.GetAllProperties()) {
      if (property.DeclaredAccessibility != Accessibility.Public || property.GetMethod is null || property.IsStatic) {
        continue;
      }

      var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
      // The annotation is part of the type the accessor yields. Without it the lambda returning a
      // property that may be null is a nullable warning in the consumer's own build, which is their
      // build broken by generated code rather than by anything they wrote.
      var nullable = property.NullableAnnotation == NullableAnnotation.Annotated && property.Type.IsReferenceType
        ? "?"
        : string.Empty;

      paths.Add(new AccessorPath(path, TypeNameUtilities.FullyQualified(property.Type) + nullable));

      // Only a plain object is descended into. A collection's elements have no path of their own
      // that a filter could name, and a primitive has nothing beneath it.
      if (property.Type is INamedTypeSymbol nested
          && nested.TypeKind == TypeKind.Class
          && nested.SpecialType == SpecialType.None
          && !nested.AllInterfaces.Any(i => i.Name == "IEnumerable")) {
        _walk(nested, path, depth + 1, paths);
      }
    }

  }

  /// <summary>Writes one accessor class per model.</summary>
  private static void _emit(SourceProductionContext context, ImmutableArray<AccessorModel?> candidates) {
    foreach (var model in candidates.Where(m => m is not null).Select(m => m!).GroupBy(m => m.FullyQualifiedName).Select(g => g.First())) {
      var sb = new StringBuilder();
      sb.AppendLine("// <auto-generated/>");
      sb.AppendLine("#nullable enable");
      sb.AppendLine();
      sb.AppendLine("using System;");
      sb.AppendLine("using System.Linq.Expressions;");
      sb.AppendLine();
      // The model's own namespace rather than one of the framework's. Two models in different
      // namespaces may share a simple name, and putting both in one namespace would make their
      // accessor classes collide; here they no more collide than the models do.
      sb.AppendLine($"namespace {model.Namespace ?? "Whizbang.Generated"};");
      sb.AppendLine();
      sb.AppendLine("/// <summary>");
      sb.AppendLine($"/// The properties of {model.Name} as expressions, so a filter built from text needs no reflection.");
      sb.AppendLine("/// </summary>");
      sb.AppendLine($"{(model.IsPublic ? "public" : "internal")} static class {model.Name}Accessors {{");

      foreach (var path in model.Paths) {
        var member = path.Path.Replace(".", "");
        sb.AppendLine($"  /// <summary>The <c>{path.Path}</c> property.</summary>");
        sb.AppendLine(
          $"  public static Expression<Func<{model.FullyQualifiedName}, {path.TypeName}>> {member} {{ get; }} = _m => _m.{path.Path};");
      }

      sb.AppendLine();
      sb.AppendLine("  /// <summary>The accessor for a path, or false when the model has no such path.</summary>");
      sb.AppendLine("  /// <param name=\"path\">The property path, as a filter names it.</param>");
      sb.AppendLine("  /// <param name=\"accessor\">The accessor, when there is one.</param>");
      sb.AppendLine("  /// <returns>Whether the path is one this model has.</returns>");
      sb.AppendLine("  public static bool TryGet(string path, out LambdaExpression? accessor) {");
      sb.AppendLine("    switch (path) {");

      foreach (var path in model.Paths) {
        sb.AppendLine($"      case \"{path.Path}\":");
        sb.AppendLine($"        accessor = {path.Path.Replace(".", "")};");
        sb.AppendLine("        return true;");
      }

      sb.AppendLine("      default:");
      sb.AppendLine("        accessor = null;");
      sb.AppendLine("        return false;");
      sb.AppendLine("    }");
      sb.AppendLine("  }");
      sb.AppendLine("}");

      // Keyed on the fully qualified name for the same reason, since the file name has to be unique
      // across the whole compilation and the simple name is not.
      var hint = model.FullyQualifiedName.Replace("global::", "").Replace(".", "_").Replace("+", "_");
      context.AddSource($"{hint}Accessors.g.cs", sb.ToString());
    }
  }
}

/// <summary>A perspective model and the paths it can be filtered on.</summary>
internal sealed record AccessorModel(
    string Name, string? Namespace, string FullyQualifiedName, bool IsPublic, ImmutableArray<AccessorPath> Paths);

/// <summary>One addressable property path and the type it yields.</summary>
internal sealed record AccessorPath(string Path, string TypeName);
