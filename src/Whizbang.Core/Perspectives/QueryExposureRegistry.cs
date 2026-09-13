using System.Collections.Concurrent;

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
public enum QueryExposure {
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
  private static readonly ConcurrentDictionary<Type, QueryExposure> _exposures = new();

  /// <summary>
  /// Records that a model is reachable by a request-composed query.
  /// </summary>
  /// <typeparam name="TModel">The perspective's model type.</typeparam>
  /// <param name="exposure">The ways a request can shape the query.</param>
  public static void Register<TModel>(QueryExposure exposure) => Register(typeof(TModel), exposure);

  /// <summary>
  /// Records that a model is reachable by a request-composed query.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <param name="exposure">The ways a request can shape the query.</param>
  /// <remarks>
  /// Combined rather than replaced, because one model is commonly reached from several surfaces and
  /// each registers only what it offers. The widest exposure is the one that matters, and taking the
  /// last writer instead would make the answer depend on assembly load order.
  /// </remarks>
  public static void Register(Type modelType, QueryExposure exposure) {
    ArgumentNullException.ThrowIfNull(modelType);

    if (exposure == QueryExposure.None) {
      return;
    }

    _exposures.AddOrUpdate(modelType, exposure, (_, existing) => existing | exposure);
  }

  /// <summary>
  /// How a request can shape queries over this model, across every surface that registered it.
  /// </summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>The combined exposure, or <see cref="QueryExposure.None"/> when nothing registered it.</returns>
  public static QueryExposure Of(Type modelType) {
    ArgumentNullException.ThrowIfNull(modelType);

    return _exposures.TryGetValue(modelType, out var exposure) ? exposure : QueryExposure.None;
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
    (Of(modelType) & (QueryExposure.Ordering | QueryExposure.Expression)) != QueryExposure.None;

  /// <summary>Every model a surface has registered, for the maintenance cycle to report on.</summary>
  /// <returns>The registered models and their combined exposure.</returns>
  public static IReadOnlyDictionary<Type, QueryExposure> All() =>
    new Dictionary<Type, QueryExposure>(_exposures);
}
