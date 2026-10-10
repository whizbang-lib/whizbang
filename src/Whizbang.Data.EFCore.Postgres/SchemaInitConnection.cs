// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Naming;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The schema-initialization connection string configured for one DbContext
/// (<c>ConnectionStrings:{name}-init</c>), or none, registered keyed by the context type so work that
/// changes schema after startup uses the same privileged connection startup migrations use.
/// </summary>
/// <remarks>
/// A deployment can give the application a role with data rights only and put the role that may
/// create, alter and analyze tables behind the <c>-init</c> key. Startup migrations always honored
/// it; the managed-object reconcile between starts, the blue-green rebuild's shadow-table swap and
/// the index-statistics <c>ANALYZE</c> ran on the application's own connection, so they failed under
/// a least-privilege role. They read the connection from here. The name resolves exactly as startup
/// resolves it, through <see cref="WhizbangNamingConvention.ResolveConnectionStringName"/>.
/// </remarks>
/// <param name="connectionString">The <c>-init</c> connection string, or <see langword="null"/> when none is configured.</param>
/// <docs>operations/infrastructure/migrations#database-roles</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SchemaInitConnectionTests.cs</tests>
public sealed class SchemaInitConnection(string? connectionString) {
  /// <summary>The suffix appended to the context's connection string name.</summary>
  public const string SUFFIX = "-init";

  /// <summary>The <c>-init</c> connection string, or <see langword="null"/> when none is configured.</summary>
  public string? ConnectionString { get; } = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;

  /// <summary>
  /// Reads the <c>-init</c> connection string for a context from configuration, resolving the name the
  /// way startup does.
  /// </summary>
  /// <param name="configuration">The host configuration, or <see langword="null"/>.</param>
  /// <param name="connectionStringName">The context's connection string name.</param>
  /// <param name="legacyConnectionStringName">The pre-convention name read as a fallback, or <see langword="null"/>.</param>
  /// <returns>The resolved value, carrying <see langword="null"/> when nothing is configured.</returns>
  public static SchemaInitConnection FromConfiguration(
      IConfiguration? configuration, string connectionStringName, string? legacyConnectionStringName) {
    ArgumentException.ThrowIfNullOrEmpty(connectionStringName);
    if (configuration is null) {
      return new SchemaInitConnection(null);
    }

    var name = WhizbangNamingConvention.ResolveConnectionStringName(
      configuration, connectionStringName, legacyConnectionStringName);
    return new SchemaInitConnection(configuration.GetConnectionString(name + SUFFIX));
  }

  /// <summary>The <c>-init</c> connection string registered for <paramref name="dbContextType"/>, if any.</summary>
  /// <param name="services">The service provider.</param>
  /// <param name="dbContextType">The DbContext type the connection was registered under.</param>
  /// <returns>The connection string, or <see langword="null"/>.</returns>
  public static string? For(IServiceProvider services, Type dbContextType) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(dbContextType);
    return services.GetKeyedService<SchemaInitConnection>(dbContextType)?.ConnectionString;
  }
}
