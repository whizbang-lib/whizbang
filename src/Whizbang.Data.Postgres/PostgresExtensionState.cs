// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Whether an extension can be used on one database, and if not, what stands in the way.
/// </summary>
/// <remarks>
/// <see cref="Installed"/> and <see cref="Available"/> are usable; every other state names the one
/// thing to fix (see <see cref="PostgresExtensionCatalog.Remedy"/>). The states are checked in the
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
}

/// <summary>One extension's state on one database.</summary>
/// <param name="Extension">The catalog entry.</param>
/// <param name="State">Its state.</param>
/// <param name="InstalledVersion">The installed version, when it is in the database; otherwise null.</param>
/// <docs>operations/infrastructure/postgres-extensions#states</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionCatalogTests.cs</tests>
public sealed record PostgresExtensionStatus(
  PostgresExtensionInfo Extension,
  PostgresExtensionState State,
  string? InstalledVersion) {
  /// <summary>Whether the extension can be used: <see cref="PostgresExtensionState.Installed"/> or <see cref="PostgresExtensionState.Available"/>.</summary>
  public bool IsUsable => State is PostgresExtensionState.Installed or PostgresExtensionState.Available;
}

/// <summary>Every catalog extension's state on one database, as one probe read it.</summary>
/// <param name="Database">The database's name, as its driver registered it.</param>
/// <param name="Extensions">One status per catalog entry, in catalog order.</param>
/// <param name="ProbeFailure">Why the probe could not read this database, or null when it did.</param>
/// <docs>operations/infrastructure/postgres-extensions#states</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionCatalogTests.cs</tests>
public sealed record PostgresDatabaseExtensions(
  string Database,
  IReadOnlyList<PostgresExtensionStatus> Extensions,
  string? ProbeFailure) {
  /// <summary>
  /// The answer for a database the probe could not read: every catalog entry
  /// <see cref="PostgresExtensionState.Unknown"/>, and the reason kept for the report.
  /// </summary>
  /// <param name="database">The database.</param>
  /// <param name="catalog">The extensions to report.</param>
  /// <param name="reason">Why the probe could not read it.</param>
  /// <returns>The answer.</returns>
  public static PostgresDatabaseExtensions Unknown(
      string database, IReadOnlyList<PostgresExtensionInfo> catalog, string reason) {
    ArgumentNullException.ThrowIfNull(catalog);
    return new(database, [.. catalog.Select(e => new PostgresExtensionStatus(e, PostgresExtensionState.Unknown, null))], reason);
  }
}
