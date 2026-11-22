// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A rebuild reads its streams' events a batch at a time on the EF Core driver (#2513): one statement reads the events
/// of a batch of streams, so the reads of a rebuild of N streams count N / batch size, not N. Counted at Npgsql, never
/// timed, and the rebuilt rows are compared with the rows the per-stream rebuild writes.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#batched-reads</docs>
public partial class BlueGreenRebuildIntegrationTests {
  [Test]
  public async Task BlueGreen_ReadsEachBatchOfStreamsWithOneStatement_AndRebuildsTheRowsThePerStreamReadsDoAsync() {
    Guid[] streams = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];
    await using var perStream = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask,
      rebuildOptions: new PerspectiveRebuildOptions { StreamReadBatchSize = 1 }, observed: false);
    await using var batched = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask,
      rebuildOptions: new PerspectiveRebuildOptions { StreamReadBatchSize = 2 }, observed: false);
    await _appendAsync(perStream, new RebuildCreditedEvent { StreamId = streams[0], Amount = 100m });
    await _appendAsync(perStream, new RebuildDebitedEvent { StreamId = streams[0], Amount = 75m });
    await _appendAsync(perStream, new RebuildCreditedEvent { StreamId = streams[0], Amount = 3m });
    for (var i = 1; i < streams.Length; i++) {
      await _appendAsync(perStream, new RebuildCreditedEvent { StreamId = streams[i], Amount = i });
    }
    await _registerTableAsync();
    await _stampCommitSequencesAsync();

    var expected = new Dictionary<Guid, decimal> {
      [streams[0]] = 28m,
      [streams[1]] = 1m,
      [streams[2]] = 2m,
      [streams[3]] = 3m,
      [streams[4]] = 4m,
    };
    int perStreamReads;
    using (var log = new NpgsqlStatementLog(ConnectionString)) {
      var result = await perStream.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);
      await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
      perStreamReads = _eventReads(log);
    }
    var perStreamRows = await _liveBalancesAsync();

    int batchedReads;
    using (var log = new NpgsqlStatementLog(ConnectionString)) {
      var result = await batched.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);
      await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
      await Assert.That(result.StreamsProcessed).IsEqualTo(5);
      batchedReads = _eventReads(log);
    }

    await Assert.That(perStreamReads).IsEqualTo(5).Because("the control: the per-stream rebuild reads each stream on its own");
    await Assert.That(batchedReads).IsEqualTo(3).Because("five streams in batches of two are three statements");
    await Assert.That(perStreamRows).IsEquivalentTo(expected);
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(perStreamRows)
      .Because("the batched rebuild folds every stream as the per-stream rebuild does");
  }

  /// <summary>
  /// The plan of the batched read (#2513), printed rather than asserted: N streams of two events each, the statement the
  /// rebuild sent for its first batch prepared and run under <c>EXPLAIN (ANALYZE, BUFFERS)</c> with a full batch of
  /// stream ids. A measurement, not a gate: it runs only when asked for by name.
  /// </summary>
  [Test]
  [Explicit]
  [NotInParallel("rebuild-measurement")]
  [Timeout(60 * 60 * 1000)]
  public async Task Measure_TheBatchedReadsPlanAsync(CancellationToken cancellationToken) {
    var count = int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_REBUILD_STREAMS"), out var configured) ? configured : 5_000;
    var batchSize = new PerspectiveRebuildOptions().StreamReadBatchSize;
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask, observed: false);
    var streams = new List<Guid>(count);
    for (var i = 0; i < count; i++) {
      var stream = Guid.CreateVersion7();
      streams.Add(stream);
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = i });
      await _appendAsync(sp, new RebuildDebitedEvent { StreamId = stream, Amount = 1m });
    }
    await _registerTableAsync();
    await _stampCommitSequencesAsync();
    await _execAsync("ANALYZE wh_event_store");
    string statement;
    using (var log = new NpgsqlStatementLog(ConnectionString)) {
      var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(PERSPECTIVE, streams, cancellationToken);
      await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
      statement = log.Matching("wh_event_store", "event_data").First(s => s.Contains("ANY", StringComparison.OrdinalIgnoreCase));
    }

    await using var db = await _openAsync();
    await using (var prepare = new Npgsql.NpgsqlCommand($"PREPARE batched_read AS {statement}", db)) {
      await prepare.ExecuteNonQueryAsync(cancellationToken);
    }
    var ids = string.Join(",", streams.Take(batchSize).Select(id => $"'{id}'"));
    await using var explain = new Npgsql.NpgsqlCommand(
      $"EXPLAIN (ANALYZE, BUFFERS) EXECUTE batched_read(ARRAY[{ids}]::uuid[])", db);
    var plan = new System.Text.StringBuilder();
    await using (var reader = await explain.ExecuteReaderAsync(cancellationToken)) {
      while (await reader.ReadAsync(cancellationToken)) {
        plan.AppendLine(reader.GetString(0));
      }
    }
    var report = $"MEASURE batched-read streams={count} batch={batchSize}{Environment.NewLine}{statement}{Environment.NewLine}{plan}";
    Console.WriteLine(report);
    await File.AppendAllTextAsync(Environment.GetEnvironmentVariable("WHIZBANG_MEASURE_REPORT")
      ?? Path.Combine(AppContext.BaseDirectory, "rebuild-measurement.txt"), report, cancellationToken);
  }

  /// <summary>The statements that read event bodies from the event store: the rebuild's reads of its streams.</summary>
  private static int _eventReads(NpgsqlStatementLog log) =>
    log.Matching("wh_event_store", "event_data").Count(s => s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
}
