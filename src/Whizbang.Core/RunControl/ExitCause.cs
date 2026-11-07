// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// Why this process exits (#2126): the closed set every exit is classified into, by the one exit path
/// (<see cref="IWhizbangInstanceExit"/>). The name is written as <c>ExitCause</c> in the exit's log entry and in the
/// <c>exit_cause</c> column of <c>wh_instance_exits</c>, and each cause has one exit code
/// (<see cref="ExitCauses.ExitCodeOf"/>). Names, values and codes are a contract with operators and supervisors: a test
/// fails when one changes.
/// </summary>
/// <docs>operations/startup/instance-exits</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/ExitCauseContractTests.cs</tests>
public enum ExitCause {
  /// <summary>The host was asked to stop (a SIGTERM, Ctrl+C, or the application stopping itself). Exit code 0.</summary>
  HostShutdown = 0,

  /// <summary>An operator stopped the instance through the killswitch. Exit code 0.</summary>
  OperatorStop = 1,

  /// <summary>
  /// The instance stood down for a newer version, and that version's migration committed beneath it: it exits as the
  /// standby handshake promises, for the orchestrator to replace it. Exit code 0.
  /// </summary>
  UpgradeStandDown = 2,

  /// <summary>Something running in the process failed and stopped the host (a faulted hosted worker). Exit code 1.</summary>
  Crash = 3,

  /// <summary>The exit could not be classified. Exit code 2.</summary>
  Unknown = 4,

  /// <summary>
  /// The fleet evicted this instance: a peer reaped it as dead by the one liveness rule and tombstoned it, so its
  /// heartbeat is refused. It exits so the host restarts it under a new instance id. Exit code 3.
  /// </summary>
  ClusterEviction = 5,

  /// <summary>Startup could not complete (the version record could not be read, a required capability is not ready). Exit code 4.</summary>
  StartupFailure = 6,

  /// <summary>The schema is newer than this binary, so this binary cannot run against it. Exit code 5.</summary>
  SchemaIncompatible = 7,
}

/// <summary>The exit code and the intent of each <see cref="ExitCause"/> (#2126).</summary>
/// <docs>operations/startup/instance-exits</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/ExitCauseContractTests.cs</tests>
public static class ExitCauses {
  /// <summary>
  /// The process exit code of <paramref name="cause"/>: 0 for an intended exit, non-zero for a failure, so a supervisor
  /// restarts the process. A value outside the enum is <see cref="ExitCause.Unknown"/>'s code.
  /// </summary>
  /// <param name="cause">The cause.</param>
  /// <returns>The exit code.</returns>
  public static int ExitCodeOf(ExitCause cause) => throw new NotImplementedException(cause.ToString());

  /// <summary>Whether <paramref name="cause"/> is an intended stop (exit code 0) rather than a failure.</summary>
  /// <param name="cause">The cause.</param>
  /// <returns><see langword="true"/> for an intended stop.</returns>
  public static bool IsIntended(ExitCause cause) => ExitCodeOf(cause) == 0;
}
