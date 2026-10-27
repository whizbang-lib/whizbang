// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// A connection string as the environment variables a libpq client program reads (<c>PGHOST</c>, <c>PGPORT</c>,
/// <c>PGDATABASE</c>, <c>PGUSER</c>, <c>PGPASSWORD</c>, <c>PGPASSFILE</c>, <c>PGSSLMODE</c>, <c>PGSSLROOTCERT</c>,
/// <c>PGSSLCERT</c>, <c>PGSSLKEY</c>, <c>PGAPPNAME</c>, <c>PGCONNECT_TIMEOUT</c>), so a program such as the pg_repack
/// client connects without its connection on its command line.
/// </summary>
/// <remarks>
/// Read with Npgsql's own parser, so every synonym it accepts maps the same way. A setting that is not in the string
/// is left out, and libpq applies its own default: with no password, it reads the host's password file or service
/// file.
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/LibpqEnvironmentTests.cs</tests>
public static class LibpqEnvironment {
  /// <summary>The libpq environment for a connection string.</summary>
  /// <param name="connectionString">An Npgsql connection string.</param>
  /// <returns>The variables, by name.</returns>
  public static IReadOnlyDictionary<string, string> FromConnectionString(string connectionString) {
    ArgumentNullException.ThrowIfNull(connectionString);
    var settings = new NpgsqlConnectionStringBuilder(connectionString);
    var environment = new Dictionary<string, string>(StringComparer.Ordinal);
    _set(environment, "PGHOST", settings.Host);
    _set(environment, "PGPORT", settings.Port.ToString(CultureInfo.InvariantCulture));
    _set(environment, "PGDATABASE", settings.Database);
    _set(environment, "PGUSER", settings.Username);
    _set(environment, "PGPASSWORD", settings.Password);
    _set(environment, "PGPASSFILE", settings.Passfile);
    _set(environment, "PGSSLMODE", _sslMode(settings.SslMode));
    _set(environment, "PGSSLROOTCERT", settings.RootCertificate);
    _set(environment, "PGSSLCERT", settings.SslCertificate);
    _set(environment, "PGSSLKEY", settings.SslKey);
    _set(environment, "PGAPPNAME", settings.ApplicationName);
    _set(environment, "PGCONNECT_TIMEOUT", settings.Timeout.ToString(CultureInfo.InvariantCulture));
    return environment;
  }

  private static void _set(Dictionary<string, string> environment, string name, string? value) {
    if (!string.IsNullOrEmpty(value)) {
      environment[name] = value;
    }
  }

  // Npgsql's VerifyCA and VerifyFull are libpq's verify-ca and verify-full; the rest are the same word in lower case.
  private static string _sslMode(SslMode mode) {
    var name = mode.ToString();
    const string VERIFY = "Verify";
    return name.StartsWith(VERIFY, StringComparison.Ordinal)
      ? "verify-" + name[VERIFY.Length..].ToLowerInvariant()
      : name.ToLowerInvariant();
  }
}
