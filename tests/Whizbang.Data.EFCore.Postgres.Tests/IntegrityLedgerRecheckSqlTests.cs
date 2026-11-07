// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2115: the idle ledger re-check's claim, against the real ledger. It takes the rows whose last report is older than
/// the threshold, oldest report first and bounded; a row it claimed is not claimed again until that claim is older
/// than the threshold too; it skips rows it could not ask about (another origin, no stamped window, the type lane);
/// and it spends nothing: no repair attempt, no pending answer.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/341_IdleLedgerRecheck.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityLedgerRecheckSqlTests : EFCoreTestBase {
  // Fixed instants, not the host clock: every function under test takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(60);
  private static readonly TimeSpan _threshold = TimeSpan.FromMinutes(120);

  [Test]
  public async Task Claim_TakesTheStalestRowsFirst_BoundedAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var oldest = await _staleRowAsync(coordinator, origin, reportedAt: _t0);
    var middle = await _staleRowAsync(coordinator, origin, reportedAt: _t0.AddMinutes(10));
    _ = await _staleRowAsync(coordinator, origin, reportedAt: _t0.AddMinutes(20));
    _ = await _staleRowAsync(coordinator, origin, reportedAt: _t0.AddDays(1));   // reported recently: not stale yet
    var now = _t0.AddDays(1).AddMinutes(30);

    var claimed = await _claimAsync(coordinator, origin, now, 2);

    await Assert.That(claimed.Select(c => c.StreamId).ToList()).IsEquivalentTo([oldest, middle])
      .Because("the stalest reports are re-checked first, and one claim never takes more than its bound");
    await Assert.That(claimed.All(c => c.WindowFrom == 100 && c.WindowUntil == 500)).IsTrue()
      .Because("each row comes back with the window its bucket was compared over");
    var rest = await _claimAsync(coordinator, origin, now, 10);
    await Assert.That(rest.Count).IsEqualTo(1)
      .Because("the third stale row is left; the recently reported one is not stale, so the re-check stops there");
  }

  [Test]
  public async Task Claim_ARowItClaimed_IsNotClaimedAgainUntilTheThresholdPassesAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    _ = await _staleRowAsync(coordinator, origin, reportedAt: _t0);
    var first = _t0.AddDays(1);
    _ = await _claimAsync(coordinator, origin, first, 10);

    var soon = first.AddMinutes(30);
    var tooSoon = await _claimAsync(coordinator, origin, soon, 10);
    var later = first + _threshold + TimeSpan.FromMinutes(1);
    var again = await _claimAsync(coordinator, origin, later, 10);

    await Assert.That(tooSoon).IsEmpty()
      .Because("a row a re-check just asked about waits for that answer instead of being asked every tick");
    await Assert.That(again.Count).IsEqualTo(1)
      .Because("a row still stale a threshold after its last re-check is asked again");
  }

  [Test]
  public async Task Claim_SkipsRowsItCannotAsk_AndSpendsNothingAsync() {
    var coordinator = _coordinator();
    var origin = TrackedGuid.New().Value;
    var other = TrackedGuid.New().Value;
    var askable = await _staleRowAsync(coordinator, origin, reportedAt: _t0);
    _ = await _staleRowAsync(coordinator, other, reportedAt: _t0);                     // an origin that cannot be asked
    _ = await _staleRowAsync(coordinator, origin, reportedAt: _t0, stampWindow: false); // no window to ask over
    _ = await _staleRowAsync(coordinator, origin, reportedAt: _t0, stream: Guid.Empty); // the type lane
    var now = _t0.AddDays(1);

    var claimed = await _claimAsync(coordinator, origin, now, 10);

    await Assert.That(claimed.Select(c => c.StreamId).ToList()).IsEquivalentTo([askable]);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT repair_attempts, last_repair_at IS NULL AND answer_pending_until IS NULL, rechecked_at
      FROM wh_integrity_ledger WHERE stream_id = $1
      """;
    cmd.Parameters.Add(new NpgsqlParameter { Value = askable });
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    await Assert.That(reader.GetInt32(0)).IsEqualTo(0).Because("a re-check is a compare: it spends no repair attempt");
    await Assert.That(reader.GetBoolean(1)).IsTrue().Because("and asks for no repair, so no answer is pending");
    await Assert.That(reader.GetFieldValue<DateTimeOffset>(2)).IsEqualTo(now);
  }

  [Test]
  public async Task Claim_ThroughTheThroughputGate_AnswersTheSameAsync() {
    using var gate = new WorkCoordinatorGate(4, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    var coordinator = _coordinator(gate);
    var origin = TrackedGuid.New().Value;
    var stale = await _staleRowAsync(coordinator, origin, reportedAt: _t0);
    var now = _t0.AddDays(1);

    var claimed = await _claimAsync(coordinator, origin, now, 10);

    await Assert.That(claimed.Single().StreamId).IsEqualTo(stale)
      .Because("the gate bounds how many coordinator calls run at once; it does not change the answer");
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinatorGate? gate = null) =>
    new(CreateDbContext(), Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);

  // Through the interface, as the re-check worker calls it.
  private static Task<IReadOnlyList<IntegrityRepairDrainItem>> _claimAsync(
      IWorkCoordinator coordinator, Guid origin, DateTimeOffset now, int limit) =>
    coordinator.IntegrityClaimLedgerRecheckAsync([origin], now - _threshold, now, limit);

  /// <summary>Reports one deficit bucket at <paramref name="reportedAt"/>, stamped with the window [100, 500) unless
  /// told not to, and returns its stream.</summary>
  private static async Task<Guid> _staleRowAsync(
      EFCoreWorkCoordinator<WorkCoordinationDbContext> coordinator, Guid origin, DateTimeOffset reportedAt,
      bool stampWindow = true, Guid? stream = null) {
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, "Contracts.RecheckProbe", stream ?? TrackedGuid.New().Value);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 11, 21, 0, 0, IsDeficit: true, DeficitEvents: 1)], reportedAt, _cooldown)
      ?? throw new InvalidOperationException("the report batch failed");
    if (stampWindow) {
      await coordinator.IntegrityStampRepairWindowsAsync(origin, [key], 100, 500);
    }
    return key.StreamId;
  }
}
