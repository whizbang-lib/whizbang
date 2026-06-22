using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Repositories;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Locks the live-progress resolver helpers. Backs every saga's
/// dashboard <c>processedCount</c> / <c>failedCount</c> /
/// <c>progressPercent</c> GraphQL resolvers. Two key invariants pinned:
/// terminal sagas short-circuit to stored counters (no DB round-trip
/// on every refresh); running sagas read a live GROUP BY aggregate
/// because the stored counters are still 0 until SagaCompletedEvent
/// lands.
/// </summary>
[Category("Unit")]
[Category("Saga")]
public class SagaLiveProgressResolversTests {

  private static readonly Guid _sagaId = Guid.Parse("11111111-1111-1111-1111-111111111111");

  // ── IsTerminal ───────────────────────────────────────────────────────

  [Test]
  [Arguments(SagaStatus.Pending, false)]
  [Arguments(SagaStatus.Running, false)]
  [Arguments(SagaStatus.Reset, false)]
  [Arguments(SagaStatus.Completed, true)]
  [Arguments(SagaStatus.CompletedWithFailures, true)]
  [Arguments(SagaStatus.Failed, true)]
  public async Task IsTerminal_TrueForCompletedFailedAsync(SagaStatus status, bool expected) {
    await Assert.That(SagaLiveProgressResolvers.IsTerminal(status)).IsEqualTo(expected);
  }

  // ── Terminal short-circuit ───────────────────────────────────────────

  [Test]
  public async Task ResolveProcessedCount_Terminal_ReturnsStoredAndDoesNotCallRepoAsync() {
    var repo = new ThrowingRepo();   // any repo call would fail the test

    var result = await SagaLiveProgressResolvers.ResolveProcessedCountAsync(
      _sagaId, SagaStatus.Completed, storedCompletedItems: 95, repo, CancellationToken.None);

    await Assert.That(result).IsEqualTo(95)
      .Because("Once terminal, stored counters are authoritative — the helper must avoid the GROUP BY round-trip on every dashboard refresh.");
  }

  [Test]
  public async Task ResolveFailedCount_Terminal_ReturnsStoredAndDoesNotCallRepoAsync() {
    var repo = new ThrowingRepo();

    var result = await SagaLiveProgressResolvers.ResolveFailedCountAsync(
      _sagaId, SagaStatus.CompletedWithFailures, storedFailedItems: 5, repo, CancellationToken.None);

    await Assert.That(result).IsEqualTo(5);
  }

  [Test]
  public async Task ResolveProgressPercent_Terminal_CapsAt100Async() {
    var repo = new ThrowingRepo();

    var result = await SagaLiveProgressResolvers.ResolveProgressPercentAsync(
      _sagaId, SagaStatus.Completed, totalItems: 4, storedCompletedItems: 3, storedFailedItems: 2, repo, CancellationToken.None);

    await Assert.That(result).IsEqualTo(100)
      .Because("(completed + failed)/total > 1.0 would render as 125% to the user without the cap.");
  }

  // ── Running: aggregate read ──────────────────────────────────────────

  [Test]
  public async Task ResolveProcessedCount_Running_ReadsFromRepoAggregateAsync() {
    var repo = new StubRepo(expectedSagaId: _sagaId, agg: new SagaItemAggregate(Total: 300, Completed: 200, Failed: 10, InProgress: 90));

    var result = await SagaLiveProgressResolvers.ResolveProcessedCountAsync(
      _sagaId, SagaStatus.Running, storedCompletedItems: 0, repo, CancellationToken.None);

    await Assert.That(result).IsEqualTo(200)
      .Because("Stored CompletedItems is still 0 while running — the only correct count comes from the GROUP BY aggregate.");
  }

  [Test]
  public async Task ResolveFailedCount_Running_ReadsFromRepoAggregateAsync() {
    var repo = new StubRepo(expectedSagaId: _sagaId, agg: new SagaItemAggregate(Total: 300, Completed: 200, Failed: 10, InProgress: 90));

    var result = await SagaLiveProgressResolvers.ResolveFailedCountAsync(
      _sagaId, SagaStatus.Running, storedFailedItems: 0, repo, CancellationToken.None);

    await Assert.That(result).IsEqualTo(10);
  }

  [Test]
  public async Task ResolveProgressPercent_Running_ComputesFromLiveAggregateAsync() {
    var repo = new StubRepo(expectedSagaId: _sagaId, agg: new SagaItemAggregate(Total: 300, Completed: 200, Failed: 10, InProgress: 90));

    var result = await SagaLiveProgressResolvers.ResolveProgressPercentAsync(
      _sagaId, SagaStatus.Running, totalItems: 300, storedCompletedItems: 0, storedFailedItems: 0, repo, CancellationToken.None);

    await Assert.That(result).IsEqualTo(70)
      .Because("(200 + 10) / 300 = 70.0%.");
  }

  [Test]
  public async Task ResolveProgressPercent_TotalItemsZero_ReturnsZeroWithoutRepoCallAsync() {
    var repo = new ThrowingRepo();

    var result = await SagaLiveProgressResolvers.ResolveProgressPercentAsync(
      _sagaId, SagaStatus.Running, totalItems: 0, storedCompletedItems: 0, storedFailedItems: 0, repo, CancellationToken.None);

    await Assert.That(result).IsEqualTo(0)
      .Because("A downstream saga awaiting TotalItems must render 0% — and avoid the round-trip.");
  }

  // ── Null guards ──────────────────────────────────────────────────────

  [Test]
  public async Task ResolveProcessedCount_NullRepo_ThrowsAsync() {
    await Assert.That(() => SagaLiveProgressResolvers.ResolveProcessedCountAsync(
        _sagaId, SagaStatus.Running, 0, null!, CancellationToken.None))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Test repos ───────────────────────────────────────────────────────

  private sealed class ThrowingRepo : ISagaItemRepository {
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("Resolver must not query the repo for terminal sagas or zero-total cases.");
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("Resolver must not list items.");
  }

  private sealed class StubRepo(Guid expectedSagaId, SagaItemAggregate agg) : ISagaItemRepository {
    public Task<SagaItemAggregate> GetAggregateForSagaAsync(Guid sagaId, CancellationToken cancellationToken) {
      if (sagaId != expectedSagaId) {
        throw new InvalidOperationException($"Resolver queried for {sagaId} but stub set up for {expectedSagaId}.");
      }
      return Task.FromResult(agg);
    }
    public Task<IReadOnlyList<SagaItemModel>> GetItemsAsync(Guid sagaId, CancellationToken cancellationToken) =>
      throw new NotImplementedException();
  }
}
