using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Binds <see cref="PostgresOptions"/> per named database from
/// <c>Whizbang:Postgres:&lt;database&gt;</c>, where the database's name is its connection-string
/// name (<c>orders-db</c>). Configuration applies after the code's <c>Configure</c> callbacks, so it
/// overrides per key and an unconfigured key keeps the code value.
/// </summary>
/// <remarks>
/// Every registered database is reachable by name through
/// <c>IOptionsMonitor&lt;PostgresOptions&gt;.Get("&lt;database&gt;")</c>. The unnamed instance — the one
/// the framework itself resolves through <c>IOptions&lt;PostgresOptions&gt;</c> — binds from the FIRST
/// database registered; with no database registered it keeps its code values. The EF Core
/// Postgres driver registers its DbContext's database automatically.
/// </remarks>
/// <docs>operations/configuration/configuration-reference#postgres-databases</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresOptionsConfigurationTests.cs</tests>
public static class PostgresOptionsConfiguration {
  /// <summary>Parent section; each child key is a database (connection-string) name.</summary>
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  public const string CONFIGURATION_SECTION = "Whizbang:Postgres";
#pragma warning restore CA1707

  /// <summary>
  /// Registers <paramref name="databaseName"/> as a database whose <see cref="PostgresOptions"/>
  /// bind from <c>Whizbang:Postgres:&lt;databaseName&gt;</c>. Idempotent; the first database
  /// registered also supplies the unnamed instance.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="databaseName">The database's connection-string name, e.g. <c>orders-db</c>.</param>
  /// <returns>The service collection for chaining.</returns>
  public static IServiceCollection AddWhizbangPostgresOptionsBinding(this IServiceCollection services, string databaseName) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

    services.AddOptions<PostgresOptions>();
    // An empty root unless the host registered one, so an unconfigured host keeps its code values.
    services.TryAddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
    // The first database registered is the unnamed instance's; later ones are reachable by name only.
    services.TryAddSingleton(new PostgresDefaultDatabase(databaseName));
    services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<PostgresOptions>, PostgresOptionsPostConfigure>());
    return services;
  }
}
