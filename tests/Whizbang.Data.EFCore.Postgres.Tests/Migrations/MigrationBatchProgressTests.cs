// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// A batched migration step says how far along it is at Information on a steady cadence (#2128): the batch it is on,
/// the rows it has moved and how long it has run, then one line with its totals.
/// </summary>
/// <remarks>Driven by a fake clock, advanced between batches: each line is due exactly when the cadence says.</remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationBatchProgress.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StartupProgress.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#progress-of-long-startup-work</docs>
[Category("Unit")]
[Category("Shard2")]
public class MigrationBatchProgressTests {
  private sealed class Lines : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
  }

  [Test]
  public async Task ALongStep_SaysItsBatchRowsAndElapsedTime_OncePerCadence_ThenItsTotalsAsync() {
    var time = new FakeTimeProvider();
    var lines = new Lines();
    var progress = new MigrationBatchProgress("087_StreamDigests", 10_000, time, lines);

    // Six batches, five seconds apart: a line is due at fifteen and at thirty seconds, and at no other batch.
    for (var batch = 1; batch <= 6; batch++) {
      time.Advance(TimeSpan.FromSeconds(5));
      progress.Batch(10_000);
    }
    time.Advance(TimeSpan.FromSeconds(42));
    progress.Batch(1_234);
    progress.Finish();

    await Assert.That(lines.Entries.Select(e => e.Level).Distinct()).IsEquivalentTo(new[] { LogLevel.Information });
    await Assert.That(string.Join("\n", lines.Entries.Select(e => e.Message))).IsEqualTo(string.Join("\n",
      "Migration 087_StreamDigests: batch 3, 30000 row(s) moved so far, 15s elapsed",
      "Migration 087_StreamDigests: batch 6, 60000 row(s) moved so far, 30s elapsed",
      "Migration 087_StreamDigests: batch 7, 61234 row(s) moved so far, 1m 12s elapsed",
      "Migration 087_StreamDigests: backfilled 61234 row(s) in 7 batch(es) of 10000, in 1m 12s"));
    await Assert.That(progress.Batches).IsEqualTo(7);
    await Assert.That(progress.Rows).IsEqualTo(61_234L);
  }

  [Test]
  public async Task AStepShorterThanTheCadence_SaysOnlyItsTotalsAsync() {
    var time = new FakeTimeProvider();
    var lines = new Lines();
    var progress = new MigrationBatchProgress("200_Backfill", 500, time, lines);

    progress.Batch(500);
    time.Advance(TimeSpan.FromSeconds(14));
    progress.Batch(20);
    progress.Finish();

    await Assert.That(string.Join("\n", lines.Entries.Select(e => e.Message)))
      .IsEqualTo("Migration 200_Backfill: backfilled 520 row(s) in 2 batch(es) of 500, in 14s");
  }

  [Test]
  public async Task ARegionThatMovedNothing_SaysNothingAsync() {
    var lines = new Lines();
    var progress = new MigrationBatchProgress("201_Nothing", 500, new FakeTimeProvider(), lines);

    progress.Finish();

    await Assert.That(lines.Entries).IsEmpty();
  }

  [Test]
  public async Task NoLogger_IsAllowed_AndMissingArgumentsAreRefusedAsync() {
    var progress = new MigrationBatchProgress("202_Quiet", 1, new FakeTimeProvider(), null);
    progress.Batch(1);
    progress.Finish();
    await Assert.That(progress.Rows).IsEqualTo(1L);

    await Assert.That(() => new MigrationBatchProgress(null!, 1, new FakeTimeProvider(), null)).Throws<ArgumentNullException>();
    await Assert.That(() => new MigrationBatchProgress("x", 1, null!, null)).Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments(0, "0s")]
  [Arguments(59, "59s")]
  [Arguments(60, "1m 00s")]
  [Arguments(725, "12m 05s")]
  public async Task Elapsed_IsSaidInSecondsThenMinutesAsync(int seconds, string said) {
    await Assert.That(StartupProgress.Describe(TimeSpan.FromSeconds(seconds))).IsEqualTo(said);
  }

  [Test]
  public async Task TheCadence_IsFifteenSecondsAsync() {
    await Assert.That(StartupProgress.Cadence).IsEqualTo(TimeSpan.FromSeconds(15));
  }
}
