// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Startup;

/// <summary>
/// Waits for this instance to reach a startup stage (#1636): what a worker that must not touch the database before
/// its stage awaits before it starts.
/// </summary>
/// <remarks>
/// <para>
/// A stage counts as reached once this instance has entered it or skipped it, or entered any later startup stage.
/// The shutdown stages reach nothing: an instance that starts to drain before a worker's stage never starts that
/// worker.
/// </para>
/// <para>
/// Every stage up to <see cref="StartupStage.Coordinate"/> also counts as reached once the schema-ready gate is open,
/// because the gate opens only at the end of that stage. A host whose schema is provisioned out of band, and so has
/// a gate that is open from the start, therefore never waits.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageGateTests.cs</tests>
public interface IStartupStageGate {
  /// <summary>Whether this instance has reached <paramref name="stage"/>.</summary>
  /// <param name="stage">A startup stage.</param>
  /// <returns>True once the stage, or a later startup stage, has been entered or skipped.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is not a startup stage.</exception>
  bool HasReached(StartupStage stage);

  /// <summary>Completes once this instance has reached <paramref name="stage"/>.</summary>
  /// <param name="stage">A startup stage.</param>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <returns>A task that completes when the stage is reached, or is canceled with the token.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is not a startup stage.</exception>
  Task WaitForAsync(StartupStage stage, CancellationToken cancellationToken);
}
