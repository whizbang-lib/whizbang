// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// How this instance reaches the database, and whether its session alive-lock is held now (#1254). The registration
/// records the mode, so the elected partition assigner judges the instance by the right signal: the alive-lock where
/// its peers can see one, the heartbeat otherwise. The assigner also renews its assignment's lease from its own
/// alive-lock while it holds it.
/// </summary>
/// <remarks>
/// Separate from <see cref="IInstanceAliveLockSource"/>, which drives the heartbeat's adaptive cadence. The Postgres
/// notifications register their shared connection as both (#1286).
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/HeartbeatConnectionModeTests.cs:Beat_WithADirectConnection_RegistersDirectAsync</tests>
public interface IInstanceConnectionModeSource {
  /// <summary>
  /// <see cref="InstanceConnectionMode.Direct"/> when this instance holds a dedicated direct connection whose alive-lock
  /// its peers can see; <see cref="InstanceConnectionMode.Pooled"/> otherwise.
  /// </summary>
  InstanceConnectionMode ConnectionMode { get; }

  /// <summary>True when the session alive-lock is held right now.</summary>
  bool IsAliveLockHeld { get; }
}

/// <summary>
/// The null default for <see cref="IInstanceConnectionModeSource"/>: pooled, no alive-lock. A storage driver with a
/// dedicated connection supplies the real source.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment</docs>
public sealed class NullInstanceConnectionModeSource : IInstanceConnectionModeSource, INullDefault {
  private NullInstanceConnectionModeSource() { }

  /// <summary>The shared instance.</summary>
  public static NullInstanceConnectionModeSource Instance { get; } = new();

  /// <inheritdoc />
  public InstanceConnectionMode ConnectionMode => InstanceConnectionMode.Pooled;

  /// <inheritdoc />
  public bool IsAliveLockHeld => false;
}
