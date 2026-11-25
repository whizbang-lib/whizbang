// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// #2595: the hosted monitor reads this instance's assignment once it starts and again on every interval, on the clock
/// it was given, and stops when the host stops. Each read is waited on through the monitor's own Observed event, and
/// the interval is the timer the loop arms, fired exactly.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/InstanceAssignmentMonitor.cs</code-under-test>
[Category("Startup")]
public class InstanceAssignmentMonitorLoopTests {
  private sealed class Pod : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "loop-svc";
    public string HostName => "loop-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class CountingReader : IInstanceAssignmentReader {
    public int Reads;
    public Task<IReadOnlyList<InstanceAssignment>> ReadAssignmentsAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref Reads);
      return Task.FromResult<IReadOnlyList<InstanceAssignment>>([]);
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task TheHostedLoop_ReadsAtStart_AndOnEveryInterval_UntilStoppedAsync(CancellationToken cancellationToken) {
    var clock = new ProbeClock();
    var reader = new CountingReader();
    var options = new InstanceAssignmentOptions { ObserveInterval = TimeSpan.FromSeconds(30) };
    using var monitor = new InstanceAssignmentMonitor(reader, new Pod(), Options.Create(options),
      Options.Create(new PartitionAssignerOptions()), Options.Create(new RoleAssignmentOptions()),
      NullLogger<InstanceAssignmentMonitor>.Instance, clock);
    var reads = Channel.CreateUnbounded<InstanceAssignmentObservation>();
    monitor.Observed += o => reads.Writer.TryWrite(o);

    await monitor.StartAsync(cancellationToken);
    var first = await reads.Reader.ReadAsync(cancellationToken);
    var due = await clock.NextArmedAsync(cancellationToken);
    await Assert.That(due - first.ObservedAt).IsEqualTo(options.ObserveInterval);
    clock.SetUtcNow(due);
    var second = await reads.Reader.ReadAsync(cancellationToken);

    await Assert.That(second.ObservedAt).IsEqualTo(due);
    await monitor.StopAsync(cancellationToken);
    await monitor.ExecuteTask!;
    await Assert.That(reader.Reads).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task TheHostedLoop_StoppedMidRead_EndsQuietlyAsync(CancellationToken cancellationToken) {
    var clock = new ProbeClock();
    var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var monitor = new InstanceAssignmentMonitor(new BlockingReader(reading), new Pod(),
      Options.Create(new InstanceAssignmentOptions()), Options.Create(new PartitionAssignerOptions()),
      Options.Create(new RoleAssignmentOptions()), NullLogger<InstanceAssignmentMonitor>.Instance, clock);

    await monitor.StartAsync(cancellationToken);
    await reading.Task.WaitAsync(cancellationToken);
    await monitor.StopAsync(cancellationToken);

    var loop = monitor.ExecuteTask!;
    await loop;
    await Assert.That(loop.IsCompletedSuccessfully).IsTrue()
      .Because("a read canceled by the host's stop ends the loop, it is not a failure");
  }

  private sealed class BlockingReader(TaskCompletionSource reading) : IInstanceAssignmentReader {
    public async Task<IReadOnlyList<InstanceAssignment>> ReadAssignmentsAsync(CancellationToken cancellationToken) {
      reading.TrySetResult();
      // Never answers: only the host's stop ends this read.
      await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
      return [];
    }
  }
}
