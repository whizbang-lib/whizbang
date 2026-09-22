using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// When a perspective a request can sort by is big enough to be worth saying something about.
/// </summary>
/// <remarks>
/// <para>
/// This is the half WHIZ306 cannot reach. The build knows a model is exposed to request-time sorting
/// and has no idea whether the table behind it holds two hundred rows or two hundred million. The
/// first case is fine and warning about it is how the whole rule gets suppressed; the second is the
/// most expensive query shape there is and returns correct rows while doing it.
/// </para>
/// <para>
/// The quiet cases are the ones worth pinning hardest, because this runs on a 30-second cycle. A
/// finding that repeated every cycle would become a wall of identical warnings, and a model whose
/// table the driver cannot name would take the statistics cycle down with it.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
[NotInParallel("QueryExposureRegistry is static")]
public class QueryExposureAdvisoryTests {
  private const long BIG = QueryExposureAdvisory.DEFAULT_SIZE_THRESHOLD_BYTES;

  private sealed class AdvisoryOrderedModel;
  private sealed class AdvisoryFilteredModel;
  private sealed class AdvisorySmallModel;
  private sealed class AdvisoryUntabledModel;
  private sealed class AdvisoryRepeatModel;
  private sealed class AdvisoryMissingSizeModel;
  private sealed class AdvisoryAnsweredModel;
  private sealed class AdvisoryFieldsModel;

  /// <summary>Names a table per model, and throws for anything it was not given.</summary>
  private sealed class FakeTables(Dictionary<Type, string> byModel) : ICollectiveSiblingTableSource {
    public string TableFor(Type modelType) =>
      byModel.TryGetValue(modelType, out var table)
        ? table
        : throw new InvalidOperationException($"no table for {modelType.Name}");
  }

  /// <summary>
  /// A fresh advisory with a ledger of its own.
  /// </summary>
  /// <remarks>
  /// Per test, not shared: the suppression is now the ledger's, so a ledger shared between tests
  /// would let whichever ran first silence the rest, and the registry these read is static.
  /// </remarks>
  private static QueryExposureAdvisory _advisory(IAdvisoryLedger? ledger = null) =>
    new(NullLogger<QueryExposureAdvisory>.Instance, ledger ?? new AdvisoryLedger());

  /// <summary>An exposed perspective over a large table is the reported case.</summary>
  [Test]
  public async Task ALargeOrderablePerspectiveIsReportedAsync() {
    QueryExposureRegistry.Register<AdvisoryOrderedModel>(QueryExposures.Ordering, "JobName");

    var reported = await _advisory().ReportAsync(
      new Dictionary<string, long> { ["ordered_table"] = BIG },
      new FakeTables(new() { [typeof(AdvisoryOrderedModel)] = "ordered_table" }));

    await Assert.That(reported.Count).IsEqualTo(1);
  }

  /// <summary>A small table is not worth reporting, whatever it is exposed to.</summary>
  /// <remarks>
  /// A sequential scan of a few hundred rows does not show up in a request. Reporting it would make
  /// the advisory noise, and noise is why warnings get filtered.
  /// </remarks>
  [Test]
  public async Task ASmallPerspectiveIsNotReportedAsync() {
    QueryExposureRegistry.Register<AdvisorySmallModel>(QueryExposures.Ordering, "JobName");

    var reported = await _advisory().ReportAsync(
      new Dictionary<string, long> { ["small_table"] = BIG - 1 },
      new FakeTables(new() { [typeof(AdvisorySmallModel)] = "small_table" }));

    await Assert.That(reported.Count).IsEqualTo(0);
  }

  /// <summary>Filtering alone is not the expensive case, however big the table is.</summary>
  [Test]
  public async Task AFilterOnlyExposureIsNotReportedAsync() {
    QueryExposureRegistry.Register<AdvisoryFilteredModel>(QueryExposures.Filtering, "JobName");

    var reported = await _advisory().ReportAsync(
      new Dictionary<string, long> { ["filtered_table"] = BIG * 10 },
      new FakeTables(new() { [typeof(AdvisoryFilteredModel)] = "filtered_table" }));

    await Assert.That(reported.Count).IsEqualTo(0)
      .Because("the document's containment index answers a filter, so the size does not make it a "
        + "missing-index problem");
  }

  /// <summary>
  /// A model whose table the driver cannot name is skipped, not thrown over.
  /// </summary>
  /// <remarks>
  /// <c>TableFor</c> throws by contract, which is right for a query that cannot be built without a
  /// table and wrong on a statistics cycle: one unmappable model would stop size, queue-depth, and
  /// bloat collection for the whole process.
  /// </remarks>
  [Test]
  public async Task AModelWithNoKnownTableIsSkippedAsync() {
    QueryExposureRegistry.Register<AdvisoryUntabledModel>(QueryExposures.Ordering, "JobName");

    var advisory = _advisory();
    var reported = await advisory.ReportAsync(
      new Dictionary<string, long> { ["some_table"] = BIG },
      new FakeTables([]));

    await Assert.That(reported.Count).IsEqualTo(0);
  }

