namespace Whizbang.Core.Messaging;

/// <summary>
/// Settings for the process-wide <see cref="WorkCoordinatorGate"/>: the cap on concurrent
/// <see cref="IWorkCoordinator"/> calls and the acquire deadline. Bound from the
/// <c>Whizbang:WorkCoordinatorGate</c> configuration section, which is the operator's explicit word;
/// a Postgres driver carries <c>PostgresOptions.MaxInFlightCommands</c> into <see cref="MaxConcurrent"/>
/// only when the section has not set it, and <see cref="DefaultMaxConcurrent"/> applies when neither
/// did. The documented option used to reach nothing: the gate was built with a literal 50 regardless of
/// configuration.
/// </summary>
/// <docs>operations/configuration/configuration-reference#workcoordinatorgateoptions</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGateRegistrationTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorGatePrecedenceTests.cs</tests>
public sealed class WorkCoordinatorGateOptions {
  private const int DEFAULT_MAX_CONCURRENT = 50;

  /// <summary>The cap when neither the section nor a driver sets one.</summary>
  public static int DefaultMaxConcurrent => DEFAULT_MAX_CONCURRENT;

  /// <summary>
  /// Cap on concurrent coordinator calls per process. Each slot holds at most one pooled
  /// connection, so the effective ceiling is <c>min(MaxConcurrent, MaxPoolSize)</c>. 0 or less
  /// disables the gate. Null means "not set here": a Postgres driver then supplies its
  /// <c>MaxInFlightCommands</c>, and failing that <see cref="DefaultMaxConcurrent"/> applies. A value set
  /// in the section wins over the driver's.
  /// </summary>
  public int? MaxConcurrent { get; set; }

  /// <summary>
  /// Acquire deadline in milliseconds. On expiry the gate logs a warning and lets the call through
  /// without a slot rather than hanging the caller; 0 or less waits without a deadline. Default 30000.
  /// </summary>
  public int AcquireTimeoutMilliseconds { get; set; } = 30_000;
}
