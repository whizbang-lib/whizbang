namespace Whizbang.Core.Perspectives;

/// <summary>
/// Declares an index over more than one of a perspective model's properties, optionally covering
/// only a subset of its rows.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IndexedAttribute"/> declares an index over one property, which is what most filters
/// need. Two shapes it cannot express are declared here: an index over several properties in a
/// stated order, for a query that filters them together, and a partial index, for the common case
/// where only a subset of rows is ever queried.
/// </para>
/// <para>
/// <strong>Properties are named, not expressions.</strong> A property may be promoted to a column
/// by <see cref="PhysicalFieldAttribute"/> or live in the document, and the index is built over
/// whichever it is -- the same storage-agnostic promise <see cref="IndexedAttribute"/> makes. A
/// name is resolved against the model, so a raw expression is not needed to say where a value
/// lives.
/// </para>
/// <para>
/// A name that matches no property, or one whose type cannot carry an index, means the index is not
/// created. It is not yet reported as a build diagnostic, so check the declaration when an index
/// you expected is absent.
/// </para>
/// <para>
/// <strong>Order matters and is the order given.</strong> A composite index answers a filter on a
/// leading subset of its properties, so the most selective, or the one filtered on alone, goes
/// first. Postgres cannot use it for a filter that skips the leading property.
/// </para>
/// <para>
/// The attribute may be applied more than once, for a model needing several.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Filtered together, so one index rather than two
/// [PerspectiveIndex(nameof(TenantId), nameof(EntityType))]
/// // Only active rows are ever queried, so only they are indexed
/// [PerspectiveIndex(nameof(TenantId), Where = "(data ->> 'Status') = 'active'")]
/// public record DocumentModel {
///   public Guid TenantId { get; init; }
///   public string EntityType { get; init; } = "";
///   public string Status { get; init; } = "";
/// }
/// </code>
/// </example>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveIndexAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class PerspectiveIndexAttribute : Attribute {
  /// <summary>Declares the index over <paramref name="properties"/>, in that order.</summary>
  /// <param name="properties">
  /// The model properties the index covers, leading property first. One is allowed: an index over a
  /// single property with a <see cref="Where"/> predicate is a partial index, which
  /// <see cref="IndexedAttribute"/> cannot express.
  /// </param>
  public PerspectiveIndexAttribute(params string[] properties) =>
    Properties = properties ?? [];

  /// <summary>The model properties the index covers, leading property first.</summary>
  public string[] Properties { get; }

  /// <summary>
  /// The index name, or null to derive one from the table and the properties.
  /// </summary>
  /// <remarks>
  /// A derived name is stable for a given set of properties in a given order, which is what keeps
  /// the schema pass idempotent. Name it explicitly only when something outside the model refers to
  /// it, and remember PostgreSQL's identifier length limit applies.
  /// </remarks>
  public string? Name { get; init; }

  /// <summary>
  /// A predicate restricting which rows the index covers, or null to index every row.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Verbatim SQL, and the one part of this attribute that is not checked. It has to be written
  /// against the STORED form rather than against the model: a property in the document is reached
  /// as <c>(data -&gt;&gt; 'Name')</c>, and a promoted one by its column name. Nothing here can
  /// validate that, because the set of predicates a server will accept is open.
  /// </para>
  /// <para>
  /// PostgreSQL only uses a partial index for a query it can prove is restricted to the same rows,
  /// and it reasons about that on the TEXT of the predicate rather than by evaluating it. So the
  /// predicate has to be written the way the query writes its filter -- the same operator and the
  /// same constant -- or the index is built and never used.
  /// </para>
  /// </remarks>
  public string? Where { get; init; }

  /// <summary>Whether the index enforces uniqueness across the covered properties.</summary>
  /// <remarks>
  /// A unique index over a document extraction constrains what the perspective may hold, and a
  /// projection that violates it fails the apply rather than the query. Worth it for a genuine
  /// invariant, and worth avoiding otherwise.
  /// </remarks>
  public bool Unique { get; init; }
}
