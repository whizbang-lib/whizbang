// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Capabilities;

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
/// Whether the library may run <c>CREATE EXTENSION</c> for it, once the operator has done the
/// server-level enablement (allowlist, preload). False for one that lives in a single database
/// (<c>pg_cron</c>).
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
/// the same way. Each entry becomes a provider-independent <see cref="CapabilityDescriptor"/>
/// (<see cref="Describe"/>), so the capability registry, its verdict and its gauge treat Postgres
/// extensions like any other capability.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions#catalog</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionCatalogTests.cs</tests>
public static class PostgresExtensionCatalog {
#pragma warning disable CA1707
  /// <summary>
  /// The provider name every Postgres extension's <see cref="CapabilityId"/> carries: the one
  /// <see cref="RequiresPostgresExtensionAttribute"/> declares requirements under.
  /// </summary>
  public const string PROVIDER = RequiresPostgresExtensionAttribute.PROVIDER;
#pragma warning restore CA1707

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
      ["Table rewrite (bloat reclaim), driven by the library's table rewriter"], LibraryMayCreate: true,
      PRELOAD | PostgresExtensionPrerequisites.LogicalWalLevel),
    new("pg_repack", "online table rewrite run by its own client program",
      ["Table rewrite (pg_repack client, in process or external job)"], LibraryMayCreate: true,
      ALLOWLIST | PostgresExtensionPrerequisites.ClientBinary),
    new("pg_partman", "partition management (a built-in fallback exists)",
      ["Ledger partition management (create_parent, then run_maintenance from the maintenance worker)"],
      LibraryMayCreate: true, ALLOWLIST),
    new("pgstattuple", "exact table bloat measurement (otherwise estimated)",
      ["Table bloat measurement"], LibraryMayCreate: true, ALLOWLIST),
    new("pg_stat_statements", "query statistics",
      ["Expensive-predicate advice", "Query diagnostics"], LibraryMayCreate: true, PRELOAD),
    new("pg_cron", "in-database scheduling",
      ["Optional in-database scheduling"], LibraryMayCreate: false, PRELOAD),
  ];

  /// <summary>
  /// The provider-independent descriptor of <paramref name="extension"/>: its purpose, features, whether
  /// the library may provision it, its prerequisites as text, and the fix for each problem state, taken
  /// from the Postgres state that maps onto it.
  /// </summary>
  /// <param name="extension">The catalog entry.</param>
  /// <returns>The descriptor, identified as <c>postgres/&lt;name&gt;</c>.</returns>
  public static CapabilityDescriptor Describe(PostgresExtensionInfo extension) {
    ArgumentNullException.ThrowIfNull(extension);
    var prerequisites = new List<string>();
    if (extension.Prerequisites.HasFlag(PostgresExtensionPrerequisites.Allowlist)) {
      prerequisites.Add("named in a managed server's extension allowlist");
    }
    if (extension.NeedsPreload) {
      prerequisites.Add($"{extension.Name} in shared_preload_libraries");
    }
    if (extension.Prerequisites.HasFlag(PostgresExtensionPrerequisites.LogicalWalLevel)) {
      prerequisites.Add("wal_level = logical");
    }
    if (extension.Prerequisites.HasFlag(PostgresExtensionPrerequisites.ClientBinary)) {
      prerequisites.Add($"the {extension.Name} client program");
    }
    var logicalWal = extension.Prerequisites.HasFlag(PostgresExtensionPrerequisites.LogicalWalLevel);
    return new CapabilityDescriptor(
      new CapabilityId(PROVIDER, extension.Name),
      extension.Enables,
      extension.Features,
      extension.LibraryMayCreate,
      prerequisites,
      new Dictionary<CapabilityState, string> {
        [CapabilityState.NotPermitted] = Remedy(extension, PostgresExtensionState.NotAllowlisted),
        [CapabilityState.NeedsServerChange] = Remedy(extension, PostgresExtensionState.NeedsPreload),
        [CapabilityState.NoPrivilege] = Remedy(extension, PostgresExtensionState.NoPrivilege),
        [CapabilityState.Missing] = Remedy(extension, PostgresExtensionState.Missing),
        [CapabilityState.Unknown] = Remedy(extension, PostgresExtensionState.Unknown),
      }) {
      // NeedsLogicalWalLevel shares NeedsServerChange with NeedsPreload, and needs its own fix.
      DetailRemedies = logicalWal
        ? new Dictionary<string, string>(StringComparer.Ordinal) {
          [nameof(PostgresExtensionState.NeedsLogicalWalLevel)] = Remedy(extension, PostgresExtensionState.NeedsLogicalWalLevel),
        }
        : new Dictionary<string, string>(StringComparer.Ordinal),
    };
  }

  /// <summary>
  /// What an operator does about <paramref name="extension"/> in <paramref name="state"/>, as one
  /// sentence for a log line or an error message.
  /// </summary>
  /// <param name="extension">The catalog entry.</param>
  /// <param name="state">Its state on some database.</param>
  /// <returns>The fix, or <c>none</c> for a usable state.</returns>
  internal static string Remedy(PostgresExtensionInfo extension, PostgresExtensionState state) {
    ArgumentNullException.ThrowIfNull(extension);
    var name = extension.Name;
    return state switch {
      PostgresExtensionState.Installed or PostgresExtensionState.Available => "none",
      PostgresExtensionState.NotAllowlisted => extension.NeedsPreload
        ? $"add {name.ToUpperInvariant()} to the server's extension allowlist (azure.extensions on Azure Database for PostgreSQL) and {name} to shared_preload_libraries, then restart the server"
        : $"add {name.ToUpperInvariant()} to the server's extension allowlist (azure.extensions on Azure Database for PostgreSQL)",
      PostgresExtensionState.NeedsPreload => $"add {name} to shared_preload_libraries, then restart the server",
      PostgresExtensionState.NeedsLogicalWalLevel =>
        "set wal_level = logical (and max_replication_slots to at least one more than the slots in use), then restart the server",
      PostgresExtensionState.NoPrivilege =>
        $"run CREATE EXTENSION {name} as a role allowed to create it (a superuser or the server's administrator role), or grant this role CREATE on the database",
      PostgresExtensionState.Missing => $"install the {name} package on the database server, or use a server image that includes it",
      _ => "the probe could not decide; check that the database is reachable and this role can connect, and for an extension that needs preloading, that this role can read shared_preload_libraries (pg_read_all_settings)",
    };
  }
}
