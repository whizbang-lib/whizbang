// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// The partition assignment source for a host without the elected partition assigner (a driver that does not register
/// it). It never has an assignment, so a claim made with it ranks the live instances itself: exactly what a claim with no
/// source registered has always done. The assigner's registration replaces it.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/NullPartitionAssignmentSourceTests.cs:ForClaimAsync_NeverHasAnAssignmentAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/NullPartitionAssignmentSourceTests.cs:MarkStaleAndSubscribing_ChangeNothingAsync</tests>
public sealed class NullPartitionAssignmentSource : IPartitionAssignmentSource, INullDefault {
  /// <summary>The single instance.</summary>
  public static NullPartitionAssignmentSource Instance { get; } = new();

  private NullPartitionAssignmentSource() { }

  /// <inheritdoc/>
  public PartitionAssignment? Current => null;

  /// <inheritdoc/>
  public event Action<PartitionAssignment?>? OnAssignmentChanged {
    add {
      // There is never an assignment to change, so a subscriber is never called.
    }
    remove {
      // Nothing was kept on add.
    }
  }

  /// <inheritdoc/>
  public void MarkStale() {
    // Nothing is cached, so nothing can be stale.
  }

  /// <inheritdoc/>
  public ValueTask<PartitionAssignmentVersion?> ForClaimAsync(Guid instanceId, CancellationToken cancellationToken) =>
    ValueTask.FromResult<PartitionAssignmentVersion?>(null);
}
