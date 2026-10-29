// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>RED stub (#1746).</summary>
/// <docs>operations/infrastructure/storage-maintenance#queue-index-rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/QueueIndexReindexStepTests.cs</tests>
public sealed partial class QueueIndexReindexStep(
    IOptions<MaintenanceWorkerOptions> options,
    ILogger<QueueIndexReindexStep> logger,
    TimeProvider? timeProvider = null) : IMaintenanceStep {
  internal const double DEFAULT_THRESHOLD = 3.0;
  internal const long MIN_REINDEX_BYTES = 1L << 20;
  internal const string THRESHOLD_KEY = "Whizbang:Workers:Maintenance:ReindexBloatThreshold";

  private readonly IOptions<MaintenanceWorkerOptions> _options = ArgumentGuard.NotNull(options);
  private readonly ILogger<QueueIndexReindexStep> _logger = ArgumentGuard.NotNull(logger);
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => "queue-index-reindex";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    _ = (_options, _logger, _timeProvider);
    return Task.CompletedTask;
  }

  internal static QueueIndexBloat? Choose(IReadOnlyList<QueueIndexBloat> survey, double threshold) {
    _ = (survey, threshold);
    return null;
  }

  internal static List<string> ValidateThreshold(double threshold) {
    _ = threshold;
    return [];
  }

  internal static TimeSpan ClaimWindow(MaintenanceWorkerOptions settings) {
    _ = settings;
    return TimeSpan.Zero;
  }
}
