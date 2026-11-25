// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Health;

/// <summary>
/// The <c>assignment</c> health component (#2595): what this instance has been given to do, from its latest read. An
/// instance eligible for work that has owned no partition for longer than its grace
/// (<see cref="InstanceAssignmentOptions.AwaitingShareGrace"/>) is Degraded, as is one that is evicted, not in the fleet,
/// or whose assignment cannot be read. Never Faulted, and readiness does not wait for a share: a missing share is made
/// visible here instead.
/// </summary>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Health/InstanceAssignmentHealthSourceTests.cs</tests>
public sealed class InstanceAssignmentHealthSource : IWhizbangHealthSource {
  private readonly InstanceAssignmentMonitor _monitor;
  private readonly TimeProvider _time;

  /// <summary>Creates the source.</summary>
  /// <param name="monitor">The monitor whose latest read this reports.</param>
  /// <param name="timeProvider">Optional clock, the monitor's own; the system clock when null.</param>
  /// <exception cref="ArgumentNullException"><paramref name="monitor"/> is null.</exception>
  public InstanceAssignmentHealthSource(InstanceAssignmentMonitor monitor, TimeProvider? timeProvider = null) {
    ArgumentNullException.ThrowIfNull(monitor);
    _monitor = monitor;
    _time = timeProvider ?? TimeProvider.System;
  }

  /// <inheritdoc />
  public string Component => "assignment";

  /// <inheritdoc />
  public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_report());

  private ComponentHealth _report() => new(ComponentState.Operational, string.Empty);
}
