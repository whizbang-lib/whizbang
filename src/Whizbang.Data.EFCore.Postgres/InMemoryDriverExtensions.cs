using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension property for selecting InMemory as the EF Core driver for Whizbang perspectives.
/// Uses C# 14 extension blocks to add .InMemory property to IDriverOptions.
/// Only visible when Whizbang.Data.EFCore.Postgres package is referenced.
/// </summary>
public static class InMemoryDriverExtensions {
  /// <summary>
  /// Extension block for IDriverOptions.
  /// Adds .InMemory property for selecting EF Core InMemory provider as the database driver.
  /// </summary>
  extension(IDriverOptions options) {
    /// <summary>
    /// Configures EF Core InMemory provider as the database driver for perspectives.
    /// Registers IPerspectiveStore&lt;T&gt;, ILensQuery&lt;T&gt;, IInbox, IOutbox, and IEventStore
    /// for all discovered perspective models via source-generated AOT-compatible code.
    /// Uses InMemoryUpsertStrategy for fast, isolated testing.
    /// </summary>
    /// <returns>A WhizbangPerspectiveBuilder for further configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if InMemory driver is used with non-EF Core storage.</exception>
    /// <example>
    /// <code>
    /// services
    ///     .AddWhizbangPerspectives()
    ///     .WithEFCore&lt;MyDbContext&gt;()
    ///     .WithDriver.InMemory;
    /// </code>
    /// </example>
    public WhizbangPerspectiveBuilder InMemory {
      get {
        if (options is not EFCoreDriverSelector selector) {
          throw new InvalidOperationException(
              "InMemory driver can only be used with EF Core storage. " +
              "Call .WithEFCore<TDbContext>() before .WithDriver.InMemory");
        }

        // Invoke model registration callback (infrastructure + perspectives)
        // This is registered by source-generated module initializer in consumer assembly
        // The generated code contains AOT-safe registration using concrete types
        ModelRegistrationRegistry.InvokeRegistration(
            selector.Services,
            selector.DbContextType,
            new InMemoryUpsertStrategy()
        );

        return new WhizbangPerspectiveBuilder(selector.Services);
      }
    }
  }
}
