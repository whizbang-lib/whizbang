// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The throughput measurement of the staging load (#2248): a blue-green rebuild of N single-event streams, with the
/// staging load on and switched off, counting the statements sent to the shadow table and the rows rebuilt per second.
/// A measurement, not a gate: it runs only when asked for by name.
/// </summary>
/// <remarks>
/// The stream count is <c>WHIZBANG_REBUILD_STREAMS</c> (20,000 by default). One <c>MEASURE</c> line per mode goes to
/// standard output and is appended to the file <c>WHIZBANG_MEASURE_REPORT</c> names (by default
/// <c>rebuild-measurement.txt</c> beside the test assembly), since a passing test's output is captured.
/// </remarks>
public partial class BlueGreenRebuildIntegrationTests {
  [Test]
  [Explicit]
  [NotInParallel("rebuild-measurement")] // the database: a mode measured beside the other measures its load too
  [Timeout(4 * 60 * 60 * 1000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Measure_BlueGreenRebuildThroughputAsync(bool stagingLoad, CancellationToken cancellationToken) {
    var count = int.TryParse(Environment.GetEnvironmentVariable("WHIZBANG_REBUILD_STREAMS"), out var configured) ? configured : 20_000;
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask,
      options: new BlueGreenRebuildOptions { StagingLoad = stagingLoad });
    for (var i = 0; i < count; i++) {
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = Guid.CreateVersion7(), Amount = i });
    }
    await _registerTableAsync();
    await _execAsync($"CREATE INDEX {PLAIN_INDEX} ON {TABLE} ((data->>'Balance'))");
    await _stampCommitSequencesAsync();
    using var log = new NpgsqlStatementLog(ConnectionString);

    var clock = Stopwatch.StartNew(); // review-checks:allow: a measurement records elapsed time; nothing waits on it or asserts it
    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE, cancellationToken);
    clock.Stop();

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(result.StreamsProcessed).IsEqualTo(count);
    var shadowStatements = log.Matching(TABLE + "_bg").Count;
    var line = $"MEASURE rebuild staging={stagingLoad} streams={count} seconds={clock.Elapsed.TotalSeconds:F1} " +
      $"rows_per_second={count / clock.Elapsed.TotalSeconds:F0} shadow_statements={shadowStatements} statements={log.Statements.Count}";
    Console.WriteLine(line);
    var report = Environment.GetEnvironmentVariable("WHIZBANG_MEASURE_REPORT")
      ?? Path.Combine(AppContext.BaseDirectory, "rebuild-measurement.txt");
    await File.AppendAllTextAsync(report, line + Environment.NewLine, cancellationToken);
  }
}
