// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The write-behind measurement (#2247): a few hot streams, each taking a steady run of events while the worker drains
/// them, applied once set-based and once write-behind, counting the statements against the perspective table and the
/// database as a whole and the events applied per second. Which perspectives write-behind pays off for is measured here
/// before it is chosen. A measurement, not a gate: it runs only when asked for by name.
/// </summary>
/// <remarks>
/// <c>WHIZBANG_HOT_STREAMS</c> streams (5 by default) take <c>WHIZBANG_HOT_EVENTS</c> events each (2,000 by default),
/// committed a round at a time (one event on every hot stream per round) while the worker runs, so each drain finds a
/// few new events per stream, as a counter or an aggregate under load does. The results go to standard output and to
/// the file <c>WHIZBANG_MEASURE_REPORT</c> names (by default <c>hot-stream-measurement.txt</c> beside the test assembly).
/// </remarks>
public partial class SetBasedPerspectiveWorkerIntegrationTests {
  [Test]
  [Explicit]
  // One mode at a time, so neither measures the other's load.
  [NotInParallel("fan-out-measurement")] // the database: a mode measured beside the other measures its load too
  [Timeout(4 * 60 * 60 * 1000)]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.WriteBehind)]
  public async Task Measure_HotStreamsAsync(PerspectiveWriteMode mode, CancellationToken ct) {
    var streamCount = int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_HOT_STREAMS"), out var s) ? s : 5;
    var eventsPerStream = int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_HOT_EVENTS"), out var e) ? e : 2000;
    var total = streamCount * eventsPerStream;
    await using var conn = await _openAsync(ct);
    var probe = _composePipeline(mode);
    await probe.RegisterAsync(conn, ct);
    var instance = probe.InstanceProvider;
    await probe.DisposeAsync();

    var completion = new CountingCompletionChannel(total);
    await using var pipeline = _composePipeline(mode, instance, completion, consumers: 4, claimBatch: 1000, productionCompletion: true);
    var flusher = new PerspectiveCompletionFlushWorker(
      pipeline.Services.GetRequiredService<IServiceScopeFactory>(),
      Microsoft.Extensions.Options.Options.Create(new PerspectiveCompletionFlushWorkerOptions()),
      Microsoft.Extensions.Options.Options.Create(new Whizbang.Core.Messaging.WorkCoordinatorOptions()),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<PerspectiveCompletionFlushWorker>.Instance,
      NoOpPinnedConnectionPool.Instance);
    completion.Inner = flusher;
    await flusher.StartAsync(ct);
    var streams = Enumerable.Range(0, streamCount).Select(_ => (Guid)TrackedGuid.New()).ToList();

    using var statements = new NpgsqlStatementLog(ConnectionString);
    var clock = Stopwatch.StartNew(); // review-checks:allow: a measurement records elapsed time; nothing waits on it or asserts it
    await pipeline.HandOffAsync([], ct);
    await pipeline.StartWorkerAsync(ct);
    for (var round = 0; round < eventsPerStream; round++) {
      await _commitManyAsync(conn, instance, streams, ct);
    }
    await completion.All.WaitAsync(TimeSpan.FromHours(2), ct);
    clock.Stop();
    await flusher.StopAsync(ct);

    var touching = statements.Matching(TABLE);
    var line = $"MEASURE mode={mode} hot_streams={streamCount} events={total} seconds={clock.Elapsed.TotalSeconds:F1} "
      + $"events_per_second={total / clock.Elapsed.TotalSeconds:F0} table_statements={touching.Count} "
      + $"total_statements={statements.Statements.Count}";
    Console.WriteLine(line);
    var reportPath = Environment.GetEnvironmentVariable("WHIZBANG_MEASURE_REPORT") is { Length: > 0 } named
      ? named
      : Path.Combine(AppContext.BaseDirectory, "hot-stream-measurement.txt");
    await File.AppendAllLinesAsync(reportPath, [line], ct);
    await Assert.That(await _countAsync(conn, ct)).IsGreaterThanOrEqualTo(streamCount);
  }
}
