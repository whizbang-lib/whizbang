// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// Default <see cref="IStartupStageGate"/>: an <see cref="IStartupStageObserver"/> of this instance's stage reporter,
/// which also reads the schema-ready gate for every stage up to <see cref="StartupStage.Coordinate"/>.
/// </summary>
/// <remarks>
/// One signal per startup stage, completed once and never reset: stages only move forward, and a stage reached stays
/// reached. A shutdown stage completes none, so a worker still waiting for its stage when the host starts to stop is
/// never started.
/// </remarks>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageGateTests.cs</tests>
public sealed class StartupStageGate : IStartupStageGate, IStartupStageObserver {
  private readonly ISchemaReadyGate? _schemaReady;
  private readonly Dictionary<StartupStage, TaskCompletionSource> _reached = StartupStages.Startup
    .ToDictionary(s => s, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

  /// <summary>Creates the gate.</summary>
  /// <param name="schemaReady">
  /// The schema-ready gate, which opens at the end of <see cref="StartupStage.Coordinate"/>; null when the host has
  /// none.
  /// </param>
  public StartupStageGate(ISchemaReadyGate? schemaReady = null) {
    _schemaReady = schemaReady;
  }

  /// <inheritdoc />
  public bool HasReached(StartupStage stage) =>
    _signal(stage).Task.IsCompleted || (_bySchemaGate(stage) && _schemaReady!.IsReady);

  /// <inheritdoc />
  public Task WaitForAsync(StartupStage stage, CancellationToken cancellationToken) {
    var reached = _signal(stage).Task;
    return _bySchemaGate(stage)
      ? _eitherAsync(reached, _schemaReady!.WaitForReadyAsync(cancellationToken), cancellationToken)
      : reached.WaitAsync(cancellationToken);
  }

  /// <inheritdoc />
  public ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(snapshot);
    if (StartupStages.OrdinalOf(snapshot.Stage) is not null) {
      foreach (var (stage, signal) in _reached) {
        if (stage <= snapshot.Stage) {
          signal.TrySetResult();
        }
      }
    }
    return ValueTask.CompletedTask;
  }

  private TaskCompletionSource _signal(StartupStage stage) =>
    _reached.TryGetValue(stage, out var signal)
      ? signal
      : throw new ArgumentOutOfRangeException(nameof(stage), stage, "Only a startup stage can be waited for.");

  /// <summary>Whether the schema-ready gate opening also reaches <paramref name="stage"/>.</summary>
  private bool _bySchemaGate(StartupStage stage) => _schemaReady is not null && stage <= StartupStage.Coordinate;

  private static async Task _eitherAsync(Task reached, Task schemaReady, CancellationToken cancellationToken) {
    await Task.WhenAny(reached, schemaReady).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
  }
}
