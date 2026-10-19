// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// What a server needs, beyond the extension's files, before an extension can be used.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions#catalog</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionCatalogTests.cs</tests>
[Flags]
public enum PostgresExtensionPrerequisites {
  /// <summary>Nothing beyond the files.</summary>
  None = 0,

  /// <summary>
  /// A managed server's extension allowlist must name it. Every extension carries this: a server with
  /// an allowlist refuses any extension the list does not name, and a server without one allows all.
  /// </summary>
  Allowlist = 1,

  /// <summary>Its library must be in <c>shared_preload_libraries</c>, which takes a server restart.</summary>
  SharedPreloadLibraries = 2,

  /// <summary>The server must run with <c>wal_level = logical</c>.</summary>
  LogicalWalLevel = 4,

  /// <summary>The work is done by a client program run outside the process.</summary>
  ClientBinary = 8,
}

/// <summary>
/// One extension the library knows about: what it enables, which library features use it, whether the
/// library may create it, and what the server needs before it can.
/// </summary>
/// <param name="Name">The extension's name, as <c>CREATE EXTENSION</c> takes it.</param>
/// <param name="Enables">What it enables, in a phrase, for the startup report.</param>
/// <param name="Features">The library features that use it.</param>
/// <param name="LibraryMayCreate">
/// Whether the library may run <c>CREATE EXTENSION</c> for it. False for an extension an operator
/// installs and runs by hand (<c>pg_repack</c>) or one that lives in a single database (<c>pg_cron</c>).
/// </param>
/// <param name="Prerequisites">What the server needs beyond the extension's files.</param>
/// <docs>operations/infrastructure/postgres-extensions#catalog</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionCatalogTests.cs</tests>
public sealed record PostgresExtensionInfo(
  string Name,
  string Enables,
  IReadOnlyList<string> Features,
  bool LibraryMayCreate,
  PostgresExtensionPrerequisites Prerequisites) {
  /// <summary>
  /// Whether its library must be in <c>shared_preload_libraries</c>. The library carries the
  /// extension's name for every entry in the catalog.
  /// </summary>
  public bool NeedsPreload => Prerequisites.HasFlag(PostgresExtensionPrerequisites.SharedPreloadLibraries);
}

/// <summary>
/// The extensions the library knows about, and what an operator does about each state.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is the one place an extension's purpose and prerequisites are written down, so the
/// startup report, the gauge and (in later work) the configuration checks all describe an extension
/// the same way. <see cref="PostgresCapabilities"/> takes a catalog of its own, so a package can probe
/// for an extension the built-in list does not name.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions#catalog</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionCatalogTests.cs</tests>
public static class PostgresExtensionCatalog {
  private const PostgresExtensionPrerequisites ALLOWLIST = PostgresExtensionPrerequisites.Allowlist;
  private const PostgresExtensionPrerequisites PRELOAD =
    PostgresExtensionPrerequisites.Allowlist | PostgresExtensionPrerequisites.SharedPreloadLibraries;

  /// <summary>The built-in entries, in report order.</summary>
  public static IReadOnlyList<PostgresExtensionInfo> BuiltIn { get; } = [
    new("vector", "vector fields and their similarity indexes",
      ["Vector fields on perspectives"], LibraryMayCreate: true, ALLOWLIST),
    new("pg_trgm", "substring (trigram) indexes",
      ["Substring indexes on perspective fields"], LibraryMayCreate: true, ALLOWLIST),
    new("pg_squeeze", "online table rewrite without a long exclusive lock",
      ["Table rewrite (bloat reclaim)"], LibraryMayCreate: true,
      PRELOAD | PostgresExtensionPrerequisites.LogicalWalLevel),
    new("pg_repack", "online table rewrite run by its own client program",
      ["Table rewrite runbook (detection only)"], LibraryMayCreate: false,
      ALLOWLIST | PostgresExtensionPrerequisites.ClientBinary),
    new("pg_partman", "partition management (a built-in fallback exists)",
      ["Ledger partition management"], LibraryMayCreate: true, ALLOWLIST),
    new("pgstattuple", "exact table bloat measurement (otherwise estimated)",
      ["Table bloat measurement"], LibraryMayCreate: true, ALLOWLIST),
    new("pg_stat_statements", "query statistics",
      ["Expensive-predicate advice", "Query diagnostics"], LibraryMayCreate: true, PRELOAD),
    new("pg_cron", "in-database scheduling",
      ["Optional in-database scheduling"], LibraryMayCreate: false, PRELOAD),
  ];

  /// <summary>
  /// What an operator does about <paramref name="extension"/> in <paramref name="state"/>, as one
  /// sentence for a log line or an error message.
  /// </summary>
  /// <param name="extension">The catalog entry.</param>
  /// <param name="state">Its state on some database.</param>
  /// <returns>The fix, or <c>none</c> for a usable state.</returns>
  public static string Remedy(PostgresExtensionInfo extension, PostgresExtensionState state) {
    ArgumentNullException.ThrowIfNull(extension);
    var name = extension.Name;
    return state switch {
      PostgresExtensionState.Installed or PostgresExtensionState.Available => "none",
      PostgresExtensionState.NotAllowlisted => extension.NeedsPreload
        ? $"add {name.ToUpperInvariant()} to the server's extension allowlist (azure.extensions on Azure Database for PostgreSQL) and {name} to shared_preload_libraries, then restart the server"
        : $"add {name.ToUpperInvariant()} to the server's extension allowlist (azure.extensions on Azure Database for PostgreSQL)",
      PostgresExtensionState.NeedsPreload => $"add {name} to shared_preload_libraries, then restart the server",
      PostgresExtensionState.NoPrivilege =>
        $"run CREATE EXTENSION {name} as a role allowed to create it (a superuser or the server's administrator role), or grant this role CREATE on the database",
      PostgresExtensionState.Missing => $"install the {name} package on the database server, or use a server image that includes it",
      _ => "the probe could not decide; check that the database is reachable and this role can connect, and for an extension that needs preloading, that this role can read shared_preload_libraries (pg_read_all_settings)",
    };
  }
}
