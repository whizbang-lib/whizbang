using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The ways a perspective model can be queried by something built from a request rather than
/// written in source.
/// </summary>
/// <remarks>
/// A flag rather than a boolean because the cost differs. Filtering narrows and may be answered from
/// the document's containment index; ordering cannot be answered by containment at all and reads
/// every row without an index over the extraction. An arbitrary expression is the widest of the
/// three, since it can produce any shape a query can take.
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
[Flags]
public enum QueryExposures {
  /// <summary>Not reachable from a request-composed query.</summary>
  None = 0,

  /// <summary>A request can choose which rows are returned.</summary>
  Filtering = 1,

  /// <summary>A request can choose the order rows are returned in.</summary>
  Ordering = 2,

  /// <summary>A request can compose an arbitrary predicate or projection.</summary>
  Expression = 4,
}

/// <summary>
/// Which perspective models can be queried by a request-composed query, and how.
/// </summary>
/// <remarks>
/// <para>
/// The build-time advisory reads source, so it can see a filter someone wrote and cannot see one
/// composed when a request arrives. That leaves the models queried hardest as the ones it says least
/// about. This registry carries the fact the build could not: that a model is reachable by ordering
/// or filtering chosen at request time, so any of its fields may end up in an <c>ORDER BY</c>.
/// </para>
/// <para>
/// <strong>Why a registry and not a lookup.</strong> The exposure is declared where the surface is,
/// which is usually the host, while the model is declared in a library that knows nothing about it.
/// A generator sees only its own compilation, so a generator running over the library cannot learn
/// what the host exposes. Each assembly therefore registers what it declares, and the registrations
/// compose as assemblies load, which is the same reason the index and message-type registries exist.
/// </para>
/// <para>
/// No reflection, so it survives trimming and native compilation: the entries are emitted as
/// ordinary calls by the generator that read the attributes.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/QueryExposureRegistryTests.cs</tests>
public static class QueryExposureRegistry {
  private static readonly ConcurrentDictionary<Type, QueryExposures> _exposures = new();
  private static readonly ConcurrentDictionary<Type, ImmutableArray<string>> _unindexedFields = new();

  /// <summary>
  /// Records that a model is reachable by a request-composed query.
  /// </summary>
  /// <typeparam name="TModel">The perspective's model type.</typeparam>
  /// <param name="exposure">The ways a request can shape the query.</param>
  public static void Register<TModel>(QueryExposures exposure) => Register(typeof(TModel), exposure);

  /// <summary>
  /// Records that a model is reachable by a request-composed query, and which of its fields had no
  /// index and no recorded decision when it was built.
  /// </summary>
  /// <typeparam name="TModel">The perspective's model type.</typeparam>
  /// <param name="exposure">The ways a request can shape the query.</param>
  /// <param name="unindexedFields">
  /// The fields a request could name that nothing accounted for. Empty when every field has an
  /// answer, when the model asked to index all of them, when the author recorded a decision, or when
  /// the model is stored opaquely and has no per-field extraction to index.
  /// </param>
  public static void Register<TModel>(QueryExposures exposure, params string[] unindexedFields) =>
    Register(typeof(TModel), exposure, unindexedFields);

  /// <summary>
  /// Records that a model is reachable by a request-composed query.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <param name="exposure">The ways a request can shape the query.</param>
  /// <param name="unindexedFields">
  /// The fields a request could name that nothing accounted for; empty or null when there are none.
  /// </param>
  /// <remarks>
  /// <para>
  /// Combined rather than replaced, because one model is commonly reached from several surfaces and
  /// each registers only what it offers. The widest exposure is the one that matters, and taking the
  /// last writer instead would make the answer depend on assembly load order.
  /// </para>
  /// <para>
  /// The unaccounted fields are a build-time fact and cannot be recomputed here, since asking a model
  /// which of its fields carry an index would mean reading its attributes at runtime. Carrying them
  /// is also what lets a recorded decision stand down the runtime advisory as well as the build
  /// warning: a suppressed or fully indexed model registers its exposure with no unaccounted fields,
  /// and an advisory with nothing to report says nothing.
  /// </para>
  /// </remarks>
  public static void Register(
      Type modelType, QueryExposures exposure, IReadOnlyList<string>? unindexedFields = null) {
    ArgumentNullException.ThrowIfNull(modelType);

    if (exposure == QueryExposures.None) {
      return;
    }

    _exposures.AddOrUpdate(modelType, exposure, (_, existing) => existing | exposure);

    if (unindexedFields is { Count: > 0 }) {
      _unindexedFields.AddOrUpdate(
        modelType,
        _ => [.. unindexedFields],
        (_, held) => [.. held.Union(unindexedFields, StringComparer.Ordinal)]);
    }
  }

  /// <summary>
  /// The model's fields that a request could name and that nothing accounted for at build time.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>The unaccounted field names, empty when every field has an answer.</returns>
  /// <remarks>
  /// Empty is the answer for a model that indexed what it exposes, asked for every field, recorded a
  /// decision with <c>[SuppressIndexAdvisory]</c>, or is stored opaquely. All four mean the same
  /// thing to a caller: there is nothing here to advise about.
  /// </remarks>
  public static IReadOnlyList<string> UnindexedFields(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);

    return _unindexedFields.TryGetValue(modelType, out var fields) ? fields : [];
  }

  /// <summary>
  /// How a request can shape queries over this model, across every surface that registered it.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>The combined exposure, or <see cref="QueryExposures.None"/> when nothing registered it.</returns>
  public static QueryExposures Of(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);

    return _exposures.TryGetValue(modelType, out var exposure) ? exposure : QueryExposures.None;
  }

  /// <summary>
  /// Whether a request can choose the order rows come back in, which is the expensive case.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns><c>true</c> when ordering or an arbitrary expression is on offer.</returns>
  /// <remarks>
  /// Ordering and an arbitrary expression are grouped because the document's containment index
  /// answers neither: both reach a sort or a comparison over an extraction, which scans without an
  /// index built for it. Filtering alone may still be answered by containment.
  /// </remarks>
  public static bool CanBeOrdered(Type modelType) =>
    (Of(modelType) & (QueryExposures.Ordering | QueryExposures.Expression)) != QueryExposures.None;

  /// <summary>Every model a surface has registered, for the maintenance cycle to report on.</summary>
  /// <returns>The registered models and their combined exposure.</returns>
  public static IReadOnlyDictionary<Type, QueryExposures> All() =>
    new Dictionary<Type, QueryExposures>(_exposures);
}
