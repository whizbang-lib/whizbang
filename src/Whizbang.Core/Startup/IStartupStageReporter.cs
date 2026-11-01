// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Startup;

/// <summary>
/// The one owner of the stage this instance is in (#1636). Entering a stage logs it, records it on the instance's row
/// (<c>startup_stage</c>, <c>startup_stage_message</c>, <c>startup_stage_entered_at</c>) and tells every
/// <see cref="IStartupStageObserver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stages only move forward. Entering a stage before the current one changes nothing (a second schema initialization
/// in the same process, or a retry, passes back through stages this instance has already left); entering the current
/// stage again with a different message is progress within it, logged with the same prefix; entering a later stage is
/// the transition.
/// </para>
/// <para>
/// The instance's row does not exist until the instance registers, so a stage entered before then is held and written
/// when <see cref="InstanceRegisteredAsync"/> says the row exists. A write that fails is held the same way and written
/// on the next registration signal (each accepted heartbeat gives one), so a lost write cannot leave the row reading
/// an earlier stage for good. Nothing here throws for a failed write: a stage the row does not show yet is the safe
/// direction, since only stages 60 and 70 make an instance eligible for work.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageReporterTests.cs</tests>
public interface IStartupStageReporter {
  /// <summary>The stage this instance is in, or <see langword="null"/> before the first one is entered.</summary>
  StartupStageSnapshot? Current { get; }

  /// <summary>Enters <paramref name="stage"/>, or reports progress within it when it is the current one.</summary>
  /// <param name="stage">The stage.</param>
  /// <param name="message">What the stage is doing. Required: it is half of the logged line.</param>
  /// <param name="cancellationToken">Canceled when the caller is.</param>
  /// <returns>A task that completes once the stage is logged, written (or held) and observed.</returns>
  ValueTask EnterAsync(StartupStage stage, string message, CancellationToken cancellationToken = default);

  /// <summary>
  /// Passes through <paramref name="stage"/> without doing its work, recording why. Changes nothing when the stage is
  /// not after the current one.
  /// </summary>
  /// <param name="stage">The stage skipped.</param>
  /// <param name="reason">Why it was skipped.</param>
  /// <param name="cancellationToken">Canceled when the caller is.</param>
  /// <returns>A task that completes once the skip is logged, written (or held) and observed.</returns>
  ValueTask SkipAsync(StartupStage stage, string reason, CancellationToken cancellationToken = default);

  /// <summary>
  /// Enters a lifecycle phase off the startup ladder (pausing, paused, standing by, resuming, faulted, halted) while the
  /// stage stays where it is: the row records the phase with the stage it is in. Entering a stage afterwards records
  /// that stage's own phase again. A phase on the ladder is the stage's, and is refused.
  /// </summary>
  /// <param name="phase">The phase.</param>
  /// <param name="cancellationToken">Canceled when the caller is.</param>
  /// <returns>A task that completes once the phase is written (or held).</returns>
  ValueTask EnterPhaseAsync(RunControl.LifecyclePhase phase, CancellationToken cancellationToken = default) =>
    ValueTask.CompletedTask;

  /// <summary>
  /// The instance's row now exists: writes the stage held since before it did, or since a write failed. Called by
  /// whatever registers the instance, and after each accepted heartbeat; does nothing when nothing is held.
  /// </summary>
  /// <param name="cancellationToken">Canceled when the caller is.</param>
  /// <returns>A task that completes once the held stage is written or held again.</returns>
  ValueTask InstanceRegisteredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Watches this instance's stage. Register any number with
/// <c>services.AddSingleton&lt;IStartupStageObserver&gt;(...)</c>.
/// </summary>
/// <remarks>
/// For monitoring, and for a test that has to know a stage was reached and written rather than guess from time
/// passing. The reporter awaits each call before the next stage can be entered, so an observer that blocks holds the
/// instance in its stage; a production observer returns promptly.
/// </remarks>
/// <docs>operations/startup/startup-status#startup-stages</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageReporterTests.cs</tests>
public interface IStartupStageObserver {
  /// <summary>
  /// The stage changed (a transition, progress within it, or a skip), or a held stage was written.
  /// </summary>
  /// <param name="snapshot">The stage as it now stands.</param>
  /// <param name="recorded">Whether the instance's row now records it.</param>
  /// <param name="cancellationToken">Canceled when the caller is.</param>
  /// <returns>A task the reporter awaits.</returns>
  ValueTask OnStageChangedAsync(StartupStageSnapshot snapshot, bool recorded, CancellationToken cancellationToken);
}