  /// <summary>A table the statistics did not mention is skipped.</summary>
  [Test]
  public async Task AModelWithNoSizeReportedIsSkippedAsync() {
    QueryExposureRegistry.Register<AdvisoryMissingSizeModel>(QueryExposures.Ordering, "JobName");

    var reported = await _advisory().ReportAsync(
      new Dictionary<string, long>(),
      new FakeTables(new() { [typeof(AdvisoryMissingSizeModel)] = "absent_table" }));

    await Assert.That(reported.Count).IsEqualTo(0);
  }

  /// <summary>The same finding is reported once, not once per cycle.</summary>
  [Test]
  public async Task AFindingIsReportedOnceAsync() {
    QueryExposureRegistry.Register<AdvisoryRepeatModel>(QueryExposures.Ordering, "JobName");

    var advisory = _advisory();
    var sizes = new Dictionary<string, long> { ["repeat_table"] = BIG };
    var tables = new FakeTables(new() { [typeof(AdvisoryRepeatModel)] = "repeat_table" });

    var first = await advisory.ReportAsync(sizes, tables);
    var second = await advisory.ReportAsync(sizes, tables);
    var third = await advisory.ReportAsync(sizes, tables);

    await Assert.That(first.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(second.Count).IsEqualTo(0)
      .Because("the statistics cycle runs every 30 seconds, so a repeating finding would become a "
        + "wall of identical warnings");
    await Assert.That(third.Count).IsEqualTo(0);
  }

  /// <summary>Without a table source nothing can be said, and nothing is.</summary>
  [Test]
  public async Task NoTableSourceReportsNothingAsync() {
    QueryExposureRegistry.Register<AdvisoryOrderedModel>(QueryExposures.Ordering, "JobName");

    var reported = await _advisory().ReportAsync(
      new Dictionary<string, long> { ["ordered_table"] = BIG }, tables: null);

    await Assert.That(reported.Count).IsEqualTo(0)
      .Because("the exposure is known but the table it lands on is not, so there is no size to judge");
  }

  /// <summary>A missing statistics dictionary is a programming error.</summary>
  [Test]
  public async Task NullSizesAreRejectedAsync() {
    await Assert.That(async () => await _advisory().ReportAsync(null!, new FakeTables([])))
      .Throws<ArgumentNullException>();
  }

  /// <summary>
  /// A model whose fields are all accounted for is not reported, however large it is.
  /// </summary>
  /// <remarks>
  /// This is what makes a recorded decision mean something. A model that indexed what it exposes,
  /// asked for every field, or carries <c>[SuppressIndexAdvisory]</c> registers its exposure with no
  /// unaccounted fields, and an advisory that asked again at runtime would make the suppression
  /// worthless while claiming to honor it.
  /// </remarks>
  [Test]
  public async Task AnAccountedForModelIsNotReportedAsync() {
    QueryExposureRegistry.Register<AdvisoryAnsweredModel>(QueryExposures.Ordering);

    var reported = await _advisory().ReportAsync(
      new Dictionary<string, long> { ["answered_table"] = BIG * 100 },
      new FakeTables(new() { [typeof(AdvisoryAnsweredModel)] = "answered_table" }));

    await Assert.That(reported.Count).IsEqualTo(0)
      .Because("the author already answered, at build time, and the answer travels with the "
        + "registration");
  }

  /// <summary>The fields a surface left unaccounted for are what comes back.</summary>
  [Test]
  public async Task TheUnaccountedFieldsAreRecordedAsync() {
    QueryExposureRegistry.Register<AdvisoryFieldsModel>(QueryExposures.Ordering, "JobName", "Status");

    var fields = QueryExposureRegistry.UnindexedFields(typeof(AdvisoryFieldsModel));

    await Assert.That(fields).Contains("JobName");
    await Assert.That(fields).Contains("Status");
    await Assert.That(QueryExposureRegistry.UnindexedFields(typeof(AdvisoryAnsweredModel))).IsEmpty();
  }

  /// <summary>The threshold is the caller's to set.</summary>
  [Test]
  public async Task TheThresholdIsConfigurableAsync() {
    QueryExposureRegistry.Register<AdvisorySmallModel>(QueryExposures.Ordering, "JobName");

    var reported = await _advisory().ReportAsync(
      new Dictionary<string, long> { ["small_table"] = 4096 },
      new FakeTables(new() { [typeof(AdvisorySmallModel)] = "small_table" }),
      thresholdBytes: 1024);

    await Assert.That(reported.Count).IsEqualTo(1);
  }
}
