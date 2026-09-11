using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The store type a JSON-only field's extraction is cast to before it can be indexed.
/// </summary>
/// <remarks>
/// <para>
/// Two constraints decide this list and both are measured rather than assumed. An index has to be
/// built from an immutable expression, and the cast out of text is immutable for these types and
/// stable for a timestamp or a date, which PostgreSQL refuses to index at all. And the expression has
/// to be the one the query produces, character for character in effect, because an index on a
/// different expression is simply a different index and the planner will not use it.
/// </para>
/// <para>
/// That second constraint is why these mirror Entity Framework's casts rather than being chosen for
/// tidiness. An <c>int</c> is extracted as <c>integer</c> and not as <c>bigint</c>, even though
/// either would hold the value, because an index on the wrong one of those two goes unused.
/// </para>
/// <para>
/// Named after PostgreSQL's own type names rather than the CLR ones they come from, because the
/// mapping is not one to one: a byte and a short both reach <c>int2</c>, and an enumeration reaches
/// whichever integer its underlying type does.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/Models/JsonIndexInfoTests.cs</tests>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
  Justification = "These are PostgreSQL's own type names, which is the point: the member has to "
    + "say which store type the index is built over, and int2, int4 and int8 are how PostgreSQL "
    + "spells those. A CLR-flavoured name would be actively misleading, since the mapping is not "
    + "one to one.")]
public enum JsonIndexCast {
  /// <summary>
  /// No cast. The extraction is already text, and the query compares it without one, so the index
  /// must not add one either.
  /// </summary>
  None = 0,

  /// <summary>A <c>smallint</c>, for a short or a byte.</summary>
  Int2 = 1,

  /// <summary>An <c>integer</c>, for an int and for an enumeration over one.</summary>
  Int4 = 2,

  /// <summary>A <c>bigint</c>, for a long and for an enumeration over an unsigned number.</summary>
  Int8 = 3,

  /// <summary>A <c>numeric</c>, for a decimal.</summary>
  Numeric = 4,

  /// <summary>A <c>real</c>, for a single-precision float.</summary>
  Float4 = 5,

  /// <summary>A <c>double precision</c>, for a double.</summary>
  Float8 = 6,

  /// <summary>A <c>boolean</c>.</summary>
  Bool = 7,

  /// <summary>A <c>uuid</c>, for an identifier.</summary>
  Uuid = 8,
}

/// <summary>
/// A JSON-only field declared to carry its own index, and what the index is built over.
/// </summary>
/// <remarks>
/// This record uses value equality, which the incremental generator's caching depends on.
/// </remarks>
/// <param name="PropertyName">The property's name on the model.</param>
/// <param name="JsonKey">The key the value is stored under in the document.</param>
/// <param name="Cast">The store type the extraction is cast to, or none for text.</param>
/// <param name="Btree">Whether to create a btree over the extraction.</param>
/// <param name="Trigram">Whether to create a trigram index for substring matching.</param>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/Models/JsonIndexInfoTests.cs</tests>
public sealed record JsonIndexInfo(
    string PropertyName,
    string JsonKey,
    JsonIndexCast Cast,
    bool Btree,
    bool Trigram
);

/// <summary>
/// Renders the SQL an index over a JSON-only field is built from.
/// </summary>
/// <remarks>
/// Kept next to the cast list rather than inside the generator so the expression a test asserts is
/// the expression the generator emits, and so the one place that has to agree with the query
/// translation is a single function.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/Models/JsonIndexInfoTests.cs</tests>
public static class JsonIndexSql {
  /// <summary>The SQL name of a cast target, or null when no cast is applied.</summary>
  public static string? StoreType(JsonIndexCast cast) => cast switch {
    JsonIndexCast.None => null,
    JsonIndexCast.Int2 => "smallint",
    JsonIndexCast.Int4 => "integer",
    JsonIndexCast.Int8 => "bigint",
    JsonIndexCast.Numeric => "numeric",
    JsonIndexCast.Float4 => "real",
    JsonIndexCast.Float8 => "double precision",
    JsonIndexCast.Bool => "boolean",
    JsonIndexCast.Uuid => "uuid",
    _ => null,
  };

  /// <summary>
  /// The index element for a field, for example <c>((data -&gt;&gt; 'Rank')::integer)</c>.
  /// </summary>
  /// <param name="column">The document column, ordinarily <c>data</c>.</param>
  /// <param name="jsonKey">The key the field is stored under.</param>
  /// <param name="cast">The store type to cast the extraction to.</param>
  /// <returns>One index element, parenthesized.</returns>
  /// <remarks>
  /// <para>
  /// <strong>The caller adds the column-list parentheses:</strong> write
  /// <c>CREATE INDEX … ON tbl (&lt;element&gt;)</c>, which yields
  /// <c>ON tbl (((data -&gt;&gt; 'Rank')::integer))</c>. The doubling is not decoration. PostgreSQL's
  /// grammar admits an expression element only as a parenthesized expression, so a cast applied
  /// outside those parentheses is a syntax error rather than an index.
  /// </para>
  /// <para>
  /// No collation is applied to the text form. A range over text is answered only by an index in the
  /// same collation as the comparison, and the query carries no collation clause, so adding one here
  /// would build an index the planner cannot use for exactly the queries it exists to serve.
  /// </para>
  /// </remarks>
  public static string Expression(string column, string jsonKey, JsonIndexCast cast) {
    var extraction = $"({column} ->> '{jsonKey}')";
    var storeType = StoreType(cast);

    return storeType is null ? extraction : $"({extraction}::{storeType})";
  }
}
