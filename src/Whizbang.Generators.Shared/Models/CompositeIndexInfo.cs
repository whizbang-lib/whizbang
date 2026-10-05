// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// One element of a composite index: the SQL the index is built over for one property.
/// </summary>
/// <remarks>
/// A property may be promoted to a real column or live in the document, and the index is built over
/// whichever it is. Resolving that at discovery keeps the decision in one place and leaves the SQL
/// renderer with nothing to decide.
/// </remarks>
/// <param name="PropertyName">The property's name on the model, for naming and diagnostics.</param>
/// <param name="Element">The SQL for this element: a column name, or an extraction expression.</param>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public sealed record CompositeIndexElement(string PropertyName, string Element);

/// <summary>
/// A declared index over more than one property, or over one property with a predicate.
/// </summary>
/// <remarks>
/// This record uses value equality, which the incremental generator's caching depends on.
/// ImmutableArray gives the element collection value equality too; a plain array would make every
/// rebuild look like a change.
/// </remarks>
/// <param name="Elements">The covered properties, leading one first.</param>
/// <param name="Name">The declared name, or null to derive one.</param>
/// <param name="Where">The partial predicate, verbatim, or null for every row.</param>
/// <param name="Unique">Whether the index enforces uniqueness.</param>
/// <param name="Method">The index method, or null for the default btree.</param>
/// <param name="OperatorClass">The operator class applied to each element, or null for the default.</param>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/CompositeIndexGenerationTests.cs</tests>
public sealed record CompositeIndexInfo(
    ImmutableArray<CompositeIndexElement> Elements,
    string? Name = null,
    string? Where = null,
    bool Unique = false,
    string? Method = null,
    string? OperatorClass = null
);

/// <summary>
/// Renders the SQL a composite or partial index is created from.
/// </summary>
/// <remarks>
/// Kept beside <see cref="JsonIndexSql"/> rather than inside a generator, for the same reason: the
/// statement a test asserts is the statement the generator emits.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/CompositeIndexGenerationTests.cs</tests>
public static class CompositeIndexSql {
  /// <summary>
  /// The index's name: the declared one, or one derived from the table and the properties.
  /// </summary>
  /// <remarks>
  /// A derived name includes the properties in their declared order, so reordering them is a
  /// different index rather than the same one silently left alone by CREATE INDEX IF NOT EXISTS --
  /// which matters because the order is what a composite index means.
  /// </remarks>
  public static string Name(CompositeIndexInfo index, string indexPrefix) {
    if (index is null) {
      return string.Empty;
    }

    if (!string.IsNullOrWhiteSpace(index.Name)) {
      return index.Name!;
    }

    var parts = index.Elements.Select(static e => e.PropertyName.ToLowerInvariant());
    var derived = $"idx_{indexPrefix}_{string.Join("_", parts)}{(index.Where is null ? "" : "_partial")}";

    return _withinIdentifierLimit(derived);
  }

  /// <summary>
  /// <paramref name="name"/> shortened to fit an identifier, with a stable suffix when it had to be.
  /// </summary>
  /// <remarks>
  /// A composite's derived name concatenates every property it covers, so it reaches the limit far
  /// sooner than a single-property name does. PostgreSQL does not refuse an over-long identifier --
  /// it truncates it -- so two composites over different properties with a long shared prefix would
  /// arrive as one name, and <c>CREATE INDEX IF NOT EXISTS</c> would quietly skip the second. The
  /// scheme is <see cref="PostgresIdentifiers.WithinLimit"/>, shared with every other index name the
  /// schema derives.
  /// </remarks>
  private static string _withinIdentifierLimit(string name) => PostgresIdentifiers.WithinLimit(name);

  /// <summary>The statement creating this index, idempotent so the schema pass can run every start.</summary>
  /// <param name="index">The declaration.</param>
  /// <param name="qualifiedTable">The table, schema-qualified.</param>
  /// <param name="indexPrefix">A prefix making derived names unique to the table.</param>
  public static string CreateStatement(
      CompositeIndexInfo index, string qualifiedTable, string indexPrefix) {
    if (index?.Elements.IsDefaultOrEmpty != false) {
      return string.Empty;
    }

    var unique = index.Unique ? "UNIQUE " : string.Empty;

    // The operator class follows the element it applies to, and applies to every one: a per-element
    // class would need a second list kept in step with the first, which is a worse thing to get
    // wrong than it is to express.
    var opClass = string.IsNullOrWhiteSpace(index.OperatorClass) ? string.Empty : " " + index.OperatorClass;
    var columns = string.Join(", ", index.Elements.Select(e => e.Element + opClass));

    // Omitted rather than written out for btree, so that the statement for an index that never asked
    // for a method is the statement it has always been.
    var using_ = string.IsNullOrWhiteSpace(index.Method) ? string.Empty : $" USING {index.Method}";
    var where = string.IsNullOrWhiteSpace(index.Where) ? string.Empty : $" WHERE {index.Where}";

    return $"CREATE {unique}INDEX IF NOT EXISTS {Name(index, indexPrefix)} "
         + $"ON {qualifiedTable}{using_} ({columns}){where};";
  }
}
