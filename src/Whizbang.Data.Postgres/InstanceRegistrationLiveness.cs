// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// What an instance's registration records for the one liveness rule (#2126): how it reaches the database and how old
/// its heartbeat may get before the rule calls it not live. Recorded at registration, the rule judges a starting
/// instance by its own window from the moment its row exists, not by the 30 seconds it gives a row that records none.
/// </summary>
/// <param name="ConnectionMode">How the instance reaches the database; null keeps what the row records.</param>
/// <param name="LivenessWindowSeconds">The instance's liveness window in seconds; null keeps what the row records.</param>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BranchCoverage/DataPostgres/InstanceRegistrationLivenessTests.cs</tests>
public readonly record struct InstanceRegistrationLiveness(InstanceConnectionMode? ConnectionMode, int? LivenessWindowSeconds) {
  /// <summary>
  /// What the host declares, read the way the heartbeat reads it, so the registration and every beat after it say the
  /// same thing: the connection mode from <see cref="IInstanceConnectionModeSource"/> (pooled when the driver does not
  /// say) and the window from the heartbeat's cadence (<see cref="HeartbeatLivenessThreshold"/>, the default cadence
  /// when none is configured).
  /// </summary>
  /// <param name="services">The host's services; null declares nothing.</param>
  /// <returns>The declaration to record.</returns>
  public static InstanceRegistrationLiveness Of(IServiceProvider? services) {
    if (services is null) {
      return default;
    }
    var mode = services.GetService<IInstanceConnectionModeSource>()?.ConnectionMode ?? InstanceConnectionMode.Pooled;
    var cadence = services.GetService<IOptions<HeartbeatWorkerOptions>>()?.Value ?? new HeartbeatWorkerOptions();
    return new InstanceRegistrationLiveness(mode, HeartbeatLivenessThreshold.StaleThresholdSeconds(cadence));
  }
}
