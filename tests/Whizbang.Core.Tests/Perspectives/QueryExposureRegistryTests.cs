using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// What the registry records about models a request can shape the query for.
/// </summary>
/// <remarks>
/// <para>
/// The registry exists because the exposure and the model are declared in different places. A model
/// lives in a library that knows nothing about how a host exposes it, and a generator sees only its
/// own compilation, so neither side can answer the question alone. Each assembly records what it
/// declares and the answers compose.
/// </para>
/// <para>
/// Combining rather than replacing is the behavior worth pinning hardest. One model is commonly
/// reached from several surfaces, each registering only what it offers, and taking the last writer
/// would make the answer depend on the order assemblies happened to load.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
[NotInParallel("QueryExposureRegistry tests share static state")]
public class QueryExposureRegistryTests {
  private sealed class UnregisteredModel;
  private sealed class OrderedModel;
  private sealed class FilteredModel;
  private sealed class CombinedModel;
  private sealed class ExpressionModel;
  private sealed class GenericallyRegisteredModel;
  private sealed class NoneModel;
  private sealed class ListedModel;

  /// <summary>A model nobody exposed is not reachable from a request-composed query.</summary>
  [Test]
  public async Task AnUnregisteredModelHasNoExposureAsync() {
    await Assert.That(QueryExposureRegistry.Of(typeof(UnregisteredModel)))
      .IsEqualTo(QueryExposures.None);
    await Assert.That(QueryExposureRegistry.CanBeOrdered(typeof(UnregisteredModel))).IsFalse();
  }

  /// <summary>What was registered is what comes back.</summary>
  [Test]
  public async Task ARegisteredExposureIsReturnedAsync() {
    QueryExposureRegistry.Register<OrderedModel>(QueryExposures.Ordering);

    await Assert.That(QueryExposureRegistry.Of(typeof(OrderedModel)))
      .IsEqualTo(QueryExposures.Ordering);
  }

  /// <summary>The generic overload is the one generated code calls.</summary>
  [Test]
  public async Task TheGenericOverloadRegistersTheSameWayAsync() {
    QueryExposureRegistry.Register<GenericallyRegisteredModel>(QueryExposures.Filtering);

    await Assert.That(QueryExposureRegistry.Of(typeof(GenericallyRegisteredModel)))
      .IsEqualTo(QueryExposures.Filtering);
  }

  /// <summary>
  /// Several surfaces over one model combine, so the widest exposure survives.
  /// </summary>
  /// <remarks>
  /// The load-order case. If the second registration replaced the first, a model exposed to ordering
  /// by one surface and filtering by another would report whichever assembly loaded last.
  /// </remarks>
  [Test]
  public async Task SeveralRegistrationsCombineAsync() {
    QueryExposureRegistry.Register<CombinedModel>(QueryExposures.Filtering);
    QueryExposureRegistry.Register<CombinedModel>(QueryExposures.Ordering);

    await Assert.That(QueryExposureRegistry.Of(typeof(CombinedModel)))
      .IsEqualTo(QueryExposures.Filtering | QueryExposures.Ordering)
      .Because("each surface registers only what it offers and the model is exposed to both");
  }

  /// <summary>Registering nothing records nothing, rather than an empty entry.</summary>
  [Test]
  public async Task RegisteringNoExposureRecordsNothingAsync() {
    QueryExposureRegistry.Register<NoneModel>(QueryExposures.None);

    await Assert.That(QueryExposureRegistry.All().ContainsKey(typeof(NoneModel))).IsFalse()
      .Because("a surface offering neither ordering nor filtering is not an exposure to report on");
  }

  /// <summary>Filtering alone is not the expensive case.</summary>
  /// <remarks>
  /// The document's containment index can answer a filter. It cannot answer a sort, which reads every
  /// row unless an index exists over the extraction, so only ordering raises the question.
  /// </remarks>
  [Test]
  public async Task FilteringAloneIsNotOrderableAsync() {
    QueryExposureRegistry.Register<FilteredModel>(QueryExposures.Filtering);

    await Assert.That(QueryExposureRegistry.CanBeOrdered(typeof(FilteredModel))).IsFalse();
  }

  /// <summary>Ordering and an arbitrary expression both reach a sort.</summary>
  [Test]
  public async Task OrderingAndExpressionAreBothOrderableAsync() {
    QueryExposureRegistry.Register<OrderedModel>(QueryExposures.Ordering);
    QueryExposureRegistry.Register<ExpressionModel>(QueryExposures.Expression);

    await Assert.That(QueryExposureRegistry.CanBeOrdered(typeof(OrderedModel))).IsTrue();
    await Assert.That(QueryExposureRegistry.CanBeOrdered(typeof(ExpressionModel))).IsTrue()
      .Because("an arbitrary expression can produce any ORDER BY a query can take");
  }

  /// <summary>The maintenance cycle reads the whole set, so it has to be enumerable.</summary>
  [Test]
  public async Task EveryRegisteredModelIsListedAsync() {
    QueryExposureRegistry.Register<ListedModel>(QueryExposures.Ordering);

    var all = QueryExposureRegistry.All();

    await Assert.That(all.ContainsKey(typeof(ListedModel))).IsTrue();
    await Assert.That(all[typeof(ListedModel)]).IsEqualTo(QueryExposures.Ordering);
  }

  /// <summary>
  /// The listing is a copy, so a caller holding it cannot be surprised by a later registration.
  /// </summary>
  /// <remarks>
  /// Assemblies load while the process runs, so registrations can arrive at any time. A live view
  /// would let the maintenance cycle's enumeration change under it.
  /// </remarks>
  [Test]
  public async Task TheListingIsASnapshotAsync() {
    var before = QueryExposureRegistry.All();
    var countBefore = before.Count;

    QueryExposureRegistry.Register<SnapshotProbe>(QueryExposures.Ordering);

    await Assert.That(before.Count).IsEqualTo(countBefore)
      .Because("the returned dictionary is a copy taken when it was asked for");
    await Assert.That(QueryExposureRegistry.All().ContainsKey(typeof(SnapshotProbe))).IsTrue();
  }

  private sealed class SnapshotProbe;

  /// <summary>A missing type is a programming error, not an empty answer.</summary>
  [Test]
  public async Task ANullTypeIsRejectedAsync() {
    await Assert.That(() => QueryExposureRegistry.Register(null!, QueryExposures.Ordering))
      .Throws<ArgumentNullException>();
    await Assert.That(() => QueryExposureRegistry.Of(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => QueryExposureRegistry.CanBeOrdered(null!))
      .Throws<ArgumentNullException>();
  }
}
