// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Whether an extension can be used on one database, and if not, what stands in the way.
/// </summary>
/// <remarks>
/// The Postgres provider's own detail, kept in <c>CapabilityStatus.Detail</c> and mapped onto the generic
/// <c>CapabilityState</c> by <see cref="PostgresCapabilityProbe.ToCapabilityState"/>. The states are checked in the
/// order they are declared below <see cref="Available"/>, so an extension the server has no files
/// for reads <see cref="Missing"/> however the allowlist is set.
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions#states</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilityProbeTests.cs</tests>
public enum PostgresExtensionState {
  /// <summary>
  /// The probe could not decide: the database was unreachable or the query was refused, the probe is
  /// turned off or has not run yet, or the extension needs preloading and this role cannot read
  /// <c>shared_preload_libraries</c>.
  /// </summary>
  Unknown = 0,

  /// <summary>Present in the database (<c>pg_extension</c>), and preloaded when it must be.</summary>
  Installed,

  /// <summary>
  /// Creatable here: its files are on the server, the allowlist names it (or there is none), its library
  /// is preloaded when it must be, and this role may create it.
  /// </summary>
  Available,

  /// <summary>Its files are on the server, but the server's allowlist exists and does not name it.</summary>
  NotAllowlisted,

  /// <summary>Installed or creatable, but its library is not in <c>shared_preload_libraries</c>.</summary>
  NeedsPreload,

  /// <summary>
  /// Creatable by some role, but not this one: it is not a superuser and either lacks <c>CREATE</c> on
  /// the database, or the extension is not trusted and no managed-server allowlist grants it.
  /// </summary>
  NoPrivilege,

  /// <summary>The server has no files for it (<c>pg_available_extensions</c> does not list it).</summary>
  Missing,

  /// <summary>
  /// Installed or creatable and preloaded, but it decodes changes from the WAL and the server's <c>wal_level</c> is
  /// not <c>logical</c> (pg_squeeze below Postgres 19).
  /// </summary>
  NeedsLogicalWalLevel,
}
