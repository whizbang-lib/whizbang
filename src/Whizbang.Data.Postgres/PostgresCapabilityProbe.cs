// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using Whizbang.Core.Capabilities;

namespace Whizbang.Data.Postgres;

/// <summary>What the probe reads about one extension on one database.</summary>
/// <param name="Name">The extension.</param>
/// <param name="InstalledVersion">Its version in <c>pg_extension</c>, or null when not installed.</param>
/// <param name="ControlFilesPresent">Whether <c>pg_available_extensions</c> lists it.</param>
/// <param name="Trusted">Whether any available version is marked trusted (<c>pg_available_extension_versions</c>).</param>
/// <param name="Allowlist">
/// The managed-server allowlist (<c>current_setting('azure.extensions', true)</c>), or null when the
/// server has no such setting, which means no allowlist.
/// </param>
/// <param name="SharedPreloadLibraries">
/// The server's <c>shared_preload_libraries</c>, or null when this role may not read it (it needs
/// <c>pg_read_all_settings</c> or superuser).
/// </param>
/// <param name="CanCreateInDatabase">Whether this role has <c>CREATE</c> on the database.</param>
/// <param name="IsSuperuser">Whether this role is a superuser.</param>
/// <docs>operations/infrastructure/postgres-extensions#states</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilityProbeTests.cs</tests>
public readonly record struct PostgresExtensionProbeRow(
  string Name,
  string? InstalledVersion,
  bool ControlFilesPresent,
  bool Trusted,
  string? Allowlist,
  string? SharedPreloadLibraries,
  bool CanCreateInDatabase,
  bool IsSuperuser) {
  /// <summary>
  /// The version <c>CREATE EXTENSION</c> installs and <c>ALTER EXTENSION ... UPDATE</c> moves to
  /// (<c>pg_available_extensions.default_version</c>), or null when the server has no files for it.
  /// </summary>
  public string? DefaultVersion { get; init; }
}

/// <summary>
/// Reads which catalog extensions a database can use, in one query, and derives each one's state.
/// </summary>
/// <remarks>
/// <para>
/// One query per database: extensions are per database, while the allowlist and the preload list are
/// per server. It reads <c>pg_extension</c>, <c>pg_available_extensions</c>,
/// <c>pg_available_extension_versions</c> (the trusted flag), the default version an install or update reaches, the
/// managed-server allowlist setting,
/// <c>shared_preload_libraries</c>, and this role's superuser flag and <c>CREATE</c> privilege. It is
/// a plain catalog read: nothing is created and no migration is involved.
/// </para>
/// <para>
/// <c>shared_preload_libraries</c> is read from <c>pg_settings</c>, which hides it from a role without
/// <c>pg_read_all_settings</c>, rather than with <c>current_setting</c>, which raises for that role and
/// would cost the whole probe. Hidden, it leaves only the extensions that need preloading Unknown.
/// </para>
/// <para>
/// The query throws when the database cannot be read (unreachable, refused, timed out); the capability
/// registry turns that into Unknown with the reason, as it does for every provider, so the probe never
/// stops startup.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/postgres-extensions#states</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilityProbeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilityProviderTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresCapabilityProbeIntegrationTests.cs</tests>
public static class PostgresCapabilityProbe {
  /// <summary>The probe's one query; <c>@names</c> is the catalog's extension names, in order.</summary>
  internal const string SQL = """
    SELECT n.name,
           e.extversion,
           a.name IS NOT NULL AS available,
           COALESCE((SELECT bool_or(v.trusted)
                     FROM pg_catalog.pg_available_extension_versions v
                     WHERE v.name = n.name), false) AS trusted,
           current_setting('azure.extensions', true) AS allowlist,
           (SELECT s.setting FROM pg_catalog.pg_settings s WHERE s.name = 'shared_preload_libraries') AS preload,
           has_database_privilege(current_database(), 'CREATE') AS can_create,
           current_setting('is_superuser') = 'on' AS is_superuser,
           a.default_version
    FROM unnest(@names::text[]) WITH ORDINALITY AS n(name, ord)
    LEFT JOIN pg_catalog.pg_extension e ON e.extname = n.name
    LEFT JOIN pg_catalog.pg_available_extensions a ON a.name = n.name
    ORDER BY n.ord
    """;

