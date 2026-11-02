// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Startup;

/// <summary>
/// A hosted worker that is told when the host stops before the worker's start point was reached (#1636), so the worker
/// was never started: it settles any signal others wait on for its startup, so no waiter hangs on a start that never
/// comes.
/// </summary>
/// <remarks>
/// Called by the wrapper that hosts the worker (<see cref="GatedHostedService"/>), once, on the host's stopping path; a
/// worker that was started is never told.
/// </remarks>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/WhizbangWorkerRegistrationTests.cs</tests>
public interface IStartAbandonedObserver {
  /// <summary>The host stopped before this worker's start point; it was never started.</summary>
  /// <param name="stoppingToken">The host's stopping token, already canceled.</param>
  void OnStartAbandoned(CancellationToken stoppingToken);
}
