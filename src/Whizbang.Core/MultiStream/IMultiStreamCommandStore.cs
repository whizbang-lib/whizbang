// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.MultiStream;

/// <summary>
/// Where multi-stream command runs are kept: the run, one work item per stream, and each stream's outcome. A storage
/// driver supplies it; until one does, <see cref="NullMultiStreamCommandStore"/> stands in and reports
/// <see cref="IsConfigured"/> false.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#storage</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandIntakeTests.cs:HandleAsync_NoStoreRegistered_ThrowsInsteadOfDroppingTheCommandAsync</tests>
public interface IMultiStreamCommandStore {
  /// <summary>True when a real store is registered.</summary>
  bool IsConfigured => true;

  /// <summary>
  /// Records a run and one pending work item per stream, and wakes every instance to look for its streams. Idempotent on
  /// <see cref="MultiStreamRun.RunId"/>: a run already recorded is left as it is.
  /// </summary>
  /// <param name="run">The run to record.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>True when this call recorded the run; false when it was already recorded.</returns>
  Task<bool> RecordRunAsync(MultiStreamRun run, CancellationToken cancellationToken);

  /// <summary>
  /// Claims pending streams this instance owns, by the rule the work claim uses: a stream whose lease this instance
  /// holds; otherwise, unless a live sibling holds its lease, a stream whose partition belongs to this instance. Each
  /// claimed stream is leased to this instance and counted as an attempt.
  /// </summary>
  /// <param name="claim">Who is claiming, how many, for how long, and under which partition assignment.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The claimed streams, oldest run first; empty when there is nothing for this instance.</returns>
  Task<IReadOnlyList<MultiStreamWorkItem>> ClaimAsync(MultiStreamClaim claim, CancellationToken cancellationToken);

  /// <summary>
  /// Records how claimed streams ended. A success is final. A failure releases the stream for another attempt until it
  /// has had <paramref name="maxAttempts"/>, then is final with its error. A release (<see cref="MultiStreamOutcome.Released"/>)
  /// hands the stream back without counting the attempt and holds it for <paramref name="releaseDelay"/> before it is
  /// offered again. Only streams this instance still holds are recorded. Runs left with no pending stream are completed,
  /// each exactly once.
  /// </summary>
  /// <param name="instanceId">The instance that worked the streams.</param>
  /// <param name="outcomes">One outcome per worked stream.</param>
  /// <param name="maxAttempts">The attempts a stream gets before a failure is final.</param>
  /// <param name="releaseDelay">How long a released stream waits before it is offered again.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The runs this call completed.</returns>
  Task<IReadOnlyList<MultiStreamRunCompletion>> CompleteAsync(
    Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, TimeSpan releaseDelay, CancellationToken cancellationToken);

  /// <summary>Reads where a run stands.</summary>
  /// <param name="runId">The run.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The run's status, or null when no such run is recorded (or it was purged).</returns>
  Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken);

  /// <summary>Deletes runs, with their streams, that completed before <paramref name="completedBefore"/>.</summary>
  /// <param name="completedBefore">The cutoff.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many runs were deleted.</returns>
  Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken);
}

/// <summary>
/// The framework's stand-in for <see cref="IMultiStreamCommandStore"/> when no storage driver registered one. It reports
/// <see cref="IsConfigured"/> false, and the intake refuses a command rather than accept one nothing would run.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#storage</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandRegistrationTests.cs:AddMultiStreamCommandHandler_WithNoStore_LeavesTheNullDefaultForADriverToReplaceAsync</tests>
public sealed class NullMultiStreamCommandStore : IMultiStreamCommandStore, INullDefault {
  private const string NOT_REGISTERED =
    "No multi-stream command store is registered; a storage driver supplies one. Check IsConfigured before calling.";

  /// <summary>The single instance.</summary>
  public static NullMultiStreamCommandStore Instance { get; } = new();

  private NullMultiStreamCommandStore() { }

  /// <inheritdoc/>
  public bool IsConfigured => false;

  /// <inheritdoc/>
  public Task<bool> RecordRunAsync(MultiStreamRun run, CancellationToken cancellationToken) =>
    throw new InvalidOperationException(NOT_REGISTERED);

  /// <inheritdoc/>
  public Task<IReadOnlyList<MultiStreamWorkItem>> ClaimAsync(MultiStreamClaim claim, CancellationToken cancellationToken) =>
    throw new InvalidOperationException(NOT_REGISTERED);

  /// <inheritdoc/>
  public Task<IReadOnlyList<MultiStreamRunCompletion>> CompleteAsync(
      Guid instanceId, IReadOnlyList<MultiStreamOutcome> outcomes, int maxAttempts, TimeSpan releaseDelay,
      CancellationToken cancellationToken) =>
    throw new InvalidOperationException(NOT_REGISTERED);

  /// <inheritdoc/>
  public Task<MultiStreamRunStatus?> ReadRunAsync(Guid runId, CancellationToken cancellationToken) =>
    throw new InvalidOperationException(NOT_REGISTERED);

  /// <inheritdoc/>
  public Task<int> PurgeCompletedAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) =>
    throw new InvalidOperationException(NOT_REGISTERED);
}
