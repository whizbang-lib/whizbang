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
}
