using System.Collections.Concurrent;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Which JSON-only fields carry an index of their own, populated by generated code at startup.
/// </summary>
/// <remarks>
/// <para>
/// The query translation needs this for one decision: whether to compile an equality filter on a
/// field into a containment test. A field with a btree over its own extraction must keep the
/// extraction form, because rewriting it sends the planner to the document index and leaves the
/// field's index unused. That is the situation the containment rewrite exists to fix, so causing it
/// would be worse than doing nothing.
/// </para>
/// <para>
/// Standing down is also the faster choice. A single-column btree equality probe reads one index and
/// goes to the heap, while containment reads the document index and then rechecks each candidate row,
/// because the default operator class stores keys and values as separate tokens and cannot confirm on
/// its own that a key and a value belong together.
/// </para>
/// <para>
/// Registration is additive, so a repeated declaration on one property combines its kinds rather than
/// replacing them. Thread-safe for concurrent registration and lookup, and shaped for startup
/// initialization by generated code, which mirrors <see cref="PhysicalFieldRegistry"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/JsonIndexStandDownTests.cs</tests>
public static class JsonIndexRegistry {
  private static readonly ConcurrentDictionary<(Type ModelType, string PropertyName), IndexKind> _kinds = new();

  /// <summary>Records that a model's property carries an index of the given kinds.</summary>
  /// <typeparam name="TModel">The model holding the property.</typeparam>
  /// <param name="propertyName">The property's name, for example <c>Rank</c>.</param>
  /// <param name="kind">The kinds of index declared.</param>
  public static void Register<TModel>(string propertyName, IndexKind kind) =>
    Register(typeof(TModel), propertyName, kind);

  /// <summary>Records that a model's property carries an index of the given kinds.</summary>
  /// <param name="modelType">The model holding the property.</param>
  /// <param name="propertyName">The property's name.</param>
  /// <param name="kind">The kinds of index declared.</param>
  /// <remarks>
  /// Additive: declaring a btree and then a trigram for one property leaves it carrying both, which is
  /// what lets the attribute be written more than once instead of demanding a combination.
  /// </remarks>
  public static void Register(Type modelType, string propertyName, IndexKind kind) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

    _kinds.AddOrUpdate((modelType, propertyName), kind, (_, existing) => existing | kind);
  }

  /// <summary>The kinds of index a property carries, or none when it carries no index.</summary>
  /// <param name="modelType">The model holding the property.</param>
  /// <param name="propertyName">The property's name.</param>
  /// <returns>The declared kinds.</returns>
  public static IndexKind Kinds(Type modelType, string propertyName) {
    ArgumentNullException.ThrowIfNull(modelType);

    return _kinds.TryGetValue((modelType, propertyName), out var kind) ? kind : IndexKind.None;
  }

  /// <summary>
  /// Whether a property carries a btree over its extraction, which is the one kind that answers an
  /// equality filter and therefore the one that makes the containment rewrite stand down.
  /// </summary>
  /// <param name="modelType">The model holding the property.</param>
  /// <param name="propertyName">The property's name.</param>
  /// <returns>True when a btree is declared for it.</returns>
  /// <remarks>
  /// A trigram declaration deliberately does not count. It answers substring matching and not
  /// equality, so an equality filter on such a field is still better off reaching the document index
  /// than scanning, and standing down for it would cost an index rather than save one.
  /// </remarks>
  public static bool HasBtree(Type modelType, string propertyName) =>
    Kinds(modelType, propertyName).HasFlag(IndexKind.Btree);

  /// <summary>
  /// Whether any model declares a btree over a property of this name.
  /// </summary>
  /// <param name="propertyName">The document key, which is the property's name.</param>
  /// <returns>True when some model declares a btree for it.</returns>
  /// <remarks>
  /// <para>
  /// Asked by the reshape, which works on translated SQL and therefore has a document key rather than
  /// a model type: by that stage the expression tree that knew which model was being queried is gone.
  /// </para>
  /// <para>
  /// So this is deliberately coarser than <see cref="HasBtree"/>, and the coarseness is safe in the
  /// direction that matters. Standing down where another model declared the same key costs an index
  /// on a filter that would otherwise have used the document index, which is a slower query. Failing
  /// to stand down would leave a declared index unused, which is the problem being solved. Between a
  /// query that is slower and a feature that does not work, the first is the right error.
  /// </para>
  /// </remarks>
  public static bool HasAnyBtreeFor(string propertyName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

    foreach (var entry in _kinds) {
      if (string.Equals(entry.Key.PropertyName, propertyName, StringComparison.Ordinal)
          && entry.Value.HasFlag(IndexKind.Btree)) {
        return true;
      }
    }

    return false;
  }

  private static readonly ConcurrentDictionary<(string TableName, string PropertyName), IndexKind> _byTable =
    new();

  /// <summary>
  /// Records the same declaration against the table the perspective is stored in.
  /// </summary>
  /// <param name="tableName">The perspective's table.</param>
  /// <param name="propertyName">The property's name, which is also the document key.</param>
  /// <param name="kind">The kinds of index declared.</param>
  /// <remarks>
  /// Kept alongside the model-keyed registration rather than replacing it, because the two mechanisms
  /// have different things in hand. The rewrite that works on the expression tree knows the model
  /// being queried; the one that reshapes translated SQL does not, because that tree is gone by then,
  /// and what it has instead is the table the column belongs to.
  /// </remarks>
  public static void RegisterForTable(string tableName, string propertyName, IndexKind kind) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

    _byTable.AddOrUpdate((tableName, propertyName), kind, (_, existing) => existing | kind);
  }

  /// <summary>
  /// Whether a property of a perspective stored in this table carries a btree of its own.
  /// </summary>
  /// <param name="tableName">The table the column belongs to.</param>
  /// <param name="propertyName">The document key.</param>
  /// <returns>True when that table's perspective declares a btree for it.</returns>
  /// <remarks>
  /// This replaced a lookup by property name alone, which was wrong in a way worth recording. Property
  /// names repeat across perspectives constantly, so one model declaring an index on a common name
  /// made every other model's property of that name stand down, quietly costing them the document
  /// index. It cost no correctness, which is exactly why it would not have been noticed: every query
  /// still returned the right rows, more slowly, on models that had declared nothing at all.
  /// </remarks>
  public static bool HasBtreeForTable(string tableName, string propertyName) {
    ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
    ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

    return _byTable.TryGetValue((tableName, propertyName), out var kind)
        && kind.HasFlag(IndexKind.Btree);
  }

  /// <summary>Forgets every registration. For tests that need a known starting point.</summary>
  public static void Clear() {
    _kinds.Clear();
    _byTable.Clear();
  }
}
