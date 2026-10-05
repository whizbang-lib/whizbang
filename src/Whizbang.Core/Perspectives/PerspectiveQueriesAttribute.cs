// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Says which kinds of lookup the queries against a perspective make, so the schema builds the
/// indexes those lookups need and no others. Applied to the model class, like
/// <see cref="PerspectiveStorageAttribute"/> and <see cref="PerspectiveIndexAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// A perspective stores its model as one JSON document. An equality filter on a field that has no
/// index of its own, <c>Where(r =&gt; r.Data.Status == status)</c>, is compiled into a whole-document
/// match, and the one index that answers a whole-document match covers every field of every row.
/// It is the largest index on the table, and every change to the document rewrites its entries, so
/// a perspective whose queries never match that way pays for it on every write and gets nothing
/// back.
/// </para>
/// <para>
/// The properties here name what a query does rather than which index is built, because the
/// question a developer can answer is "do my queries match on any field?", not "do I need an
/// inverted index over the document?".
/// </para>
/// <para>
/// <strong>Nothing is ever dropped for you.</strong> Turning a lookup off stops the schema pass
/// from creating its index. An index an earlier release already built stays where it is until an
/// operator drops it, because removing an index a production query relies on is worse than keeping
/// one nobody reads. The operator step is documented with the SQL to run.
/// </para>
/// <para>
/// Build-time diagnostics keep the declaration honest. WHIZ307 warns when a query compiles to a
/// match that this declaration leaves without an index, and WHIZ308 warns at the same match on a
/// model that leaves <see cref="MatchOnAnyField"/> undeclared, which does not get the index either.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Queries filter on declared indexes only, so the whole-document index is not built.
/// [PerspectiveQueries(MatchOnAnyField = false)]
/// public record OrderSummary {
///   [Indexed]
///   public string Status { get; init; } = "";
/// }
///
/// // Queries match on any field, and one of them filters on the event that last wrote the row.
/// [PerspectiveQueries(MatchOnAnyField = true, MatchOnMetadata = true)]
/// public record AuditTrail {
///   public string Actor { get; init; } = "";
/// }
/// </code>
/// </example>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveQueriesAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveDocumentIndexGenerationTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/Analyzers/DocumentMatchIndexAnalyzerTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class PerspectiveQueriesAttribute : Attribute {
  /// <summary>
  /// Whether queries match on any field of the document: an equality filter on a field that has no
  /// index of its own.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>true</c> builds the index over the whole document, which answers every such filter.
  /// <c>false</c> does not build it; declare <see cref="IndexedAttribute"/> on each field a query
  /// filters on instead, which is smaller and faster for the fields that matter.
  /// </para>
  /// <para>
  /// <strong>Left undeclared, the index is not built.</strong> Earlier releases built it for every
  /// perspective; a new database no longer gets it unless the model declares <c>true</c>. A database
  /// that already has it keeps it, because nothing drops an index for you. WHIZ308 points at each
  /// query that needs it, which is the list to check: declare <c>true</c>, or index those fields.
  /// </para>
  /// </remarks>
  public bool MatchOnAnyField { get; init; }

  /// <summary>
  /// Whether queries match on the row's metadata, for example
  /// <c>Where(r =&gt; r.Metadata.EventType == name)</c>.
  /// </summary>
  /// <remarks>
  /// Off unless declared. The metadata document records which event last wrote the row, and the
  /// framework itself only ever reads it one row at a time by key, so an index over it is rarely
  /// read and always written. WHIZ307 warns at a query that matches on metadata while this is off.
  /// </remarks>
  public bool MatchOnMetadata { get; init; }
}
