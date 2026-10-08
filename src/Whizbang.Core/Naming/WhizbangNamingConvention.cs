// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Naming;

/// <summary>
/// Central, reusable name-derivation helpers Whizbang uses to keep its
/// turnkey wiring coherent across the source generator, runtime DI, and
/// any user code that wants to follow the same convention.
/// </summary>
/// <remarks>
/// <para>
/// The EF Core source generator and the runtime turnkey path must agree on every name here: when
/// they once derived the connection string name separately, EF Core and the notification workers
/// read different keys and LISTEN/NOTIFY silently fell back to the pooled connection. The generator
/// keeps a copy (it cannot reference this assembly); a generator test and a turnkey test pin both to "db".
/// </para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference#connectionstrings-conventions</docs>
public static class WhizbangNamingConvention {

#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>
  /// The connection string a database reads when its <c>DbContext</c> names none:
  /// <c>ConnectionStrings:db</c>, with <c>db-direct</c> for notifications and <c>db-init</c> for
  /// schema initialization. The same in every service, since each service has its own configuration.
  /// </summary>
  public const string DEFAULT_CONNECTION_STRING_NAME = "db";
#pragma warning restore CA1707

  /// <summary>
  /// The connection string name earlier releases derived from a context's class name: the name minus
  /// a <c>DbContext</c> suffix, lowercased, with <c>-db</c> appended (<c>OrderServiceDbContext</c> →
  /// <c>orderservice-db</c>). Read only as a fallback; see <see cref="ResolveConnectionStringName"/>.
  /// </summary>
  /// <param name="dbContextClassName">The context's simple class name.</param>
  public static string LegacyConnectionStringName(string dbContextClassName) {
    ArgumentException.ThrowIfNullOrEmpty(dbContextClassName);
    var name = dbContextClassName.EndsWith("DbContext", StringComparison.Ordinal)
      ? dbContextClassName[..^"DbContext".Length]
      : dbContextClassName;
    return name.ToLowerInvariant() + "-db";
  }

  /// <summary>
  /// The connection string name a database reads: <paramref name="name"/>, unless nothing is configured
  /// under it (nor its <c>-direct</c> and <c>-init</c> variants) while something is under
  /// <paramref name="legacyName"/>, the name an earlier release derived. The legacy name then keeps the
  /// service working, and a warning says to rename the keys.
  /// </summary>
  /// <remarks>
  /// A bridge for services configured before the default became <c>db</c>; it is removed once services
  /// have had a release to rename their keys. Called by generated registrations and the EF Core driver.
  /// </remarks>
  /// <param name="configuration">The configuration to look in; null keeps <paramref name="name"/>.</param>
  /// <param name="name">The name the database reads.</param>
  /// <param name="legacyName">The derived name to fall back to, or null when the name was chosen explicitly.</param>
  /// <param name="logger">Where to report a fallback; null reports nothing.</param>
  /// <docs>operations/configuration/configuration-reference#connectionstrings-conventions</docs>
  /// <tests>tests/Whizbang.Core.Tests/Naming/ConnectionStringNameFallbackTests.cs</tests>
  public static string ResolveConnectionStringName(
    Microsoft.Extensions.Configuration.IConfiguration? configuration,
    string name,
    string? legacyName,
    Microsoft.Extensions.Logging.ILogger? logger = null) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    if (configuration is null || legacyName is null || _isConfigured(configuration, name) || !_isConfigured(configuration, legacyName)) {
      return name;
    }

    if (logger is not null) {
      WhizbangNamingConventionLog.LegacyConnectionStringName(logger, legacyName, name);
    }

    return legacyName;
  }

  private static bool _isConfigured(Microsoft.Extensions.Configuration.IConfiguration configuration, string name) {
    var connectionStrings = configuration.GetSection("ConnectionStrings");
    return connectionStrings[name] is not null
      || connectionStrings[name + "-direct"] is not null
      || connectionStrings[name + "-init"] is not null;
  }
}

/// <summary>Source-generated logging for connection string naming.</summary>
internal static partial class WhizbangNamingConventionLog {
  [Microsoft.Extensions.Logging.LoggerMessage(
      Level = Microsoft.Extensions.Logging.LogLevel.Warning,
      Message = "Reading connection string '{LegacyName}' (and its -direct and -init variants), the name earlier "
              + "releases derived from the context's class. Rename those keys to ConnectionStrings:{Name} "
              + "(and {Name}-direct, {Name}-init); this fallback is removed in a later release")]
  public static partial void LegacyConnectionStringName(Microsoft.Extensions.Logging.ILogger logger, string legacyName, string name);
}
