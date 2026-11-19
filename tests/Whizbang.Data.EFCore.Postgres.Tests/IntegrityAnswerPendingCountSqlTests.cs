// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2184: the paced repair drain stops claiming while its cap's worth of ledger buckets await the answer to a request.
/// The count it asks for (migration 371) counts the buckets whose answer is pending at the drain's instant, reads no
/// further than the cap, and once the framework index is built (concurrently, after the migrations) is answered from
/// that index alone, because the drain asks it every tick it has tokens to spend.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/371_RepairAnswersAreCapped.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/ConcurrentIndexBuild.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#outstanding-repair-answers-are-capped</docs>
[Category("Shard5")]
public class IntegrityAnswerPendingCountSqlTests : EFCoreTestBase {
  private const string ORDERS = "Contracts.Orders.OrderPlaced, Contracts";
  private const string INDEX = "idx_integrity_ledger_answer_pending";
  private static readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private IWorkCoordinator _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private async Task<IntegrityRepairLedger.DivergenceKey> _trackAsync(Guid origin) {
    var key = new IntegrityRepairLedger.DivergenceKey(origin, "tenant-a", ORDERS, TrackedGuid.New().Value);
    _ = await _coordinator().IntegrityTryBeginReportBatchAsync(
      origin, [new IntegrityReportObservation(key, 10, 20, 0, 0, IsDeficit: true)], _now, TimeSpan.FromMinutes(5));
    return key;
  }

  [Test]
  public async Task Count_TheBucketsAwaitingAnAnswer_AtTheDrainsInstantAsync() {
    var origin = TrackedGuid.New().Value;
    var asked = new[] { await _trackAsync(origin), await _trackAsync(origin), await _trackAsync(origin) };
    _ = await _trackAsync(origin);   // never asked: no answer on the way
    _ = await _coordinator().IntegrityMarkAnswerPendingAsync(origin, asked, null, null, _now, TimeSpan.FromHours(3));

    var pending = await _coordinator().IntegrityCountAnswerPendingAsync(_now.AddMinutes(1), 500);
    var afterTimeout = await _coordinator().IntegrityCountAnswerPendingAsync(_now.AddHours(3).AddSeconds(1), 500);

    await Assert.That(pending).IsEqualTo(3).Because("three buckets were asked for and none has been answered");
    await Assert.That(afterTimeout).IsEqualTo(0)
      .Because("an answer that did not land inside the timeout is no longer on the way; the bucket may be asked again");
  }

  [Test]
  public async Task Count_ReadsNoFurtherThanTheCapAsync() {
    var origin = TrackedGuid.New().Value;
    var asked = new[] { await _trackAsync(origin), await _trackAsync(origin), await _trackAsync(origin) };
    _ = await _coordinator().IntegrityMarkAnswerPendingAsync(origin, asked, null, null, _now, TimeSpan.FromHours(3));

    var capped = await _coordinator().IntegrityCountAnswerPendingAsync(_now, 2);

    await Assert.That(capped).IsEqualTo(2).Because("the drain only needs to know whether the cap is reached");
  }

  [Test]
  public async Task Count_ThroughAThroughputGatedCoordinator_CountsAsync() {
    var origin = TrackedGuid.New().Value;
    var key = await _trackAsync(origin);
    _ = await _coordinator().IntegrityMarkAnswerPendingAsync(origin, [key], null, null, _now, TimeSpan.FromHours(3));
    await using var ctx = CreateDbContext();
    using var gate = new WorkCoordinatorGate(maxConcurrent: 1,
      logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkCoordinatorGate>.Instance);
    IWorkCoordinator coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), gate: gate);

    await Assert.That(await coordinator.IntegrityCountAnswerPendingAsync(_now, 10)).IsEqualTo(1);
  }

  [Test]
  public async Task Count_OnceTheIndexIsBuilt_IsAnsweredFromItAsync() {
    await using (var connection = new NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync();
      await ConcurrentIndexBuild.EnsureAsync(
        connection, "public", ConcurrentIndexBuild.FrameworkIndexes.Single(i => i.Name == INDEX), 120);
    }
    await using var dbContext = CreateDbContext();
    var db = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    await db.OpenAsync();
    await using (var off = db.CreateCommand()) {
      off.CommandText = "SET enable_seqscan = off";
      await off.ExecuteNonQueryAsync();
    }
    // The deployed function's own body, its parameters bound, so the plan is the one the drain's count gets.
    string body;
    await using (var source = db.CreateCommand()) {
      source.CommandText = "SELECT prosrc FROM pg_proc WHERE proname = 'wh_integrity_count_answer_pending'";
      body = (string)(await source.ExecuteScalarAsync())!;
    }
    await using var explain = db.CreateCommand();
#pragma warning disable S2077 // EXPLAIN of the framework's own deployed function body; no input reaches it.
    explain.CommandText = "EXPLAIN " + body.Trim().TrimEnd(';')
      .Replace("p_now", "now()", StringComparison.Ordinal).Replace("p_limit", "500", StringComparison.Ordinal);
#pragma warning restore S2077
    var plan = new List<string>();
    await using (var reader = await explain.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        plan.Add(reader.GetString(0));
      }
    }

    await Assert.That(string.Join("\n", plan)).Contains(INDEX, StringComparison.Ordinal)
      .Because("the drain asks every tick; a count that scanned the ledger would cost the same as the work it paces");
  }
}
