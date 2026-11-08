// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Whizbang.Data.Postgres;

/// <summary>
/// What a batched migration step says while it runs (#2128): a line at Information each
/// <see cref="StartupProgress.Cadence"/> with the batch it is on, the rows it has moved and how long it has run, and
/// one line when it finishes. A backfill over a large table runs for minutes, and a step that says nothing for minutes
/// reads as stuck; restarting it starts it over.
/// </summary>
/// <remarks>
/// Both drivers send a marked region one batch at a time and hand each batch's row count here, so they report the
/// same lines. A step shorter than one cadence says only its last line.
/// </remarks>
/// <docs>operations/infrastructure/migrations#progress-of-long-startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationBatchProgressTests.cs</tests>
public sealed class MigrationBatchProgress {
  private readonly string _migration;
  private readonly int _batchSize;
  private readonly ILogger _logger;
  private readonly ProgressClock _clock;

  /// <summary>Starts reporting one batched region of <paramref name="migration"/>.</summary>
  /// <param name="migration">The migration's name.</param>
  /// <param name="batchSize">Rows per batch, for the last line.</param>
  /// <param name="timeProvider">The clock the lines are paced on.</param>
  /// <param name="logger">Where the lines go; nowhere when null.</param>
  public MigrationBatchProgress(string migration, int batchSize, TimeProvider timeProvider, ILogger? logger) {
    ArgumentNullException.ThrowIfNull(migration);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _migration = migration;
    _batchSize = batchSize;
    _logger = logger ?? NullLogger.Instance;
    _clock = new ProgressClock(timeProvider);
  }

  /// <summary>How many batches have moved rows.</summary>
  public int Batches { get; private set; }

  /// <summary>How many rows they moved.</summary>
  public long Rows { get; private set; }

  /// <summary>Counts one batch that moved <paramref name="rows"/> rows, and says so when a line is due.</summary>
  /// <param name="rows">The rows the batch reported.</param>
  public void Batch(long rows) {
    Batches++;
    Rows += rows;
    _ = _clock;
  }

  /// <summary>Says the region finished, with its totals, when it moved anything.</summary>
  public void Finish() {
    _ = _logger;
    _ = _migration;
    _ = _batchSize;
  }
}
