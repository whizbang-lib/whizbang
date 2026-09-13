namespace Whizbang.Core.Perspectives;

/// <summary>
/// What a field needs an index to answer. Combinable, because a field filtered by a range and by a
/// substring needs both.
/// </summary>
/// <remarks>
/// <para>
/// These name the question a query asks, not the index a database builds to answer it. That is the
/// difference between a portable declaration and one that only means something on one engine: a
/// model is ordinary code and the same model is stored in PostgreSQL in production and in SQLite
/// under test, so what the author writes has to survive the move. Which structure gets built is the
/// driver's answer, and the drivers do not agree: an ordered lookup is a btree on PostgreSQL, SQLite
/// and MySQL, a nonclustered index on SQL Server; substring matching is a trigram GIN index on
/// PostgreSQL, a full-text index on SQL Server and MySQL, and on SQLite it is not an index on the
/// table at all.
/// </para>
/// <para>
/// A driver that cannot provide a capability says so at build time. Silently building nothing is
/// the one outcome worth ruling out, because the declaration reads as a claim either way.
/// </para>
/// <para>
/// Plural, as a combinable enumeration is named. A single capability is the common case and reads
/// fine that way, <c>[Indexed(IndexKinds.Substring)]</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Flags]
public enum IndexKinds {
  /// <summary>No index. Present so that an explicit "not indexed" can be written down.</summary>
  None = 0,

  /// <summary>
  /// Answers equality, ranges, ordering and null tests over the stored value.
  /// </summary>
  /// <remarks>
  /// This is what the index over the whole document cannot do. Containment answers "is this value
  /// present" and has no ordered answer space, so a range or a sort key is read by scanning however
  /// the document is indexed. An ordered index over the extracted value answers all of it.
  /// </remarks>
  Ordered = 1,

  /// <summary>
  /// Answers substring matching: <c>Contains</c>, <c>StartsWith</c> and <c>EndsWith</c>.
  /// </summary>
  /// <remarks>
  /// Only meaningful for a text field, and reported rather than silently ignored when asked for on
  /// anything else. Not every driver can provide it, and a driver that cannot reports that too.
  /// </remarks>
  Substring = 2,
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
/// <see cref="IndexKinds.None"/> declines an index, which is how one field opts out of
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
///   [Indexed(IndexKinds.Ordered | IndexKinds.Substring)]
///   public string Title { get; init; }
/// }
/// </code>
/// </example>
/// <param name="kind">What the index has to answer. Defaults to <see cref="IndexKinds.Ordered"/>.</param>
/// <param name="caseInsensitive">
/// Whether the comparison this serves folds case. See <see cref="IndexedAttribute.CaseInsensitive"/>.
/// </param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class IndexedAttribute(IndexKinds kind = IndexKinds.Ordered, bool caseInsensitive = false)
    : Attribute {
  /// <summary>What the index over this field has to answer.</summary>
  public IndexKinds Kind { get; } = kind;

  /// <summary>
  /// Whether the comparison this index serves folds case, as <c>Name.ToLower() == …</c> does.
  /// </summary>
  /// <remarks>
  /// <para>
  /// It has to be declared rather than inferred, because an index is only used for the expression it
  /// was built over. A case-folded comparison is a predicate over the folded value, so an index over
  /// the unfolded one is not a candidate for it however it is built: the declaration would be paid
  /// for on every write and answer nothing.
  /// </para>
  /// <para>
  /// Costs an index that a comparison respecting case cannot use, which is why it is a separate
  /// declaration rather than a default. A field compared both ways needs one of each.
  /// </para>
  /// <para>
  /// <strong>Write the comparison as <c>ToLower()</c>, with no argument.</strong> That is the one
  /// form the query translation maps, to the database's own downward fold, and it is what this index
  /// is built over. <c>ToLowerInvariant()</c> and the overloads taking a culture have no translation
  /// at all, so a query using them fails rather than running slowly. <c>ToUpper()</c> does translate,
  /// to the upward fold, which no declaration builds an index over; the index advisory reports that
  /// and names the fold to use instead.
  /// </para>
  /// <para>
  /// The fold happens in the database, under the column's collation, so no CLR culture is involved
  /// and none can be expressed. Analyzers that ask for a culture or for a
  /// <c>StringComparison</c> on such a comparison are answering a question about in-process string
  /// handling; inside a query expression the comparison becomes SQL, and those overloads are exactly
  /// the ones with no translation. Suppress them on the query rather than taking their advice.
  /// </para>
  /// <para>
  /// Only meaningful for a text field, and reported rather than ignored when asked for elsewhere.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Declared both ways, because both comparisons are made.
  /// [Indexed]
  /// [Indexed(caseInsensitive: true)]
  /// public string Label { get; init; }
  ///
  /// // The folded comparison. ToLower() with no argument is the form that translates.
  /// rows.Where(r =&gt; r.Data.Label.ToLower() == term.ToLower())
  /// </code>
  /// </example>
  public bool CaseInsensitive { get; } = caseInsensitive;
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
/// <param name="kind">What each index has to answer. Defaults to <see cref="IndexKinds.Ordered"/>.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class IndexAllFieldsAttribute(IndexKinds kind = IndexKinds.Ordered) : Attribute {
  /// <summary>What the index over every eligible field has to answer.</summary>
  public IndexKinds Kind { get; } = kind;
}
