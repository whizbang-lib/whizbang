using System.Text.RegularExpressions;

namespace Whizbang.Data.EFCore.Postgres.Observability;

/// <summary>
/// Names the document fields a statement filters on, in a form the database's statement statistics
/// keep.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL records a statement with its constants replaced by placeholders, and a JSON key is a
/// constant: <c>data -&gt;&gt; 'Kind'</c> is recorded as <c>data -&gt;&gt; $1</c>, so the field the
/// filter read is not in what is kept. A comment is not a constant and survives the replacement
/// whole, so the fields are written into one before the statement is sent and read back out of it
/// afterwards.
/// </para>
/// <para>
/// The tag is derived from the statement, so the same query always carries the same tag, and two
/// queries that filter on different fields carry different ones -- which is what keeps them apart
/// in the statistics instead of collapsing them into one entry that names nothing.
/// </para>
/// </remarks>
internal static partial class DocumentFilterNaming {
  /// <summary>The documents a perspective stores, which are the only ones a filter can extract from.</summary>
  internal static readonly string[] Documents = ["data", "metadata", "scope"];

  /// <summary>Matches a document extraction, capturing the document and the field.</summary>
  [GeneratedRegex(@"\b(data|metadata|scope)\s*->>?\s*'([^']+)'", RegexOptions.IgnoreCase)]
  internal static partial Regex DocumentExtraction();

  /// <summary>Matches the tag this writes, capturing the fields it names.</summary>
  [GeneratedRegex(@"/\*\s*wh:f=([A-Za-z0-9_.,]+)\s*\*/")]
  internal static partial Regex FilterTag();

  /// <summary>Matches the perspective table a statement reads.</summary>
  [GeneratedRegex(@"\bwh_per_[a-z0-9_]+", RegexOptions.IgnoreCase)]
  internal static partial Regex PerspectiveTable();

  /// <summary>
  /// The statement with its document filters named in a leading comment, or unchanged where it has
  /// none.
  /// </summary>
  /// <param name="sql">The statement about to be sent.</param>
  /// <returns>The statement to send.</returns>
  internal static string Tagged(string sql) {
    // Cheap enough to run on every command: a statement that names no perspective table cannot be
    // one the advisory has anything to say about, and that is almost all of them.
    if (sql.Length == 0 || sql.IndexOf("wh_per_", StringComparison.OrdinalIgnoreCase) < 0) {
      return sql;
    }

    var named = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var (document, field) in _extractionsIn(sql)) {
      named.Add($"{document}.{field}");
    }

    return named.Count == 0 ? sql : $"/* wh:f={string.Join(",", named)} */ {sql}";
  }

  /// <summary>The document filters a recorded statement names.</summary>
  /// <remarks>
  /// The tag is preferred because it is what survives being recorded. The extraction is still read
  /// where there is no tag, which is the statement of a consumer who has not turned the naming on
  /// and any statement read from somewhere that keeps its constants.
  /// </remarks>
  /// <param name="statement">The recorded statement text.</param>
  /// <returns>The document and field of each filter named.</returns>
  internal static IEnumerable<(string Document, string Field)> FiltersIn(string statement) {
    var tag = FilterTag().Match(statement);

    return tag.Success ? _tagged(tag.Groups[1].Value) : _extractionsIn(statement);
  }

  private static IEnumerable<(string Document, string Field)> _tagged(string fields) {
    foreach (var name in fields.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
      var separator = name.IndexOf('.', StringComparison.Ordinal);
      if (separator > 0 && separator < name.Length - 1) {
        yield return (name[..separator].ToLowerInvariant(), name[(separator + 1)..]);
      }
    }
  }

  private static IEnumerable<(string Document, string Field)> _extractionsIn(string statement) =>
    DocumentExtraction().Matches(statement)
      .Select(match => (Document: match.Groups[1].Value.ToLowerInvariant(), Field: match.Groups[2].Value))
      .Where(extraction => Array.IndexOf(Documents, extraction.Document) >= 0);
}
