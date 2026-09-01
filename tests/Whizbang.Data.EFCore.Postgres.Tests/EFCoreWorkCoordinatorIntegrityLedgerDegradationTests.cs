using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// What the integrity ledger surface does when its SQL is unavailable. Every one of these calls
/// wraps its command in a catch that degrades instead of throwing, and each degrades DIFFERENTLY
/// and deliberately — those directions are the contract, and none of them was exercised.
/// <para>
/// The pairing that matters most: reporting a divergence fails OPEN (a ledger outage must not
/// stop a service from reporting what it found) while beginning a repair fails CLOSED (a service
/// that cannot consult the ledger must not repair blind, or two pods repair the same bucket at
/// once). Flip either one and the failure only shows up during an actual outage, which is the
/// worst possible moment to discover it.
/// </para>
/// </summary>
/// <remarks>
/// The outage is produced by dropping the function the call depends on. <c>EFCoreTestBase</c>
/// builds a fresh database per test, so a dropped function is invisible to everything else — and
/// it is a truer fault than a mocked connection, because it exercises the real Npgsql exception
/// the catch was written against.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class EFCoreWorkCoordinatorIntegrityLedgerDegradationTests : EFCoreTestBase {

  private static readonly Guid _originId = Guid.Parse("33333333-3333-3333-3333-333333333333");

  private CapturingLogger _log = null!;

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _build(WorkCoordinationDbContext ctx) {
    _log = new CapturingLogger();
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, JsonContextRegistry.CreateCombinedOptions(), _log);
  }

  /// <summary>
  /// Every degradation here is also a LOG. Asserting it is what separates "the call degraded"
  /// from "the call happened to return the same value it returns on a healthy ledger" — several
  /// of these returns (false, empty, the empty snapshot) are indistinguishable from a normal
  /// quiet result, so without this the test would pass with the fault removed.
  /// </summary>
  private async Task _assertDegradedLoudlyAsync() =>
    await Assert.That(_log.MessagesFor(LogLevel.Warning)).IsNotEmpty()
      .Because("a ledger failure that degrades silently is how a broken ledger masquerades as a "
             + "working one — and it is how this test would pass without the fault");

  private static IntegrityRepairLedger.DivergenceKey _key() =>
    new(_originId, "tenant-1", "OrderCreated", Guid.Parse("44444444-4444-4444-4444-444444444444"));

  /// <summary>Removes a function so the next call to it raises a real Postgres error.</summary>
  private async Task _dropFunctionAsync(string name) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    // Signature-agnostic: drop every overload the schema happens to carry.
    cmd.CommandText = $"""
      DO $$
      DECLARE r record;
      BEGIN
        FOR r IN SELECT oid::regprocedure AS sig FROM pg_proc WHERE proname = '{name}'
        LOOP EXECUTE 'DROP FUNCTION ' || r.sig || ' CASCADE'; END LOOP;
      END $$;
      """;
    await cmd.ExecuteNonQueryAsync();
  }

  // ── The fail-open / fail-closed pair ────────────────────────────────────

  [Test]
  public async Task IntegrityTryBeginReport_WithTheLedgerUnavailable_FailsOpenAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_try_begin_report");

    var began = await _build(ctx).IntegrityTryBeginReportAsync(
      _key(), originLo: 1, originHi: 10, localLo: 1, localHi: 5,
      DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

    await Assert.That(began).IsTrue()
      .Because("a ledger outage must not stop a service reporting a divergence it has already "
             + "found — the cooldown the ledger provides is an optimization, not a gate");
    await _assertDegradedLoudlyAsync();
  }

  [Test]
  public async Task IntegrityTryBeginRepair_WithTheLedgerUnavailable_FailsClosedAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_try_begin_repair");

    var began = await _build(ctx).IntegrityTryBeginRepairAsync(
      _key(), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), maxAttempts: 5);

    await Assert.That(began).IsFalse()
      .Because("the ledger is what serializes repair across pods and enforces the attempt cap; "
             + "repairing without it means repairing blind, in parallel, forever");
    await _assertDegradedLoudlyAsync();
  }

  [Test]
  public async Task IntegrityMarkHealed_WithTheLedgerUnavailable_DoesNotThrowAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_mark_healed");

    // The caller is a completion path — throwing here would fail work that actually succeeded.
    await _build(ctx).IntegrityMarkHealedAsync(_key());
    await _assertDegradedLoudlyAsync();
  }

  // ── Batch calls: null means "fall back", not "nothing to do" ────────────

  [Test]
  public async Task IntegrityTryBeginReportBatch_WithTheBatchFunctionMissing_ReturnsNullSoTheCallerFallsBackAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_try_begin_report_batch");

    var results = await _build(ctx).IntegrityTryBeginReportBatchAsync(
      _originId,
      [new IntegrityReportObservation(_key(), 1, 10, 1, 5)],
      DateTimeOffset.UtcNow,
      TimeSpan.FromMinutes(5));

    await Assert.That(results).IsNull()
      .Because("null is the signal to retry this chunk one key at a time; an empty list would "
             + "read as 'no key may proceed' and silently drop every report in the batch");
    await _assertDegradedLoudlyAsync();
  }

  [Test]
  public async Task IntegrityMarkHealedBatchWithAges_WithTheBatchFunctionMissing_ReturnsNullAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_mark_healed_batch");

    var ages = await _build(ctx).IntegrityMarkHealedBatchWithAgesAsync(_originId, [_key()]);

    await Assert.That(ages).IsNull()
      .Because("the same fallback signal — these buckets are healed and must still be marked, "
             + "one key at a time");
    await _assertDegradedLoudlyAsync();
  }

  [Test]
  public async Task IntegrityMarkHealedBatch_WithTheBatchFunctionMissing_ReportsFailureAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_mark_healed_batch");

    var marked = await _build(ctx).IntegrityMarkHealedBatchAsync(_originId, [_key()]);

    await Assert.That(marked).IsFalse()
      .Because("the bool overload folds the null into 'did not happen', which is what makes the "
             + "caller fall back rather than believe the buckets were marked");
    await _assertDegradedLoudlyAsync();
  }

  // ── Drain, stamping, and gauges ─────────────────────────────────────────

  [Test]
  public async Task IntegrityClaimRepairDrain_WithTheLedgerUnavailable_DispatchesNothingThisPassAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_claim_repair_drain");

    var items = await _build(ctx).IntegrityClaimRepairDrainAsync(
      [_originId], DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), maxAttempts: 5, limit: 10);

    await Assert.That(items).IsEmpty()
      .Because("an empty claim is a pass that dispatched nothing, which the drain retries next "
             + "tick — throwing would take the maintenance loop down with the ledger");
    await _assertDegradedLoudlyAsync();
  }

  [Test]
  public async Task IntegrityStampRepairWindows_WithTheLedgerUnavailable_DoesNotThrowAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_stamp_repair_windows");

    // Stamping is an optimization: without it the drain derives a coarser range per origin, so
    // the work still happens. Failing the caller would turn a slower repair into no repair.
    await _build(ctx).IntegrityStampRepairWindowsAsync(_originId, [_key()], windowFrom: 1, windowUntil: 10);
    await _assertDegradedLoudlyAsync();
  }

  [Test]
  public async Task GetIntegrityLedgerSummary_WithTheLedgerUnavailable_ReturnsTheEmptySnapshotAsync() {
    await using var ctx = CreateDbContext();
    await _dropFunctionAsync("wh_integrity_ledger_summary");

    var snapshot = await _build(ctx).GetIntegrityLedgerSummaryAsync(maxRepairAttempts: 5);

    await Assert.That(snapshot).IsEqualTo(LedgerGaugeSnapshot.Empty)
      .Because("the gauges fall back to the empty snapshot rather than failing the metrics pass — "
             + "which is exactly why the failure is logged, since convergence then reads as "
             + "healthy while the ledger is broken");
    await _assertDegradedLoudlyAsync();
  }

  private sealed class CapturingLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<(LogLevel Level, string Message)> _entries = [];
    private readonly Lock _lock = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_lock) {
        _entries.Add((logLevel, formatter(state, exception)));
      }
    }

    public List<string> MessagesFor(LogLevel level) {
      lock (_lock) {
        return [.. _entries.Where(e => e.Level == level).Select(e => e.Message)];
      }
    }
  }
}
