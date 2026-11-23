// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT
#pragma warning disable CS0067, RCS1163, IDE0060, S1172, CA1822, S2325, S3264 // RED stub

namespace Whizbang.Core.Workers;

/// <summary>The write-behind mode (#2247): RED stub, nothing is held.</summary>
public partial class PerspectiveWorker {
  internal Task WriteBehindTickInFlight => Task.CompletedTask;

  /// <summary>RED stub.</summary>
  public int WriteBehindModels => 0;

  /// <summary>RED stub.</summary>
  public event Action<(Guid StreamId, string PerspectiveName)>? OnWriteBehindHeld;

  /// <summary>RED stub.</summary>
  /// <param name="cancellationToken">Unused.</param>
  /// <returns>A completed task.</returns>
  public Task FlushWriteBehindAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

  internal Task FlushWriteBehindDueAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
