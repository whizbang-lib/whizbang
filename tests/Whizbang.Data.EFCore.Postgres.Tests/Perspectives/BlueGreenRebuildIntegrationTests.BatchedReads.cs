// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
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
  /// A rebuild's statements grow with its batches, not its streams (#2513): rebuilding 4, then 8, then 12 streams in two
  /// batches each (of 2, 4 and 6 streams) makes each read at most once per batch, whatever the batch holds: the events,
  /// the rows' versions and models, the purge markers of the missing rows, the commit sequences, and one cursor write for
  /// the flush. Which of the row reads a batch needs depends on its rows (a batch whose rows all exist asks no purge
  /// marker, one whose rows are all new reads no model), never on how many streams it holds. A blue-green rebuild writes
  /// its rows to the empty shadow table through the staging load, so every run sends exactly the same statements. A
  /// rebuild in place writes each batch's rows through the write sink (#2522): one statement for the batch's new rows and
  /// one for its existing ones, at most two per batch whatever the batch holds, where it used to write one per stream.
  /// </summary>
  /// <docs>fundamentals/perspectives/rebuild#batched-writes</docs>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Rebuild_SendsTheSameStatementsForTheSameBatches_WhateverTheirStreamCountAsync(bool blueGreen) {
    const int BATCHES = 2;
    var counts = new List<int>();
    var streams = new List<Guid>();
    foreach (var (streamCount, batchSize) in new[] { (4, 2), (8, 4), (12, 6) }) {
      await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask,
        rebuildOptions: new PerspectiveRebuildOptions { StreamReadBatchSize = batchSize }, observed: false);
      while (streams.Count < streamCount) {
        var stream = Guid.CreateVersion7();
        streams.Add(stream);
        await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 10m });
        await _appendAsync(sp, new RebuildDebitedEvent { StreamId = stream, Amount = 1m });
      }
      if (counts.Count == 0) {
        await _registerTableAsync();
      }
      await _stampCommitSequencesAsync();
      using var log = new NpgsqlStatementLog(ConnectionString);
      var result = blueGreen
        ? await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE)
        : await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(PERSPECTIVE, streams);
      await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
      await Assert.That(result.StreamsProcessed).IsEqualTo(streamCount);
      Console.WriteLine($"streams={streamCount} batch={batchSize} statements={log.Statements.Count}{Environment.NewLine}" +
        string.Join(Environment.NewLine, log.Statements.Select(s => "  " + s.Trim().Split('\n')[0])));

      var reads = new Dictionary<string, int> {
        ["events"] = log.Matching("wh_event_store", "wh_event_body").Count,
        ["row versions"] = log.Matching("SELECT id, xmin", "wh_per_rebuild_balance").Count,
        ["row models"] = log.Matching("SELECT w.id", "wh_per_rebuild_balance").Count,
        ["purge markers"] = log.Matching("wh_stream_purge_markers").Count,
        ["commit sequences"] = log.Statements.Count(s => s.Contains("w.commit_sequence AS \"CommitSequence\"", StringComparison.Ordinal)
          && !s.Contains("wh_event_body", StringComparison.Ordinal)),
      };
      foreach (var (read, made) in reads) {
        await Assert.That(made).IsLessThanOrEqualTo(BATCHES)
          .Because($"{read} are read at most once per batch, not once per stream ({streamCount} streams)");
      }
      await Assert.That(reads["events"]).IsEqualTo(BATCHES);
      await Assert.That(reads["commit sequences"]).IsEqualTo(BATCHES);
      await Assert.That(log.Matching("wh_complete_rebuilt_cursors").Count).IsEqualTo(1)
        .Because("the run's cursors are written with one statement");
      var rowWrites = log.Statements.Count(statement =>
        statement.Contains("wh_per_rebuild_balance", StringComparison.Ordinal)
        && (statement.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
          || statement.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)));
      if (blueGreen) {
        await Assert.That(rowWrites).IsEqualTo(0).Because("blue-green stages its rows");
      } else {
        await Assert.That(rowWrites).IsBetween(BATCHES, 2 * BATCHES)
          .Because($"in place each batch writes its rows with one statement per write shape, never one per row ({streamCount} streams)");
      }
      counts.Add(log.Statements.Count - rowWrites);
    }

    if (blueGreen) {
      await Assert.That(counts.Distinct().Count()).IsEqualTo(1)
        .Because($"two batches cost the same statements whether they hold 4, 8 or 12 streams; counted {string.Join(", ", counts)}");
    }
    await Assert.That((await _liveBalancesAsync()).Values.Distinct()).IsEquivalentTo([9m]);
  }

  /// <summary>
  /// The plan of the batched read (#2513), printed rather than asserted: N streams of two events each, the statement the
  /// rebuild sent for its first batch prepared and run under <c>EXPLAIN (ANALYZE, BUFFERS)</c> with a full batch of
  /// stream ids. A measurement, not a gate: it runs only when asked for by name.
  /// </summary>
  [Test]
  [Explicit]
  [NotInParallel("rebuild-measurement")] // the database: a measurement beside it would load the same server and skew its plan and timings
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
    // The statement's one parameter, the batch's stream ids, named as Npgsql traced it.
    var positional = System.Text.RegularExpressions.Regex.Replace(statement, @"@\w+", "$$1",
      System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    await using (var prepare = new Npgsql.NpgsqlCommand($"PREPARE batched_read(uuid[]) AS {positional}", db)) {
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

  /// <summary>
  /// The plan of the batched cursor write (#2513, migration 556), printed rather than asserted: N streams with a cursor
  /// each, then one flush of a full batch of rebuilt cursors through the driver's completer, its statement planned by
  /// <c>auto_explain</c> (nested statements, analyzed) and handed back as notices. A measurement, not a gate.
  /// </summary>
  [Test]
  [Explicit]
  [NotInParallel("rebuild-measurement")] // the database: a measurement beside it would load the same server and skew its plan and timings
  [Timeout(60 * 60 * 1000)]
  public async Task Measure_TheBatchedCursorWritesPlanAsync(CancellationToken cancellationToken) {
    var count = int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_REBUILD_STREAMS"), out var configured) ? configured : 5_000;
    var batchSize = new PerspectiveRebuildOptions().StreamReadBatchSize;
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask, observed: false);
    var streams = new List<Guid>(count);
    for (var i = 0; i < count; i++) {
      var stream = Guid.CreateVersion7();
      streams.Add(stream);
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = i });
    }
    await _registerTableAsync();
    await _stampCommitSequencesAsync();
    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildStreamsAsync(PERSPECTIVE, streams, cancellationToken);
    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await _execAsync("ANALYZE wh_perspective_cursors; ANALYZE wh_stream_lifecycle; ANALYZE wh_event_archive_cursors");

    var plan = new System.Text.StringBuilder();
    await using var db = await _openAsync();
    db.Notice += (_, e) => plan.AppendLine(e.Notice.MessageText);
    await using (var setup = new Npgsql.NpgsqlCommand("""
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_buffers = on;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_level = notice;
      """, db)) {
      await setup.ExecuteNonQueryAsync(cancellationToken);
    }
    var flush = streams.Take(batchSize).ToList();
    var lastEvents = new List<Guid>(flush.Count);
    foreach (var stream in flush) {
      await using var last = new Npgsql.NpgsqlCommand(
        "SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version DESC LIMIT 1", db);
      last.Parameters.AddWithValue("s", stream);
      lastEvents.Add((Guid)(await last.ExecuteScalarAsync(cancellationToken))!);
    }
    plan.Clear();
    await using (var write = new Npgsql.NpgsqlCommand("SELECT wh_complete_rebuilt_cursors(@s, @p, @e, @st)", db)) {
      write.Parameters.AddWithValue("s", flush.ToArray());
      write.Parameters.AddWithValue("p", flush.ConvertAll(_ => PERSPECTIVE).ToArray());
      write.Parameters.AddWithValue("e", lastEvents.ToArray());
      write.Parameters.AddWithValue("st", flush.ConvertAll(_ => (short)PerspectiveProcessingStatus.Completed).ToArray());
      await write.ExecuteNonQueryAsync(cancellationToken);
    }
    var report = $"MEASURE batched-cursor-write streams={count} batch={batchSize}{Environment.NewLine}{plan}";
    Console.WriteLine(report);
    await File.AppendAllTextAsync(Environment.GetEnvironmentVariable("WHIZBANG_MEASURE_REPORT")
      ?? Path.Combine(AppContext.BaseDirectory, "rebuild-measurement.txt"), report, cancellationToken);
  }

  /// <summary>The statements that read event bodies from the event store: the rebuild's reads of its streams.</summary>
  private static int _eventReads(NpgsqlStatementLog log) =>
    log.Matching("wh_event_store", "event_data").Count(s => s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
}
