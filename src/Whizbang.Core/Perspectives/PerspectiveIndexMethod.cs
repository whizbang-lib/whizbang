namespace Whizbang.Core.Perspectives;

/// <summary>
/// The index method a perspective index is built with.
/// </summary>
/// <remarks>
/// Named rather than taken as a string so that a typo is a build error, and kept to the methods
/// PostgreSQL ships. An extension's own method is not expressible here, which is deliberate: it
/// would be unverifiable at build time and is rare enough to belong in an application-owned object
/// instead.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public enum PerspectiveIndexMethod {
  /// <summary>Equality, ranges and ordering. The default, and what almost every filter wants.</summary>
  Btree = 0,

  /// <summary>Equality alone, in less space than btree. No ordering and no ranges.</summary>
  Hash,

  /// <summary>Containment and key existence over a document or an array.</summary>
  Gin,

  /// <summary>Overlap and nearness, for geometric types and ranges.</summary>
  Gist,

  /// <summary>Partitioned lookup for types with no natural linear order.</summary>
  SpGist,

  /// <summary>Block-range summaries, for a very large table already stored in the filtered order.</summary>
  Brin,
}
