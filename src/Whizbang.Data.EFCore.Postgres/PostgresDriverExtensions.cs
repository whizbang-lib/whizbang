using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension property for selecting Postgres as the EF Core driver for Whizbang perspectives.
/// Uses C# 14 extension blocks to add .Postgres property to IDriverOptions.
/// Only visible when Whizbang.Data.EFCore.Postgres package is referenced.
/// </summary>
public static class PostgresDriverExtensions {
  /// <summary>
  /// Extension block for IDriverOptions.
  /// Adds .Postgres property for selecting PostgreSQL as the database driver.
  /// </summary>
  extension(IDriverOptions options) {
    /// <summary>
    /// Configures PostgreSQL as the database driver for EF Core perspectives.
    /// Registers IPerspectiveStore&lt;T&gt; and ILensQuery&lt;T&gt; for all discovered perspective models.
    /// Uses PostgresUpsertStrategy for native PostgreSQL ON CONFLICT support.
    /// </summary>
    /// <returns>A WhizbangPerspectiveBuilder for further configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if Postgres driver is used with non-EF Core storage.</exception>
    /// <example>
    /// <code>
    /// services
    ///     .AddWhizbangPerspectives()
    ///     .WithEFCore&lt;MyDbContext&gt;()
    ///     .WithDriver.Postgres;
    /// </code>
    /// </example>
    public WhizbangPerspectiveBuilder Postgres {
      get {
        if (options is not EFCoreDriverSelector selector) {
          throw new InvalidOperationException(
              "Postgres driver can only be used with EF Core storage. " +
              "Call .WithEFCore<TDbContext>() before .WithDriver.Postgres");
        }

        RegisterEFCoreInfrastructure(
            selector.Services,
            selector.DbContextType,
            new PostgresUpsertStrategy()
        );

        return new WhizbangPerspectiveBuilder(selector.Services);
      }
    }
  }

  /// <summary>
  /// Registers EF Core infrastructure (IPerspectiveStore and ILensQuery) for all discovered models.
  /// Invokes the callback registered by source-generated module initializer (AOT-compatible, no reflection).
  /// </summary>
  private static void RegisterEFCoreInfrastructure(
      IServiceCollection services,
      Type dbContextType,
      IDbUpsertStrategy upsertStrategy) {

    // Invoke the registration callback that was registered by the
    // source-generated module initializer in the consumer assembly
    ModelRegistrationRegistry.InvokeRegistration(services, dbContextType, upsertStrategy);
  }
}
