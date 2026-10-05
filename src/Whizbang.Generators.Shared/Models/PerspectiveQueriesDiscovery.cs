// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>What a model's <c>[PerspectiveQueries]</c> says about one kind of lookup.</summary>
public enum DocumentMatchDeclaration {
  /// <summary>Not declared, so the framework's default for that lookup applies.</summary>
  Undeclared = 0,

  /// <summary>Declared on: the index for the lookup is built.</summary>
  On = 1,

  /// <summary>Declared off: the index for the lookup is not built.</summary>
  Off = 2,
}

/// <summary>
/// Which document indexes a perspective model's queries need, as its <c>[PerspectiveQueries]</c>
/// declares them.
/// </summary>
/// <remarks>
/// This record uses value equality, which the incremental generator's caching depends on.
/// </remarks>
/// <param name="AnyField">What the model says about matching on any field of its document.</param>
/// <param name="Metadata">What the model says about matching on its metadata.</param>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDocumentIndexGenerationTests.cs</tests>
public sealed record DocumentMatching(DocumentMatchDeclaration AnyField, DocumentMatchDeclaration Metadata) {
  /// <summary>A model that declares nothing.</summary>
  public static DocumentMatching Undeclared { get; } =
    new(DocumentMatchDeclaration.Undeclared, DocumentMatchDeclaration.Undeclared);

  /// <summary>
  /// Whether the index over the whole model document is built. Only when declared on: it is the
  /// largest index on the table and every change to the document rewrites its entries, so it is a
  /// decision a model makes rather than a default it inherits. An index an earlier release built is
  /// never dropped for an undeclared model; that stays an operator step.
  /// </summary>
  public bool BuildsDataIndex => AnyField == DocumentMatchDeclaration.On;

  /// <summary>Whether the index over the metadata document is built. Only when declared on.</summary>
  public bool BuildsMetadataIndex => Metadata == DocumentMatchDeclaration.On;
}

/// <summary>
/// Reads <c>[PerspectiveQueries]</c> from a model, the one reading every generator and analyzer
/// shares, so the index the schema builds and the diagnostic that checks a query agree.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDocumentIndexGenerationTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/DocumentMatchIndexAnalyzerTests.cs</tests>
public static class PerspectiveQueriesDiscovery {
  /// <summary>The attribute's full name.</summary>
  public const string ATTRIBUTE = "Whizbang.Core.Perspectives.PerspectiveQueriesAttribute";

  /// <summary>The named argument for matching on any field.</summary>
  public const string MATCH_ON_ANY_FIELD = "MatchOnAnyField";

  /// <summary>The named argument for matching on metadata.</summary>
  public const string MATCH_ON_METADATA = "MatchOnMetadata";

  // Diagnostic IDs: WHIZ300-399 reserved for perspective validation.
  private const string CATEGORY = "Whizbang.PerspectiveValidation";

  /// <summary>
  /// WHIZ307: Warning - a filter compiles to a whole-document match that the model's declaration
  /// leaves without an index.
  /// </summary>
  /// <remarks>
  /// Here rather than in an analyzer because two report it: the filter analyzer for a filter written
  /// in source, and the query-exposure analyzer for filters a request composes, which no source shows.
  /// </remarks>
  public static readonly DiagnosticDescriptor WholeDocumentMatchHasNoIndex = new(
      id: "WHIZ307",
      title: "Whole-document match has no index",
      messageFormat: "{0} compiles to a whole-document match that no index answers, because {1}, so the database reads every row of the perspective. {2}.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "An equality or set-membership filter on a field with no index of its own is compiled into a " +
                   "whole-document match, and only the index over the whole document answers it. [PerspectiveQueries] on " +
                   "the model says which of those indexes the schema builds: MatchOnAnyField = false leaves the model's " +
                   "document without one, and the metadata document has one only with MatchOnMetadata = true. A filter " +
                   "that needs a missing index is a sequential scan that returns correct rows, which is why it is reported " +
                   "here rather than found in production. Mark the field [Indexed], declare the lookup, or record a " +
                   "deliberate scan with [SuppressIndexAdvisory(\"reason\")]."
  );

  /// <summary>
  /// WHIZ308: Warning - a filter compiles to a whole-document match on a model that has not declared
  /// whether its queries match on any field, and so does not get the index that answers it.
  /// </summary>
  /// <remarks>
  /// Separate from WHIZ307 because the fix differs: nothing was decided here, so declaring the lookup
  /// is as good an answer as indexing the field, and the message offers both.
  /// </remarks>
  public static readonly DiagnosticDescriptor WholeDocumentMatchReliesOnDefault = new(
      id: "WHIZ308",
      title: "Whole-document match has no index by default",
      messageFormat: "{0} compiles to a whole-document match, and the index over the whole document is not built for '{1}', which does not declare [PerspectiveQueries(MatchOnAnyField = ...)], so the database reads every row of the perspective. Declare [PerspectiveQueries(MatchOnAnyField = true)] to build that index{2}.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "A model that does not declare MatchOnAnyField does not get the index over its whole document: it is " +
                   "the largest index on the table and every change rewrites its entries, so it is built only when a model " +
                   "asks for it. An equality or set-membership filter on a field with no index of its own compiles to a " +
                   "whole-document match that only that index answers, so on a new database it reads every row. Declare " +
                   "MatchOnAnyField = true to build the index, mark the field [Indexed] for an index of its own, or record a " +
                   "deliberate scan with [SuppressIndexAdvisory(\"reason\")]. A database an earlier release created keeps " +
                   "the index it already has: nothing is dropped automatically."
  );

  /// <summary>
  /// The model's declaration, from the model itself or else the nearest base that carries one.
  /// </summary>
  /// <param name="model">The perspective's model type, or null.</param>
  /// <returns>The declaration, or <see cref="DocumentMatching.Undeclared"/>.</returns>
  /// <remarks>
  /// The attribute is inherited, as the composite index declaration is, because one base commonly
  /// carries the fields of many models. The nearest declaration wins whole rather than property by
  /// property: a model that writes the attribute has said what it wants, and merging it with a base
  /// would make one line of source mean different things depending on a file elsewhere.
  /// </remarks>
  public static DocumentMatching From(INamedTypeSymbol? model) {
    for (var type = model; type is not null; type = type.BaseType) {
      var attribute = type.GetAttributes().FirstOrDefault(static a => TypeNameUtilities.IsNamed(a.AttributeClass, ATTRIBUTE));
      if (attribute is not null) {
        return new DocumentMatching(_declared(attribute, MATCH_ON_ANY_FIELD), _declared(attribute, MATCH_ON_METADATA));
      }
    }

    return DocumentMatching.Undeclared;
  }

  private static DocumentMatchDeclaration _declared(AttributeData attribute, string name) {
    foreach (var argument in attribute.NamedArguments) {
      // Both properties are bool, so the value is one of the two constants whenever the name matches.
      if (string.Equals(argument.Key, name, StringComparison.Ordinal)) {
        return argument.Value.Value is true ? DocumentMatchDeclaration.On : DocumentMatchDeclaration.Off;
      }
    }

    return DocumentMatchDeclaration.Undeclared;
  }
}
