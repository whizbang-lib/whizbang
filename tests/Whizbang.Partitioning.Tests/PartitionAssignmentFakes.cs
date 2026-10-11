// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>An in-memory partition assignment store for the assigner's and the cache's unit tests. No I/O.</summary>
internal sealed class FakePartitionAssignmentStore : IPartitionAssignmentStore {
  public PartitionAssignmentRead? Published { get; set; }
  public List<PartitionAssignmentCandidate> Candidates { get; } = [];
  public int Reads { get; private set; }
  public List<(Guid Instance, long Epoch)> Renewals { get; } = [];
  public List<IReadOnlyList<Guid>> Publishes { get; } = [];
  public Exception? FailReadsWith { get; set; }
  public bool RefusePublish { get; set; }
  public TimeSpan LeaseRemaining { get; set; } = TimeSpan.FromSeconds(90);

  public Task<PartitionAssignmentRead?> ReadAsync(CancellationToken cancellationToken) {
    Reads++;
    return FailReadsWith is { } failure ? Task.FromException<PartitionAssignmentRead?>(failure) : Task.FromResult(Published);
  }

  public Task<IReadOnlyList<PartitionAssignmentCandidate>> ReadCandidatesAsync(CancellationToken cancellationToken) =>
    Task.FromResult<IReadOnlyList<PartitionAssignmentCandidate>>([.. Candidates]);

  public Task<PartitionAssignment?> PublishAsync(
      string role, Guid instanceId, long epoch, IReadOnlyList<Guid> members, TimeSpan lease, CancellationToken cancellationToken) {
    if (RefusePublish) {
      return Task.FromResult<PartitionAssignment?>(null);
    }
    Publishes.Add(members);
    var revision = Published?.Assignment.Epoch == epoch ? Published.Assignment.Revision + 1 : 1;
    var assignment = new PartitionAssignment(epoch, revision, instanceId, [.. members], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch + lease);
    Published = new PartitionAssignmentRead(assignment, LeaseRemaining);
    return Task.FromResult<PartitionAssignment?>(assignment);
  }

  public Task<bool> RenewAsync(Guid instanceId, long epoch, CancellationToken cancellationToken) {
    Renewals.Add((instanceId, epoch));
    return Task.FromResult(true);
  }
}

/// <summary>A grant at a fixed epoch, held until the test says otherwise.</summary>
internal sealed class FakeDutyGrant(long? epoch) : IDutyGrant {
  public string Duty => PartitionAssignerOptions.ROLE;
  public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
  public long? Epoch => epoch;
  public bool Held { get; set; } = true;
  public bool DrainRequested { get; set; }
  public bool Disposed { get; private set; }
  public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(Held);
  public ValueTask DisposeAsync() {
    Disposed = true;
    return ValueTask.CompletedTask;
  }
}

/// <summary>A connection mode and alive-lock whose state the test sets.</summary>
internal sealed class FakeAliveLock : IInstanceConnectionModeSource {
  public InstanceConnectionMode ConnectionMode { get; set; } = InstanceConnectionMode.Direct;
  public bool IsAliveLockHeld { get; set; }
}
