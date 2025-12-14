using Microsoft.Extensions.DependencyInjection;
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
    /// Registers IPerspectiveStore&lt;T&gt;, ILensQuery&lt;T&gt;, IInbox, IOutbox, and IEventStore
    /// for all discovered perspective models via source-generated AOT-compatible code.
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
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_WithValidEFCoreSelector_ReturnsWhizbangPerspectiveBuilderAsync</tests>
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_ReturnedBuilder_HasSameServicesAsync</tests>
    /// <tests>Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_WithNonEFCoreDriverOptions_ThrowsInvalidOperationExceptionAsync</tests>
    public WhizbangPerspectiveBuilder Postgres {
      get {
        if (options is not EFCoreDriverSelector selector) {
          throw new InvalidOperationException(
              "Postgres driver can only be used with EF Core storage. " +
              "Call .WithEFCore<TDbContext>() before .WithDriver.Postgres");
        }

        // Invoke model registration callback (infrastructure + perspectives)
        // This is registered by source-generated module initializer in consumer assembly
        // The generated code contains AOT-safe registration using concrete types
        ModelRegistrationRegistry.InvokeRegistration(
            selector.Services,
            selector.DbContextType,
            new PostgresUpsertStrategy()
        );

        return new WhizbangPerspectiveBuilder(selector.Services);
      }
    }
  }
}
