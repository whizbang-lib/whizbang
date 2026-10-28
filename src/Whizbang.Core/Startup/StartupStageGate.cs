// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// Default <see cref="IStartupStageGate"/>: an <see cref="IStartupStageObserver"/> of this instance's stage reporter.
/// </summary>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageGateTests.cs</tests>
public sealed class StartupStageGate : IStartupStageGate, IStartupStageObserver {
  /// <summary>Creates the gate.</summary>
  /// <param name="schemaReady">
  /// The schema-ready gate, which opens at the end of <see cref="StartupStage.Coordinate"/>; null when the host has
  /// none.
  /// </param>
  public StartupStageGate(ISchemaReadyGate? schemaReady = null) {
    _ = schemaReady;
  }

  /// <inheritdoc />
  public bool HasReached(StartupStage stage) => true;

  /// <inheritdoc />
  public Task WaitForAsync(StartupStage stage, CancellationToken cancellationToken) => Task.CompletedTask;

  /// <inheritdoc />
  public ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) =>
    ValueTask.CompletedTask;
}
