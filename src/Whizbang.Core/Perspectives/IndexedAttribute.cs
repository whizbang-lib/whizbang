namespace Whizbang.Core.Perspectives;

/// <summary>
/// The kinds of index a JSON-only field can carry. Combinable, because a field filtered by a range
/// and by a substring wants both.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Flags]
public enum IndexKind {
  /// <summary>No index. Present so that an explicit "not indexed" can be written down.</summary>
  None = 0,

  /// <summary>
  /// A btree over the extracted value, which answers equality, ranges, ordering and null tests.
  /// </summary>
  /// <remarks>
  /// This is what the GIN index on the document cannot do. Containment answers "is this value
  /// present" and has no ordered answer space, so a range or a sort key is read by scanning however
  /// the document is indexed. A btree on the extraction answers all of it.
  /// </remarks>
  Btree = 1,

  /// <summary>
  /// A trigram index, which answers substring matching: <c>Contains</c>, <c>StartsWith</c> and
  /// <c>EndsWith</c>.
  /// </summary>
  /// <remarks>
  /// Requires the <c>pg_trgm</c> extension. Only meaningful for a string field, and reported as a
  /// diagnostic rather than silently ignored when asked for on anything else.
  /// </remarks>
  Trigram = 2,
}

/// <summary>
/// Marks a JSON-only property to carry its own index, without promoting it to a physical column.
/// </summary>
/// <remarks>
/// <para>
/// There are three tiers of storage for a perspective field and this is the middle one. Undeclared,
/// a field lives in the document and an equality filter on it is answered from the GIN index while a
/// range or an ordering is read by scanning. Declared here, the extraction carries its own index, so
/// every operation is answered from one, and the cost is an index rather than a schema change.
/// Promoted with <see cref="PhysicalFieldAttribute"/>, it becomes a real column, which additionally
/// buys constraints and foreign keys and costs a column and a hydration path.
/// </para>
/// <para>
/// <strong>An indexed field is no longer compiled into a containment test.</strong> Rewriting its
/// equality filter to <c>data @&gt; …</c> would send the planner to the document index and leave this
/// one unused, which is the situation the rewrite exists to fix rather than to cause. A single-column
/// btree equality probe is also cheaper than containment plus its recheck, so the filter is better
/// off either way.
/// </para>
/// <para>
/// <strong>Universal.</strong> It says what the author means, and that is the same wherever the field
/// lives: this field is filtered, make it fast. Where the index goes follows from whether the field
/// was promoted, which the framework already knows. On a field held in the document it builds an
/// index over the extraction a query produces; on a field promoted by <c>[PhysicalField]</c> it
/// indexes the column. Use both together when you need a real column and an index on it.
/// </para>
/// <para>
/// A promoted column is what you need for a constraint, a foreign key or uniqueness. An index over
/// the document needs no schema change and no write-path column, and can offer none of those. That
/// distinction belongs to <c>[PhysicalField]</c> rather than here, which is why this attribute does
/// not mention either storage.
/// </para>
/// <para>
/// Not every type can carry one. An index has to be built from an immutable expression, and the cast
/// out of a document is immutable for text, the integer family, numerics, booleans, identifiers and
/// the date family, whose stored form is a number. Asking for an index a field cannot carry, or one
/// its model's storage puts out of reach, is reported at build time rather than silently skipped.
/// </para>
/// <para>
/// <see cref="IndexKind.None"/> declines an index, which is how one field opts out of
/// <see cref="IndexAllFieldsAttribute"/>. Declining is not declaring, so it is not reported as a
/// claim the framework cannot honor.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/IndexedAttributeTests.cs</tests>
/// <example>
/// <code>
/// public record OrderModel {
///   [StreamId]
///   public Guid OrderId { get; init; }
///
///   // Filtered by range and sorted on: a btree over the extraction answers both.
///   [Indexed]
///   public int Rank { get; init; }
///
///   // Filtered both by range and by substring.
///   [Indexed(IndexKind.Btree | IndexKind.Trigram)]
///   public string Title { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class IndexedAttribute : Attribute {
  /// <summary>Declares an index of the given kinds over this field's extraction.</summary>
  /// <param name="kind">The kinds to create. Defaults to <see cref="IndexKind.Btree"/>.</param>
  public IndexedAttribute(IndexKind kind = IndexKind.Btree) => Kind = kind;

  /// <summary>The kinds of index to create over this field.</summary>
  public IndexKind Kind { get; }
}

/// <summary>
/// Marks a perspective model so that every eligible JSON-only field carries an index, without a
/// declaration per property.
/// </summary>
/// <remarks>
/// <para>
/// For a read model that is genuinely queried every way, naming each field is noise. This says it
/// once. A field that also carries <see cref="IndexedAttribute"/> uses that field's kinds
/// instead, so an exception to the rule stays local to the property it applies to.
/// </para>
/// <para>
/// The cost is real and worth stating: every index is write amplification on each apply and disk that
/// has to be kept warm. A perspective with many fields that are never filtered is better served by
/// declaring the few that are. The index advisory names them, so the choice can be made from what
/// the queries actually do rather than from a guess.
/// </para>
/// <para>
/// Only fields whose extraction can carry an index are included. The rest are skipped silently here
/// rather than reported, because a blanket declaration is not a claim about any particular field.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/IndexedAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class IndexAllFieldsAttribute : Attribute {
  /// <summary>Declares an index of the given kinds over every eligible field.</summary>
  /// <param name="kind">The kinds to create. Defaults to <see cref="IndexKind.Btree"/>.</param>
  public IndexAllFieldsAttribute(IndexKind kind = IndexKind.Btree) => Kind = kind;

  /// <summary>The kinds of index to create over every eligible field.</summary>
  public IndexKind Kind { get; }
}
