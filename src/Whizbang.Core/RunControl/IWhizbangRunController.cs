// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>Why the run-control path halts the instance (#2045).</summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControllerTests.cs:Halt_ClosesThePermit_ReleasesWorkThenRoles_ThenWritesTheSettledPhaseAsync</tests>
public enum RunHaltReason {
  /// <summary>An operator paused the instance. Resumable; the settled phase is <see cref="LifecyclePhase.Paused"/>.</summary>
  Pause,

  /// <summary>
  /// The instance stands down for a newer version: at startup when Assess finds the schema newer, or while running when
  /// a newer instance asks. Resumable when the newer instance's migration rolls back; the settled phase is
  /// <see cref="LifecyclePhase.StandingBy"/>.
  /// </summary>
  StandDown,

  /// <summary>The instance stops: host shutdown, or the killswitch's stop. Final; the settled phase is <see cref="LifecyclePhase.Stopped"/>.</summary>
  Stop,
}

/// <summary>
/// The one run-control path (#2045). Stand-down, pause and stop all take it, with a reason: close the run permit (no new
/// work is taken), write the transitional phase, wait for the work in hand to finish, release the work still held so
/// peers take it over promptly, release the roles this instance holds, and confirm by writing the settled phase on the
/// instance's row. Several reasons can hold at once (an operator's pause during a stand-down); the permit stays closed
/// until every one is lifted. A resume lifts a pause or a stand-down, never a stop.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControllerTests.cs</tests>
public interface IWhizbangRunController {
  /// <summary>The permit this path closes and reopens: the one every claiming worker obeys.</summary>
  WhizbangRunPermit Permit { get; }

  /// <summary>
  /// The strongest reason the instance is halted for (a stop, then a stand-down, then a pause), or <see langword="null"/>
  /// while it runs. The reported phase follows it.
  /// </summary>
  RunHaltReason? HaltedFor { get; }

  /// <summary>Every reason that holds the permit closed; empty while the instance runs.</summary>
  IReadOnlyCollection<RunHaltReason> HaltedReasons { get; }

  /// <summary>Halts the instance for <paramref name="reason"/>. Returns once the settled phase is written.</summary>
  /// <param name="reason">Why.</param>
  /// <param name="cancellationToken">
  /// Bounds every step (host shutdown passes its stop token). A step it cuts short is logged, and the halt still
  /// completes and writes its phases.
  /// </param>
  /// <returns>A task that completes when the halt is confirmed.</returns>
  Task HaltAsync(RunHaltReason reason, CancellationToken cancellationToken);

  /// <summary>
  /// Lifts <paramref name="reason"/>. The permit reopens only when no other reason holds; otherwise the reported phase
  /// follows the strongest reason that remains. Does nothing for a reason that does not hold.
  /// </summary>
  /// <param name="reason">The reason to lift: a pause (the operator's resume) or a stand-down (a revival).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the phase is written.</returns>
  /// <exception cref="InvalidOperationException">The instance was stopped; a stop is final.</exception>
  Task ResumeAsync(RunHaltReason reason, CancellationToken cancellationToken);
}

/// <summary>
/// Releases the work this instance still holds once nothing is in hand (#2045): the second step of the run-control path,
/// so peers take the work over at once rather than after its lease lapses. Implemented by what holds leases beyond a
/// single unit of work, such as the claim worker's leased streams.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControllerTests.cs:Halt_AReleaserThatFails_IsLoggedAndTheRestStillRunAsync</tests>
public interface IReleasesHeldWork {
  /// <summary>Releases the held work that has not been started.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the release is done.</returns>
  Task ReleaseHeldWorkAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Releases every role this instance holds (#2045): the third step of the run-control path, so a stand-down, a pause or
/// a stop hands each role off at once. Implemented by the role elector.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/WhizbangRunControllerTests.cs:Halt_ClosesThePermit_ReleasesWorkThenRoles_ThenWritesTheSettledPhaseAsync</tests>
public interface IReleasesHeldRoles {
  /// <summary>Releases every held role. Idempotent; a release that cannot reach the database leaves the lease to lapse.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when every role is released.</returns>
  Task ReleaseAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A hosted worker that takes work and obeys the instance's run permit (#2045): it takes a hold around each unit of work
/// (or, for a consumer called back by its transport, acts on each change of the permit). A test discovers every hosted
/// worker of a composed host and fails when one that takes work does not obey the container's one permit.
/// </summary>
/// <docs>resilience/managed-resource-run-control</docs>
public interface IObeysRunPermit {
  /// <summary>The permit this worker obeys.</summary>
  WhizbangRunPermit RunPermit { get; }
}
