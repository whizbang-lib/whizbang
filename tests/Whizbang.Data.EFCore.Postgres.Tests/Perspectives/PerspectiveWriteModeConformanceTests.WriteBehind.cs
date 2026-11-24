// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// What only the write-behind mode (#2247) has to prove, beyond the conformance cases every mode passes: a hot stream's
/// batches are held and written once; nothing about a held batch is durable or reported until its flush; a crash loses
/// only the held models, and the stream replays from its checkpoint; and a writer outside the runner that changes a row
/// while a model of it is held keeps its write.
/// </summary>
/// <remarks>
/// The flush interval here is the longest the default lease allows, so nothing is flushed by time: each test flushes
/// when it chooses (<see cref="Whizbang.Core.Workers.PerspectiveWorker.FlushWriteBehindAsync"/>) and waits on the worker's
/// hold signal (<see cref="Whizbang.Core.Workers.PerspectiveWorker.OnWriteBehindHeld"/>), never on time.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
public partial class PerspectiveWriteModeConformanceTests {
  /// <summary>Half of the default lease (300 s) less its grace (30 s): no timer flush within a test.</summary>
  private const int NO_TIMER_FLUSH_MS = 135_000;

  [Test]
  public async Task WriteBehind_AHotStreamsBatches_AreHeld_ThenWrittenOnceAsync() {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var stream = _streams(1)[0];
    await using var pipeline = _compose(PerspectiveWriteMode.WriteBehind, flushIntervalMs: NO_TIMER_FLUSH_MS);
    var held = _heldSignals(pipeline, stream);
    await pipeline.RegisterAsync(conn, ct);
    var first = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "a" }, ct);

    await pipeline.HandOffAsync([stream], ct);
    await pipeline.StartWorkerAsync(ct);
    await held.WaitForAsync(1);
    var second = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 7, Owner = "b" }, ct);
    await held.WaitForAsync(2);

    await Assert.That(await _ledgerExistsAsync(conn, stream, ct)).IsFalse()
      .Because("both batches are held: nothing is written until the flush");
    await Assert.That(_ledgerWritesOf(pipeline, stream)).IsEmpty();
    await Assert.That(pipeline.PerspectiveWorker.WriteBehindModels).IsEqualTo(2)
      .Because("one held model per (stream, perspective): the ledger and the split ledger");

    await pipeline.PerspectiveWorker.FlushWriteBehindAsync(ct);
    await pipeline.Completion.WaitForAllAsync([.. first, .. second]);

    await _assertLedgerAsync(conn, stream, balance: 17, postings: 2, owner: "b", ct);
    var writes = _ledgerWritesOf(pipeline, stream);
    await Assert.That(writes).Count().IsEqualTo(1).Because("the two batches are written with one merged write");
    await Assert.That(writes[0].Operation).IsEqualTo(PerspectiveWriteOperation.Insert);
    await Assert.That(pipeline.PerspectiveWorker.WriteBehindModels).IsEqualTo(0);
  }

  [Test]
  public async Task WriteBehind_ARowAnotherWriterChangesWhileItsModelIsHeld_KeepsBothWritesAsync() {
    // A writer outside the runner (a collective UPDATE, a consumer's own SQL) changes the row while a model of it is held.
    // The merged write is checked against the row's version as the database had it, meets the change as a conflict, and
    // each held batch is re-applied on the row it finds.
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var stream = _streams(1)[0];
    ServiceInstanceProvider instance;
    await using (var seed = _compose(PerspectiveWriteMode.SetBased)) {
      instance = seed.InstanceProvider;
      await seed.RegisterAsync(conn, ct);
      await seed.RunAsync([stream], await seed.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "a" }, ct), ct);
    }
    await using var pipeline = _compose(PerspectiveWriteMode.WriteBehind, instance, NO_TIMER_FLUSH_MS);
    var held = _heldSignals(pipeline, stream);
    var work = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 5, Owner = "a" }, ct);
    await pipeline.HandOffAsync([stream], ct);
    await pipeline.StartWorkerAsync(ct);
    await held.WaitForAsync(1);

    await _executeAsync(
      $"UPDATE {LEDGER} SET data = jsonb_set(data, '{{Balance}}', to_jsonb((data->>'Balance')::numeric + 1000)) WHERE id = @id", stream);
    await pipeline.PerspectiveWorker.FlushWriteBehindAsync(ct);
    await pipeline.Completion.WaitForAllAsync(work);

    await _assertLedgerAsync(conn, stream, balance: 1015, postings: 2, owner: "a", ct);
  }

  [Test]
  public async Task WriteBehind_ACrash_LosesOnlyTheHeldModels_AndTheStreamReplaysFromItsCheckpointAsync() {
    // The instance holding a model dies: its rows' leases are released as an abandoned instance's are, another instance
    // claims the stream and applies it from the checkpoint the held batch never moved. When the first instance's flush
    // runs anyway, it meets the other's row and applies nothing twice.
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var stream = _streams(1)[0];
    await using var crashed = _compose(PerspectiveWriteMode.WriteBehind, flushIntervalMs: NO_TIMER_FLUSH_MS);
    var held = _heldSignals(crashed, stream);
    await crashed.RegisterAsync(conn, ct);
    var work = await crashed.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "a" }, ct);
    await crashed.HandOffAsync([stream], ct);
    await crashed.StartWorkerAsync(ct);
    await held.WaitForAsync(1);
    await crashed.ClaimWorker.StopAsync(ct);

    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @id AND last_event_id IS NOT NULL", stream, ct)).IsEqualTo(0L)
      .Because("a held batch never moves its checkpoint");
    await _executeAsync("UPDATE wh_perspective_events SET instance_id = NULL, lease_expiry = NULL WHERE stream_id = @id", stream);

    await using (var survivor = _compose(PerspectiveWriteMode.SetBased)) {
      await survivor.RegisterAsync(conn, ct);
      await survivor.RunAsync([stream], work, ct);
    }
    await _assertLedgerAsync(conn, stream, balance: 10, postings: 1, owner: "a", ct);

    await crashed.PerspectiveWorker.FlushWriteBehindAsync(ct);
    await _assertLedgerAsync(conn, stream, balance: 10, postings: 1, owner: "a", ct);
  }

  /// <summary>The ledger writes the suite's flush policy was handed for one stream.</summary>
  private static List<PendingPerspectiveWrite> _ledgerWritesOf(Pipeline pipeline, Guid stream) =>
    [.. pipeline.Policy.Written.Where(w => w.StreamId == stream
      && w.Table.Contains(LEDGER, StringComparison.Ordinal) && !w.Table.Contains("split", StringComparison.Ordinal))];

  /// <summary>Counts the ledger perspective's held batches of a stream, and signals each count as it is reached.</summary>
  private static HeldSignals _heldSignals(Pipeline pipeline, Guid stream) {
    var signals = new HeldSignals();
    var ledger = TypeNameFormatter.GetPerspectiveName(typeof(ConformanceLedgerPerspective));
    pipeline.PerspectiveWorker.OnWriteBehindHeld += key => {
      if (key.StreamId == stream && key.PerspectiveName == ledger) {
        signals.Add();
      }
    };
    return signals;
  }

  private sealed class HeldSignals {
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _reached = new();
    private int _count;

    public void Add() => _signal(Interlocked.Increment(ref _count)).TrySetResult();

    public async Task WaitForAsync(int count) {
      try {
        await _signal(count).Task.WaitAsync(_signalDeadline);
      } catch (TimeoutException) {
        throw new TimeoutException($"Held {Volatile.Read(ref _count)} batch(es), waited for {count}");
      }
    }

    private TaskCompletionSource _signal(int count) =>
      _reached.GetOrAdd(count, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }
}
