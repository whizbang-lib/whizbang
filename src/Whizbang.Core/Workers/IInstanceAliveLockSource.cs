// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Surface that <see cref="HeartbeatWorker"/> consults each tick to decide
/// whether the adaptive cadence should run fast (no lock held — fallback to
/// heartbeat-table liveness) or slow (lock held — direct conn is the primary
/// liveness signal).
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <c>Whizbang.Data.Postgres.Notifications.PgSharedNotifyConnection</c> (the LISTEN direct
/// connection, which takes the lock) and registered by the Postgres notifications (#1286). It reports the lock held
/// only on a connection of its own: behind the pooled fallback it reports none. Hosts without the notifications keep
/// <see cref="NullInstanceAliveLockSource"/>, and <see cref="HeartbeatWorker"/> stays on the fast cadence.
/// </para>
/// <para>
/// The slow cadence is safe because every reader that judges a peer by its heartbeat also honors the lock: a direct
/// instance holding it is live whatever its heartbeat's age (the claim's rank, the stale-peer reap, the partition
/// assigner, the standby handshake), and once the lock is gone it is judged by its heartbeat at once. The claim loop
/// passes this source's answer to each claim, so a direct instance holding the lock is not asked to re-register
/// between slow beats.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerAdaptiveCadenceTests.cs:LockHeld_AdvisoryLockMode_UsesSlowCadenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerAdaptiveCadenceTests.cs:LockNotHeld_AdvisoryLockMode_UsesFastCadenceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/HeartbeatWorkerAdaptiveCadenceTests.cs:LockTransitionsHeldToNotHeld_NextResolveReturnsFastCadenceAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AliveLockSourceRegistrationTests.cs:AddWhizbangPostgresNotifications_RegistersTheSharedConnectionAsTheAliveLockSourceAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/ClaimWorkerAliveLockTests.cs:AClaim_WhileTheAliveLockIsHeld_SaysSoAsync</tests>
public interface IInstanceAliveLockSource {
  /// <summary>True when the session-level alive-lock is currently held by this instance.</summary>
  bool IsAliveLockHeld { get; }
}

/// <summary>
/// The framework's null default for <see cref="IInstanceAliveLockSource"/>: no alive lock is ever held,
/// so the heartbeat's watchdog treats the regular cadence as the only one. A storage-backed instance
/// monitor supplies the real source.
/// </summary>
public sealed class NullInstanceAliveLockSource : IInstanceAliveLockSource, INullDefault {
  private NullInstanceAliveLockSource() { }

  /// <summary>The shared instance.</summary>
  public static NullInstanceAliveLockSource Instance { get; } = new();

  /// <inheritdoc />
  public bool IsAliveLockHeld => false;
}

