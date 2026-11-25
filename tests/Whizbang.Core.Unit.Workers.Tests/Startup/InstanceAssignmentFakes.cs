// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>An assignment reader that answers what the test queued, or throws what it queued.</summary>
internal sealed class QueuedAssignmentReader : IInstanceAssignmentReader {
  private readonly Queue<Func<IReadOnlyList<InstanceAssignment>>> _answers = new();

  public void Answer(params InstanceAssignment[] rows) => _answers.Enqueue(() => rows);

  public void Fail(Exception ex) => _answers.Enqueue(() => throw ex);

  public Task<IReadOnlyList<InstanceAssignment>> ReadAssignmentsAsync(CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult(_answers.Dequeue()());
  }
}

/// <summary>This instance's identity, fixed for a test.</summary>
internal sealed class FixedInstance : IServiceInstanceProvider {
  public Guid InstanceId { get; } = Guid.CreateVersion7();
  public string ServiceName => "assign-svc";
  public string HostName => "assign-host";
  public int ProcessId => 1;
  public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
}

/// <summary>Builds monitors and rows for the assignment tests.</summary>
internal static class InstanceAssignments {
  public static InstanceAssignment Row(Guid id, InstanceAssignmentKind kind, int owned = 2500, long? map = 12, params string[] roles) =>
    new(id, "assign-svc", "assign-host", "1.2.3", kind != InstanceAssignmentKind.Starting, true, roles, owned, map, kind);

  public static InstanceAssignmentMonitor Monitor(
      IInstanceAssignmentReader? reader, FixedInstance instance, FakeTimeProvider time, FakeLogger<InstanceAssignmentMonitor>? logger = null,
      InstanceAssignmentOptions? options = null) =>
    new(reader, instance, Options.Create(options ?? new InstanceAssignmentOptions()), Options.Create(new PartitionAssignerOptions()),
      Options.Create(new RoleAssignmentOptions()), logger ?? new FakeLogger<InstanceAssignmentMonitor>(), time);
}
