using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// What the measured advisory reports, and what it stays quiet about.
/// </summary>
/// <remarks>
/// The static check already covers shape, so the whole value of this one is that it is harder to
/// trigger. A threshold low enough to fire on a small table would reproduce that check's noise with
/// more machinery behind it, so what is asserted here is mostly the silence.
/// </remarks>
public class PerspectiveScanAdvisoryTests {
  private sealed class AlwaysReports : IAdvisoryLedger {
    public List<string> Keys { get; } = [];

    public ValueTask<bool> TryBeginReportAsync(
        string findingKey, string signature, DateTimeOffset now, TimeSpan cooldown,
        CancellationToken cancellationToken = default) {
      Keys.Add(findingKey);
      return ValueTask.FromResult(true);
    }
  }

  private static PerspectiveScanAdvisory _advisory(IAdvisoryLedger ledger) =>
    new(ledger, new FakeTimeProvider(), NullLogger<PerspectiveScanAdvisory>.Instance);

  [Test]
  public async Task ATableScannedPastTheThreshold_IsReportedAsync() {
    var ledger = new AlwaysReports();

    var reported = await _advisory(ledger).ReportAsync(
      new Dictionary<string, TableScanStatistics> {
        ["wh_per_documents"] = new(SequentialScans: 4_000, SequentialRowsRead: 50_000_000, IndexScans: 12),
      },
      new Dictionary<string, long> { ["wh_per_documents"] = 1_500_000_000 });

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].TableName).IsEqualTo("wh_per_documents");
    await Assert.That(reported[0].SequentialRowsRead).IsEqualTo(50_000_000);
    await Assert.That(ledger.Keys).Contains("scan:wh_per_documents")
      .Because("the finding is about the table, so the same table means the same finding in any process");
  }

  [Test]
  public async Task ASmallTableScannedOften_IsNotReportedAsync() {
    var reported = await _advisory(new AlwaysReports()).ReportAsync(
      new Dictionary<string, TableScanStatistics> {
        // Scanned constantly, and it does not matter: two hundred rows a time costs nothing.
        ["wh_per_lookup"] = new(SequentialScans: 900_000, SequentialRowsRead: 180_000, IndexScans: 0),
      },
      new Dictionary<string, long> { ["wh_per_lookup"] = 40_000 });

    await Assert.That(reported).IsEmpty()
      .Because("ranking on scans rather than on what they read is what would make this noise");
  }

  [Test]
  public async Task ATableReadMostlyByIndex_IsNotReportedAsync() {
    var reported = await _advisory(new AlwaysReports()).ReportAsync(
      new Dictionary<string, TableScanStatistics> {
        ["wh_per_documents"] = new(SequentialScans: 30, SequentialRowsRead: 90_000_000, IndexScans: 4_000_000),
      },
      new Dictionary<string, long> { ["wh_per_documents"] = 1_500_000_000 });

    await Assert.That(reported).IsEmpty()
      .Because("a table read by index is doing what it should, and a rare full pass over a large "
             + "one is a report or a backfill rather than a missing index");
  }

  [Test]
  public async Task TheFrameworksOwnTables_AreNotReportedAsync() {
    var reported = await _advisory(new AlwaysReports()).ReportAsync(
      new Dictionary<string, TableScanStatistics> {
        ["wh_outbox"] = new(SequentialScans: 500_000, SequentialRowsRead: 900_000_000, IndexScans: 4),
      },
      new Dictionary<string, long> { ["wh_outbox"] = 9_000_000_000 });

    await Assert.That(reported).IsEmpty()
      .Because("the framework's own queues are drained by scanning on purpose, and advising their "
             + "owner to index them would be advising them about a decision already made");
  }

  /// <summary>
  /// An engine that keeps no such counters produces no advice rather than an error.
  /// </summary>
  [Test]
  public async Task NoStatistics_MeansNoAdviceAsync() {
    var reported = await _advisory(new AlwaysReports())
      .ReportAsync(new Dictionary<string, TableScanStatistics>(), new Dictionary<string, long>());

    await Assert.That(reported).IsEmpty();
  }
}
