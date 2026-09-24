using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// What a provider that predates the measured readings answers for them.
/// </summary>
/// <remarks>
/// The scan counters and the statement statistics are PostgreSQL's. An engine that does not keep
/// them has to keep working, and the advisory built on them has to have nothing to say rather than
/// something wrong to say, so the contract answers empty for both instead of requiring every
/// provider to write the same two methods.
/// </remarks>
[Category("Core")]
[Category("Observability")]
public class TableStatisticsContractTests {
  /// <summary>A provider written against the contract before the measured readings existed.</summary>
  private sealed class SizesOnlyProvider : ITableStatisticsProvider {
    public Task<IReadOnlyDictionary<string, long>> GetEstimatedTableSizesAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(
        new Dictionary<string, long> { ["wh_per_documents"] = 1_500_000_000 });

    public Task<IReadOnlyDictionary<string, long>> GetQueueDepthsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
  }

  [Test]
  public async Task AProviderWithoutTheMeasuredReadings_AnswersEmptyRatherThanFailingAsync() {
    ITableStatisticsProvider provider = new SizesOnlyProvider();

    await Assert.That(await provider.GetTableScanStatisticsAsync()).IsEmpty()
      .Because("an engine that does not keep these counters reports nothing rather than failing");

    await Assert.That(await provider.GetExpensivePredicatesAsync()).IsEmpty()
      .Because("naming the filter is an improvement on the advice, never a condition of it");

    await Assert.That(await provider.GetTableBloatRatiosAsync()).IsEmpty()
      .Because("a provider that cannot estimate bloat keeps working, and the gauge reports nothing");
  }
}
