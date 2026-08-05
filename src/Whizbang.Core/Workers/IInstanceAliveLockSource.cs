namespace Whizbang.Core.Workers;

/// <summary>
/// Surface that <see cref="HeartbeatWorker"/> consults each tick to decide
/// whether the adaptive cadence should run fast (no lock held — fallback to
/// heartbeat-table liveness) or slow (lock held — direct conn is the primary
/// liveness signal).
/// </summary>
/// <remarks>
/// Implemented by <c>Whizbang.Data.Postgres.Notifications.PgSharedNotifyConnection</c>
/// (the existing LISTEN direct conn). Hosts that don't enable the direct conn don't
/// register an implementation; <see cref="HeartbeatWorker"/> then sees null and
/// stays on the fast cadence (today's behaviour).
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerAdaptiveCadenceTests.cs:LockHeld_AdvisoryLockMode_UsesSlowCadenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerAdaptiveCadenceTests.cs:LockNotHeld_AdvisoryLockMode_UsesFastCadenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerAdaptiveCadenceTests.cs:LockTransitionsHeldToNotHeld_NextResolveReturnsFastCadenceAsync</tests>
public interface IInstanceAliveLockSource {
  /// <summary>True when the session-level alive-lock is currently held by this instance.</summary>
  bool IsAliveLockHeld { get; }
}
