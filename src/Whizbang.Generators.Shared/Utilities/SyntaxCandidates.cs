// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Cheap syntactic checks a generator's syntax predicate uses to keep ordinary code away from its semantic
/// transform. Each answers whether a declaration could possibly have what the transform looks for, reading
/// nothing but the declaration's own syntax, so a predicate built from them admits exactly the declarations the
/// transform could accept and nothing else.
/// </summary>
/// <remarks>
/// A partial declaration always passes: another declaration of the same type, which the predicate does not
/// see, may carry the base list or the attribute, and the transform binds the whole type from any of them.
/// </remarks>
/// <docs>operations/performance/build-cost</docs>
/// <tests>tests/Whizbang.Generators.Tests/Utilities/SyntaxCandidatesTests.cs</tests>
public static class SyntaxCandidates {
  /// <summary>Whether the declaration is partial, so other declarations of the same type may add to it.</summary>
  public static bool IsPartial(TypeDeclarationSyntax type) => type.Modifiers.Any(SyntaxKind.PartialKeyword);

  /// <summary>
  /// Whether the type can have a base type or implement an interface: the declaration has a base list, or is
  /// partial. A type with neither implements nothing and derives from nothing but <see cref="object"/>.
  /// </summary>
  public static bool MayHaveBaseTypes(TypeDeclarationSyntax type) =>
      type.BaseList is { Types.Count: > 0 } || IsPartial(type);

  /// <summary>Whether the type can carry an attribute: the declaration has one, or is partial.</summary>
  public static bool MayHaveAttributes(TypeDeclarationSyntax type) =>
      type.AttributeLists.Count > 0 || IsPartial(type);

  /// <summary>
  /// Whether the declaration itself declares an attributed property: a property, indexer or event with an
  /// attribute, or a primary-constructor parameter with one (a record's positional parameter is a property).
  /// </summary>
  public static bool DeclaresAttributedProperty(TypeDeclarationSyntax type) =>
      type.Members.OfType<BasePropertyDeclarationSyntax>().Any(static p => p.AttributeLists.Count > 0) ||
      (type.ParameterList?.Parameters.Any(static p => p.AttributeLists.Count > 0) ?? false);
}
