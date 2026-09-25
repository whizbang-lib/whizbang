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

    // Cast rather than tested: the answer for a class declaration is the class, and a null or an
    // unexpected symbol falls into the interface check below rather than into a guard of its own
    // that nothing can reach.
    var declared = context.SemanticModel.GetDeclaredSymbol(
      (ClassDeclarationSyntax)context.Node, cancellationToken) as INamedTypeSymbol;

    var perspective = declared?.AllInterfaces.FirstOrDefault(
      i => i.Name == "IPerspectiveFor" && i.TypeArguments.Length > 0);

    if (perspective?.TypeArguments[0] is not INamedTypeSymbol model) {
      return null;
    }

    var paths = new List<AccessorPath>();
    _walk(model, prefix: "", access: "", depth: 0, paths);

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
      [.. paths]);
  }

  /// <summary>Collects the paths of a model and, to a bounded depth, of the objects it holds.</summary>
  /// <param name="type">The type being walked.</param>
  /// <param name="prefix">The dotted path so far, as a filter names it.</param>
  /// <param name="access">
  /// The same path as an expression to emit, which differs from <paramref name="prefix"/> once a
  /// hop is nullable: reaching through it needs the null-forgiving operator, or the consumer's own
  /// build raises CS8602 on a file it did not write.
  /// </param>
  /// <param name="depth">How deep the walk is, against <c>MAX_DEPTH</c>.</param>
  /// <param name="paths">The paths collected so far.</param>
  private static void _walk(
      INamedTypeSymbol type, string prefix, string access, int depth, List<AccessorPath> paths) {
    if (depth >= MAX_DEPTH) {
      return;
    }

    foreach (var property in type.GetAllProperties()) {
      if (!_isAddressable(property)) {
        continue;
      }

      var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
      var propertyAccess = access.Length == 0 ? property.Name : access + "." + property.Name;
      // Rendered WITH nullability rather than appending the outer annotation by hand, because the
      // annotations inside a generic argument count too: a List<string?> declared as a List<string>
      // is a nullability mismatch on the assignment itself (CS8619), not merely a laxer type.
      paths.Add(new AccessorPath(path, TypeNameUtilities.FullyQualifiedWithNullability(property.Type), propertyAccess));

      if (property.Type is INamedTypeSymbol nested && _hasPathsBeneath(nested)) {
        // Only a nullable hop is forgiven, and only the hop itself: the leaf keeps whatever
        // nullability it has, so nothing downstream is claimed to be non-null on its behalf.
        _walk(nested, path, propertyAccess + (_isNullableHop(property) ? "!" : ""), depth + 1, paths);
      }
    }
  }

  /// <summary>Whether a filter could name this property at all.</summary>
  /// <param name="property">The property being considered.</param>
  /// <returns><see langword="true"/> when it is public, readable and not static.</returns>
  private static bool _isAddressable(IPropertySymbol property) =>
    property.DeclaredAccessibility == Accessibility.Public
    && property.GetMethod is not null
    && !property.IsStatic;

  /// <summary>
  /// Whether reaching through this property needs the null-forgiving operator — that is, whether
  /// the property itself may be null.
  /// </summary>
  /// <param name="property">The hop being reached through.</param>
  /// <returns><see langword="true"/> when the hop is an annotated reference type.</returns>
  private static bool _isNullableHop(IPropertySymbol property) =>
    property.NullableAnnotation == NullableAnnotation.Annotated && property.Type.IsReferenceType;

  /// <summary>
  /// Whether a type has paths of its own worth walking. Only a plain object does: a collection's
  /// elements have no path a filter could name, and a primitive has nothing beneath it.
  /// </summary>
  /// <param name="type">The candidate type.</param>
  /// <returns><see langword="true"/> when the walk should descend into it.</returns>
  private static bool _hasPathsBeneath(INamedTypeSymbol type) =>
    type.TypeKind == TypeKind.Class
    && type.SpecialType == SpecialType.None
    && !type.AllInterfaces.Any(i => i.Name == "IEnumerable");


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
          $"  public static Expression<Func<{model.FullyQualifiedName}, {path.TypeName}>> {member} {{ get; }} = _m => _m.{path.Access};");
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

/// <summary>One addressable property path, the type it yields, and how to reach it.</summary>
/// <param name="Path">The dotted path a filter names.</param>
/// <param name="TypeName">The type the accessor yields, annotation included.</param>
/// <param name="Access">
/// The path as emitted, which carries a null-forgiving operator on every nullable hop it reaches
/// through. Identical to <paramref name="Path"/> when nothing on the way can be null.
/// </param>
internal sealed record AccessorPath(string Path, string TypeName, string Access);
