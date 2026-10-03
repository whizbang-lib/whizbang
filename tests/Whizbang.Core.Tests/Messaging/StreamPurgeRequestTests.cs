using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The operator stream purge's request and report (#1030): a request yields the same batches every time (a resumed
/// purge must claim the same batch under the same index), refuses what it cannot audit or must not touch, and the
/// report totals only the batches this instance ran.
/// </summary>
/// <docs>operations/infrastructure/purging-streams</docs>
public class StreamPurgeRequestTests {
  private static StreamPurgeRequest _request(params Guid[] ids) =>
    new() { StreamIds = ids, RequestedBy = "operator-1", Reason = "orphaned" };

  [Test]
  public async Task Batches_AreDistinct_Ordered_AndChunkedAsync() {
    var a = Guid.Parse("00000000-0000-0000-0000-000000000003");
    var b = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var c = Guid.Parse("00000000-0000-0000-0000-000000000002");

    var batches = (_request(a, b, c, a) with { BatchSize = 2 }).Batches();

    await Assert.That(batches).Count().IsEqualTo(2);
    await Assert.That(batches[0].ToArray()).IsEquivalentTo(new[] { b, c });
    await Assert.That(batches[1].ToArray()).IsEquivalentTo(new[] { a });
  }

  [Test]
  public async Task Defaults_AreOneHundredStreamsPerBatch_AndAFreshPurgeIdAsync() {
    var request = _request(Guid.NewGuid());

    await Assert.That(request.BatchSize).IsEqualTo(StreamPurgeRequest.DEFAULT_BATCH_SIZE);
    await Assert.That(request.DryRun).IsFalse();
    await Assert.That(request.PurgeId).IsNotEqualTo(_request(Guid.NewGuid()).PurgeId);
  }

  [Test]
  [Arguments("", "orphaned", 1, "RequestedBy")]
  [Arguments("operator-1", " ", 1, "Reason")]
  [Arguments("operator-1", "orphaned", 0, "BatchSize")]
  public async Task Batches_RefuseAnUnauditableOrEmptyRequestAsync(string requestedBy, string reason, int batchSize, string field) {
    var request = new StreamPurgeRequest {
      StreamIds = [Guid.NewGuid()],
      RequestedBy = requestedBy,
      Reason = reason,
      BatchSize = batchSize,
    };

    var refused = await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(request.Batches()));

    await Assert.That(refused!.ParamName).IsEqualTo(field);
  }

  [Test]
  public async Task Batches_RefuseTheEmptyStreamIdAsync() {
    var refused = await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(_request(Guid.NewGuid(), Guid.Empty).Batches()));

    await Assert.That(refused!.ParamName).IsEqualTo("StreamIds");
  }

  [Test]
  public async Task Report_TotalsTheBatchesThatRan_AndFormatsThemAsync() {
    var ran = new StreamPurgeBatch(0, [Guid.NewGuid(), Guid.NewGuid()], Ran: true,
      new Dictionary<string, long> { ["wh_event_store"] = 5, ["wh_outbox"] = 0 });
    var ranToo = new StreamPurgeBatch(1, [Guid.NewGuid()], Ran: true,
      new Dictionary<string, long> { ["wh_event_store"] = 2 });
    var claimedElsewhere = new StreamPurgeBatch(2, [Guid.NewGuid()], Ran: false, new Dictionary<string, long>());
    var purgeId = Guid.NewGuid();

    var report = new StreamPurgeReport(purgeId, DryRun: false, [ran, ranToo, claimedElsewhere]);

    await Assert.That(report.Totals["wh_event_store"]).IsEqualTo(7);
    await Assert.That(report.Totals["wh_outbox"]).IsEqualTo(0);
    var text = report.Format();
    await Assert.That(text).StartsWith($"Purged 3 stream(s) in 2 batch(es), purge {purgeId}");
    await Assert.That(text).Contains("wh_event_store");
    await Assert.That(text).DoesNotContain("wh_outbox").Because("a table with no rows is left out");
    await Assert.That(text).Contains("Skipped 1 batch(es) already claimed");
  }

  [Test]
  public async Task Report_OfADryRun_SaysSo_AndOmitsSkipsWhenNoneAsync() {
    var report = new StreamPurgeReport(Guid.NewGuid(), DryRun: true,
      [new StreamPurgeBatch(0, [Guid.NewGuid()], Ran: true, new Dictionary<string, long> { ["wh_event_store"] = 1 })]);

    var text = report.Format();

    await Assert.That(text).StartsWith("Dry run: would purge 1 stream(s)");
    await Assert.That(text).DoesNotContain("Skipped");
  }
}