  /// <summary>
  /// Parses a comma-separated server setting (an allowlist or <c>shared_preload_libraries</c>) into a
  /// set compared without regard to case, with spaces and double quotes trimmed and empty entries dropped.
  /// </summary>
  /// <param name="value">The setting, or null when the server has no such setting.</param>
  /// <returns>The names, or null when <paramref name="value"/> is null. An empty setting is an empty set.</returns>
  public static IReadOnlySet<string>? ParseList(string? value) {
    if (value is null) {
      return null;
    }
    return value
      .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
      .Select(entry => entry.Trim('"'))
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Derives one extension's state from what the probe read. Pure: the same row gives the same state.
  /// </summary>
  /// <param name="extension">The catalog entry.</param>
  /// <param name="row">What the probe read for it.</param>
  /// <returns>
  /// The state, checked in the order the states are declared. An extension that needs preloading reads
  /// <see cref="PostgresExtensionState.Unknown"/> where the answer would turn on a preload list this role
  /// cannot read.
  /// </returns>
  public static PostgresExtensionState DeriveState(PostgresExtensionInfo extension, PostgresExtensionProbeRow row) {
    ArgumentNullException.ThrowIfNull(extension);
    // True, false, or null when the list could not be read.
    var preloaded = extension.NeedsPreload ? _listed(row.SharedPreloadLibraries, extension.Name) : true;
    if (row.InstalledVersion is not null) {
      return preloaded switch {
        true => PostgresExtensionState.Installed,
        false => PostgresExtensionState.NeedsPreload,
        null => PostgresExtensionState.Unknown,
      };
    }
    if (!row.ControlFilesPresent) {
      return PostgresExtensionState.Missing;
    }
    if (_listed(row.Allowlist, extension.Name) == false) {
      return PostgresExtensionState.NotAllowlisted;
    }
    return preloaded switch {
      null => PostgresExtensionState.Unknown,
      false => PostgresExtensionState.NeedsPreload,
      true => _canCreate(row) ? PostgresExtensionState.Available : PostgresExtensionState.NoPrivilege,
    };
  }

  /// <summary>
  /// Maps a Postgres state onto the generic capability state: Installed is Ready, Available is
  /// Provisionable, NotAllowlisted is NotPermitted, NeedsPreload is NeedsServerChange; NoPrivilege, Missing
  /// and Unknown keep their names.
  /// </summary>
  /// <param name="state">The Postgres state.</param>
  /// <returns>The generic state.</returns>
  public static CapabilityState ToCapabilityState(PostgresExtensionState state) => state switch {
    PostgresExtensionState.Installed => CapabilityState.Ready,
    PostgresExtensionState.Available => CapabilityState.Provisionable,
    PostgresExtensionState.NotAllowlisted => CapabilityState.NotPermitted,
    PostgresExtensionState.NeedsPreload => CapabilityState.NeedsServerChange,
    PostgresExtensionState.NoPrivilege => CapabilityState.NoPrivilege,
    PostgresExtensionState.Missing => CapabilityState.Missing,
    _ => CapabilityState.Unknown,
  };

  /// <summary>Runs the probe's query on an open connection: one row per catalog entry, by name. Throws on failure.</summary>
  internal static async Task<Dictionary<string, PostgresExtensionProbeRow>> ReadAsync(
      NpgsqlConnection connection, IReadOnlyList<PostgresExtensionInfo> catalog, CancellationToken cancellationToken) {
    await using var command = connection.CreateCommand();
    command.CommandText = SQL;
    command.Parameters.Add(new NpgsqlParameter<string[]>("names", [.. catalog.Select(e => e.Name)]));
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    var rows = new Dictionary<string, PostgresExtensionProbeRow>(StringComparer.Ordinal);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var row = new PostgresExtensionProbeRow(
        Name: reader.GetString(0),
        InstalledVersion: _nullableString(reader, 1),
        ControlFilesPresent: reader.GetBoolean(2),
        Trusted: reader.GetBoolean(3),
        Allowlist: _nullableString(reader, 4),
        SharedPreloadLibraries: _nullableString(reader, 5),
        CanCreateInDatabase: reader.GetBoolean(6),
        IsSuperuser: reader.GetBoolean(7)) {
        DefaultVersion = _nullableString(reader, 8),
      };
      rows[row.Name] = row;
    }
    return rows;
  }

  private static string? _nullableString(DbDataReader reader, int ordinal) =>
    reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

  /// <summary>Whether a comma-separated setting names <paramref name="name"/>; null when there is no setting.</summary>
  private static bool? _listed(string? setting, string name) => ParseList(setting)?.Contains(name);

  /// <summary>
  /// A superuser creates anything. Any other role needs <c>CREATE</c> on the database, and an untrusted
  /// extension also needs a managed-server allowlist, which is what lets that server's administrator
  /// role create it.
  /// </summary>
  private static bool _canCreate(PostgresExtensionProbeRow row) =>
    row.IsSuperuser || (row.CanCreateInDatabase && (row.Trusted || row.Allowlist is not null));
}
