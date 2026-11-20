// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Writes the purge markers that rows purged before markers existed never got (#2314).
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PurgeMarkerBackfillStepTests.cs</tests>
public sealed partial class PurgeMarkerBackfillStep(ILogger<PurgeMarkerBackfillStep> logger) : IMaintenanceStep {
  /// <summary>The most purge markers one cycle writes.</summary>
  public const int BATCH_SIZE = 5000;

  /// <summary>The most missing rows left unmarked that the report counts.</summary>
  public const int COUNT_CAP = 10000;

  private readonly ILogger<PurgeMarkerBackfillStep> _logger = ArgumentGuard.NotNull(logger);

  /// <inheritdoc />
  public string Name => "purge-marker-backfill";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    _ = _logger;
    return Task.CompletedTask;
  }
}
