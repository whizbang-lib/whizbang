// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>What one superseding-history cycle did.</summary>
public readonly record struct SupersedingHistoryReconcileResult(
  int Batches, long EventsScanned, long EventsStamped, long EventsQueued, int TypesFinished, string Status);

/// <summary>RED stub (#2110).</summary>
public sealed partial class SupersedingHistoryReconcileStep : IMaintenanceStep {
  /// <summary>Creates the step.</summary>
  public SupersedingHistoryReconcileStep(ILogger<SupersedingHistoryReconcileStep> logger) : this(logger, 1, 1, () => []) { }

  internal SupersedingHistoryReconcileStep(ILogger logger, int scanSize, int maxBatches, Func<IReadOnlyList<Type>> types) { }

  /// <summary>Builds the step.</summary>
  public static SupersedingHistoryReconcileStep FromServices(IServiceProvider services) =>
    new(Microsoft.Extensions.Logging.Abstractions.NullLogger<SupersedingHistoryReconcileStep>.Instance);

  /// <inheritdoc />
  public string Name => "";

  /// <inheritdoc />
  public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;

  /// <summary>Runs one cycle.</summary>
  public Task<SupersedingHistoryReconcileResult> ReconcileAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    Task.FromResult(new SupersedingHistoryReconcileResult(0, 0, 0, 0, 0, ""));
}
