namespace Whizbang.Core.Messaging;

/// <summary>
/// Settings for the process-wide <see cref="WorkCoordinatorGate"/>: the cap on concurrent
/// <see cref="IWorkCoordinator"/> calls and the acquire deadline. Bound from the
/// <c>Whizbang:WorkCoordinatorGate</c> configuration section; the Postgres drivers also carry
/// <c>PostgresOptions.MaxInFlightCommands</c> into <see cref="MaxConcurrent"/>, so the option that
/// has always been documented is the one that takes effect. It used to reach nothing: the gate was
/// built with a literal 50 regardless of configuration.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference#max-in-flight-commands</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGateRegistrationTests.cs</tests>
public sealed class WorkCoordinatorGateOptions {
  /// <summary>
  /// Cap on concurrent coordinator calls per process. Each slot holds at most one pooled
  /// connection, so the effective ceiling is <c>min(MaxConcurrent, MaxPoolSize)</c>. 0 or less
  /// disables the gate. Default 50.
  /// </summary>
  public int MaxConcurrent { get; set; } = 50;

  /// <summary>
  /// Acquire deadline in milliseconds. On expiry the gate logs a warning and lets the call through
  /// without a slot rather than hanging the caller; 0 or less waits without a deadline. Default 30000.
  /// </summary>
  public int AcquireTimeoutMilliseconds { get; set; } = 30_000;
}
