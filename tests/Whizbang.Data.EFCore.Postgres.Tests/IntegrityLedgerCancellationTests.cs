using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the integrity ledger's SQL wrappers do when a shutdown lands mid-call.
/// <para>
/// Every method here catches broadly on purpose. The ledger is bookkeeping about repair progress,
/// not the repair itself, so a failed round trip degrades rather than throws: the batch wrappers
/// return <see langword="null"/> so the caller falls back to the single-key functions, the drain
/// claim returns an empty list so the pass simply dispatches nothing, and the window stamp gives
/// up so the drain derives a coarser range. Each of those is a correct answer to "the database
/// would not answer".
/// </para>
/// <para>
/// None of them is a correct answer to "the host is shutting down". A swallowed cancellation is
/// indistinguishable from a real negative result at the call site, so the caller reads it as
/// settled fact and acts: it dispatches nothing and records the pass as clean, or it falls back to
/// per-key calls that each have to be cancelled in turn on the way out. That is why every one of
/// these wrappers rethrows cancellation ahead of its wide catch, and why the wide catch alone
/// having a test is not enough.
/// </para>
/// </summary>
/// <remarks>
/// Live PostgreSQL, because these are thin wrappers over plpgsql functions and the cancellation
/// has to travel through the real command execution the wrapper is built around. The token is
/// cancelled before the call: the guard clauses these methods have are on their arguments, not on
/// the token, so every call below still enters the try and reaches the arm under test.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard1")]
public class IntegrityLedgerCancellationTests : EFCoreTestBase {

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin) =>
    new(origin, TenantScope: "tenant-a", EventType: "OrderPlaced", StreamId: (Guid)TrackedGuid.NewMedo());

  [Test]
  [Timeout(60000)]
  public async Task ASingleReportGrant_CanceledDuringShutdown_PropagatesInsteadOfReadingAsRefusedAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await coordinator.IntegrityTryBeginReportAsync(
        _key(Guid.CreateVersion7()), originLo: 10, originHi: 20, localLo: 10, localHi: 18,
        DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the wide catch returns the fail-open default, which the caller cannot tell apart "
             + "from the ledger genuinely refusing the grant");
  }

  [Test]
  [Timeout(60000)]
  public async Task ABatchReportGrant_CanceledDuringShutdown_PropagatesInsteadOfFallingBackPerKeyAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.CreateVersion7();
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await coordinator.IntegrityTryBeginReportBatchAsync(
        origin,
        [new IntegrityReportObservation(_key(origin), OriginLo: 10, OriginHi: 20, LocalLo: 10, LocalHi: 18)],
        DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("null means 'batch unavailable, use the single-key path', so swallowing a shutdown "
             + "here sends the caller into a per-key loop that has to be cancelled all over again");
  }

  [Test]
  [Timeout(60000)]
  public async Task AWindowStamp_CanceledDuringShutdown_PropagatesInsteadOfWideningTheDrainAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.CreateVersion7();
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    // Non-empty keys: the method returns early on an empty list, which would exit before the arm.
    await Assert.That(async () => await coordinator.IntegrityStampRepairWindowsAsync(
        origin, [_key(origin)], windowFrom: 10, windowUntil: 20, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("a swallowed stamp silently costs the next drain a coarser, more expensive range, "
             + "and nothing about a shutdown says the window was wrong");
  }

  [Test]
  [Timeout(60000)]
  public async Task ARepairDrainClaim_CanceledDuringShutdown_PropagatesInsteadOfLookingIdleAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    // Non-empty origins and a positive limit: both are guarded ahead of the try.
    await Assert.That(async () => await coordinator.IntegrityClaimRepairDrainAsync(
        [Guid.CreateVersion7()], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30),
        maxAttempts: 3, limit: 10, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("an empty claim is how this method says 'nothing is due', so a swallowed shutdown "
             + "records the pass as clean when it never looked");
  }

  [Test]
  [Timeout(60000)]
  public async Task AHealedBatchMark_CanceledDuringShutdown_PropagatesInsteadOfLosingTheAgesAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.CreateVersion7();
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await coordinator.IntegrityMarkHealedBatchWithAgesAsync(
        origin, [_key(origin)], stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("null sends the caller to the single-key functions, so a shutdown swallowed here "
             + "becomes a second round of calls against a database this pod is leaving");
  }

  [Test]
  [Timeout(60000)]
  public async Task ALedgerSummaryRead_CanceledDuringShutdown_PropagatesInsteadOfPublishingAGaugeAsync(
      CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await coordinator.GetIntegrityLedgerSummaryAsync(
        maxRepairAttempts: 3, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the degraded snapshot is published as a gauge, and one shaped by a shutdown reads "
             + "later as a real measurement of a ledger nobody was reading");
  }
}
