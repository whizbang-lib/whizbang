// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// The core's placeholder partition assignment source, for a host with no storage driver. It never has an assignment, so
/// a claim made with it ranks the live instances itself: exactly what a claim with no source registered has always done.
/// Every Postgres driver runs the elected assigner and registers its cache over this (#2559).
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/MultiStream/NullPartitionAssignmentSourceTests.cs:ForClaimAsync_NeverHasAnAssignmentAsync</tests>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/MultiStream/NullPartitionAssignmentSourceTests.cs:MarkStaleAndSubscribing_ChangeNothingAsync</tests>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/MultiStream/NullPartitionAssignmentSourceTests.cs:TheCoreDefaults_RegisterIt_AsTheNullDefaultADriverDisplacesAsync</tests>
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
