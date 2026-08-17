using System;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The paced-repair-drain substrate (migration 097), against real Postgres: discovery stamps the
/// compared window onto ledger rows, and the drain CLAIMS eligible rows atomically — past
/// backoff, under the attempt cap, least-recently-attempted first, SKIP LOCKED so concurrent
/// drainers never double-dispatch a bucket. The claim stamps the attempt exactly like the proven
/// single-key repair grant, so eligibility semantics cannot drift between the burst path and the
/// drain.
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
[Category("Integration")]
public class PacedRepairDrainSqlTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream) =>
    new(origin, "tenant-a", "Contracts.DrainProbe", stream);

  private static IntegrityReportObservation _obs(IntegrityRepairLedger.DivergenceKey key) =>
    new(key, 11, 21, 0, 0);

  [Test]
  public async Task StampWindows_PersistsTheComparedRange_OnReportedRowsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var keys = Enumerable.Range(0, 2).Select(_ => _key(origin, Guid.NewGuid())).ToList();
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, keys.Select(_obs).ToList(), now, TimeSpan.FromMinutes(60));

    await coordinator.IntegrityStampRepairWindowsAsync(
      origin, keys, windowFrom: 100, windowUntil: 500);

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(claimed.Count).IsEqualTo(2)
      .Because("both reported buckets are eligible for their first attempt");
    await Assert.That(claimed.All(c => c.WindowFrom == 100 && c.WindowUntil == 500)).IsTrue()
      .Because("the drain dispatches the RANGE the compare disagreed on — the window must ride the row");
  }

  [Test]
  public async Task Claim_StampsTheAttempt_AndBackoffSuppressesTheNextClaimAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var key = _key(origin, Guid.NewGuid());
    var now = DateTimeOffset.UtcNow;
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(key)], now, TimeSpan.FromMinutes(60));

    var first = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(first.Count).IsEqualTo(1);

    var insideBackoff = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(2), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(insideBackoff).IsEmpty()
      .Because("a claim IS an attempt — the same exponential ladder the burst path enforces");

    var pastBackoff = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(302), TimeSpan.FromSeconds(300), maxAttempts: 8, limit: 10);
    await Assert.That(pastBackoff.Count).IsEqualTo(1)
      .Because("past the base backoff the bucket re-offers");
  }

  [Test]
  public async Task Claim_RespectsAttemptCap_OriginFilter_AndLimitAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var learned = Guid.NewGuid();
    var unlearned = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var learnedKeys = Enumerable.Range(0, 3).Select(_ => _key(learned, Guid.NewGuid())).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      learned, learnedKeys.Select(_obs).ToList(), now, TimeSpan.FromMinutes(60));
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      unlearned, [_obs(_key(unlearned, Guid.NewGuid()))], now, TimeSpan.FromMinutes(60));

    var claimed = await coordinator.IntegrityClaimRepairDrainAsync(
      [learned], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 2);
    await Assert.That(claimed.Count).IsEqualTo(2)
      .Because("the limit bounds one drain pass");
    await Assert.That(claimed.All(c => c.OriginServiceId == learned)).IsTrue()
      .Because("an origin with no learned request topic is never claimed — nothing could be sent");

    var exhausted = await coordinator.IntegrityClaimRepairDrainAsync(
      [learned], now.AddMinutes(10), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(exhausted.Count).IsEqualTo(1)
      .Because("only the never-attempted third row is eligible — the two capped rows hold the "
               + "terminal wait (300s x 2^6), far past the ten-minute mark");
  }

  [Test]
  public async Task Claim_PastCap_RetriesAtTerminalCadenceAsync() {
    // The drain twin of the burst-path terminal cadence: a capped row is not shadow-banned
    // forever — it re-enters the claimable pool once per terminal interval (base x 2^6), so a
    // deficit whose budget burned against a down origin still converges after the origin returns.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var terminal = TimeSpan.FromSeconds(300 * 64);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(_key(origin, Guid.NewGuid()))], now, TimeSpan.FromMinutes(60));

    var first = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(first.Count).IsEqualTo(1).Because("precondition: the row's budget is now spent");

    var inside = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1) + terminal - TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(inside.Count).IsEqualTo(0)
      .Because("inside the terminal wait a capped row stays out of the pool");

    var atCadence = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1) + terminal, TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(atCadence.Count).IsEqualTo(1)
      .Because("each terminal interval earns the capped row exactly one more claim");

    var rightAfter = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(2) + terminal, TimeSpan.FromSeconds(300), maxAttempts: 1, limit: 10);
    await Assert.That(rightAfter.Count).IsEqualTo(0)
      .Because("the terminal grant is a cadence, not a reopened floodgate");
  }

  [Test]
  public async Task Claim_NeverReturnsTheSyntheticBulkLane_AndOrdersLeastRecentlyAttemptedFirstAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var bulkKey = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", "Contracts.DrainProbe", Guid.Empty);
    var streamA = _key(origin, Guid.NewGuid());
    var streamB = _key(origin, Guid.NewGuid());
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(bulkKey), _obs(streamA), _obs(streamB)], now, TimeSpan.FromMinutes(60));

    // streamA gets an early attempt; streamB stays never-attempted — B must come first.
    var first = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(1), TimeSpan.FromSeconds(1), maxAttempts: 8, limit: 1);
    await Assert.That(first.Count).IsEqualTo(1);
    var attempted = first[0].StreamId;

    var second = await coordinator.IntegrityClaimRepairDrainAsync(
      [origin], now.AddSeconds(3), TimeSpan.FromSeconds(1), maxAttempts: 8, limit: 1);
    await Assert.That(second.Count).IsEqualTo(1);
    await Assert.That(second[0].StreamId).IsNotEqualTo(attempted)
      .Because("least-recently-attempted first — the never-attempted row outranks the just-attempted one");
    await Assert.That(first[0].StreamId).IsNotEqualTo(Guid.Empty);
    await Assert.That(second[0].StreamId).IsNotEqualTo(Guid.Empty)
      .Because("the synthetic bulk lane (stream zero) dispatches through bulk escalation, not the per-stream drain");
  }
}
