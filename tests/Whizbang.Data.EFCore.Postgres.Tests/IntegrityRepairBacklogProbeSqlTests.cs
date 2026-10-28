// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #1792: the paced repair drain's admission probe against real Postgres. One round trip answers
/// how large the drain's backlog already is (per-stream ledger rows, every origin, counted only up
/// to the caller's bound) and which of a comparison's keys the ledger already tracks.
/// </summary>
/// <remarks>
/// Every arm of the probe (answer, failure, cancellation) is taken inside this class, because the CI
/// coverage merge keeps the best per-line condition count of any single shard. The failure is a real
/// one: the ledger table is renamed in this test's own database, so the query raises the Postgres
/// error the catch was written against.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#repair-caps</docs>
[Category("Integration")]
[Category("Shard1")]
public class IntegrityRepairBacklogProbeSqlTests : EFCoreTestBase {

  private CapturingLogger _log = null!;

  private IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) {
    _log = new CapturingLogger();
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions(), _log);
  }

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin, Guid stream, string? tenant = "tenant-a") =>
    new(origin, tenant, "Contracts.BacklogProbe", stream);

  private static IntegrityReportObservation _obs(IntegrityRepairLedger.DivergenceKey key) => new(key, 11, 21, 0, 0, IsDeficit: true);

  [Test]
  public async Task Probe_CountsTheBacklogAcrossOrigins_AndFlagsTrackedKeysInOrderAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var other = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var tracked = _key(origin, Guid.NewGuid());
    var trackedNoTenant = _key(origin, Guid.NewGuid(), tenant: null);
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, [_obs(tracked), _obs(trackedNoTenant)], now, TimeSpan.FromMinutes(60));
    var shared = Guid.NewGuid();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(other, [_obs(_key(other, shared))], now, TimeSpan.FromMinutes(60));
    // The synthetic bulk lane (stream zero) dispatches through bulk escalation, never the drain.
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, [_obs(_key(origin, Guid.Empty))], now, TimeSpan.FromMinutes(60));
    var fresh = _key(origin, Guid.NewGuid());
    // A stream tracked only for ANOTHER origin: not the same divergence. The probe's origin
    // argument scopes every key, as in the ledger's other batch calls.
    var otherOriginsStream = _key(origin, shared);

    var probe = await coordinator.IntegrityProbeRepairBacklogAsync(
      origin, [fresh, tracked, trackedNoTenant, otherOriginsStream], backlogCap: 100);

    await Assert.That(probe).IsNotNull();
    await Assert.That(probe!.Backlog).IsEqualTo(3L)
      .Because("the backlog is every per-stream row the drain may claim, whichever origin it is for");
    await Assert.That(probe.Tracked).IsEquivalentTo([false, true, true, false], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("flag i answers key i, a null tenant matches the stored empty scope, and the key includes the origin");
    await Assert.That(_log.Warnings).IsEmpty();
  }

  [Test]
  public async Task Probe_CountsAndTracksOnlyDeficits_TheRowsTheDrainCanClaimAsync() {
    // #1842: a mismatch, a surplus or a row of unknown kind is never claimed, so it is not part of the
    // backlog, and a deficit found where the ledger holds one of them is new to the drain.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var deficit = _key(origin, Guid.NewGuid());
    var surplus = _key(origin, Guid.NewGuid());
    var unknown = _key(origin, Guid.NewGuid());
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, [
      _obs(deficit),
      new IntegrityReportObservation(surplus, 11, 21, 31, 41, IsDeficit: false),
    ], now, TimeSpan.FromMinutes(60));
    _ = await coordinator.IntegrityTryBeginReportAsync(unknown, 11, 21, 0, 0, now, TimeSpan.FromMinutes(60));

    var probe = await coordinator.IntegrityProbeRepairBacklogAsync(origin, [deficit, surplus, unknown], backlogCap: 100);

    await Assert.That(probe!.Backlog).IsEqualTo(1L)
      .Because("only the deficit is a queued repair; the others would fill the bound and defer real deficits");
    await Assert.That(probe.Tracked).IsEquivalentTo([true, false, false], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("re-admitting a non-deficit row as a deficit adds a repair to the backlog, so it has to count");
  }

  [Test]
  public async Task Probe_AnUnhealableDeficit_IsTrackedButNotPartOfTheBacklogAsync() {
    // The drain never claims a bucket marked unhealable (249), so it is not a queued repair. It stays
    // tracked: re-sighting it unchanged adds nothing, and counting it as new would let buckets repair
    // cannot change take the per-comparison allowance on every audit.
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var unhealable = _key(origin, Guid.NewGuid());
    var repairable = _key(origin, Guid.NewGuid());
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(
      origin, [_obs(unhealable), _obs(repairable)], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60));
    _ = await coordinator.IntegrityMarkUnhealableBatchAsync(origin, [unhealable], DateTimeOffset.UtcNow);

    var probe = await coordinator.IntegrityProbeRepairBacklogAsync(origin, [unhealable, repairable], backlogCap: 100);

    await Assert.That(probe!.Backlog).IsEqualTo(1L)
      .Because("a bucket the drain will never claim cannot fill the bound and defer a deficit it would claim");
    await Assert.That(probe.Tracked).IsEquivalentTo([true, true], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task Probe_StopsCountingAtTheBound_SoTheReadStaysBoundedAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var keys = Enumerable.Range(0, 5).Select(_ => _key(origin, Guid.NewGuid())).ToList();
    _ = await coordinator.IntegrityTryBeginReportBatchAsync(origin, keys.ConvertAll(_obs), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(60));

    var probe = await coordinator.IntegrityProbeRepairBacklogAsync(origin, [keys[0]], backlogCap: 3);

    await Assert.That(probe!.Backlog).IsEqualTo(3L)
      .Because("the caller only needs to know whether the bound is reached, so the count stops there");
    await Assert.That(probe.Tracked).IsEquivalentTo([true]);
  }

  [Test]
  public async Task Probe_WithTheLedgerUnavailable_AnswersUnsupported_AndSaysSoAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "ALTER TABLE wh_integrity_ledger RENAME TO wh_integrity_ledger_gone";
      await cmd.ExecuteNonQueryAsync();
    }

    var probe = await coordinator.IntegrityProbeRepairBacklogAsync(Guid.NewGuid(), [_key(Guid.NewGuid(), Guid.NewGuid())], backlogCap: 10);

    await Assert.That(probe).IsNull()
      .Because("a probe that cannot read the ledger degrades to the per-comparison cap, never a failed comparison");
    await Assert.That(_log.Warnings).IsNotEmpty()
      .Because("a degraded probe looks exactly like an unsupported one unless it is logged");
  }

  [Test]
  [Timeout(60000)]
  [SuppressMessage("Redundancy", "RCS1163:Unused parameter",
    Justification = "TUnit requires the cancellation token parameter alongside [Timeout] (TUnit0015) and injects it; this case has nothing long-running of its own to pass it to.")]
  [SuppressMessage("Style", "IDE0060:Remove unused parameter",
    Justification = "As RCS1163: required by [Timeout] and supplied by the framework.")]
  public async Task Probe_CanceledDuringShutdown_PropagatesInsteadOfReadingAsUnsupportedAsync(CancellationToken cancellationToken) {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await coordinator.IntegrityProbeRepairBacklogAsync(
        Guid.NewGuid(), [_key(Guid.NewGuid(), Guid.NewGuid())], backlogCap: 10, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("a swallowed cancellation reads as 'unsupported' and the comparison carries on admitting");
  }

  private sealed class CapturingLogger : ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings {
      get {
        lock (_warnings) {
          return [.. _warnings];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (logLevel == LogLevel.Warning) {
        lock (_warnings) {
          _warnings.Add(formatter(state, exception));
        }
      }
    }
  }
}
