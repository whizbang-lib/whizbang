using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Fluent builder for selecting EF Core database driver for Whizbang perspectives.
/// Provides the .WithDriver property as an extension point for driver packages.
/// </summary>
/// <example>
/// Usage:
/// <code>
/// services
///     .AddWhizbangPerspectives()
///     .WithEFCore&lt;MyDbContext&gt;()
///     .WithDriver.Postgres  // Extension property from driver package
/// </code>
/// </example>
public sealed class EFCoreDriverSelector : IDriverOptions {
  /// <summary>
  /// Gets the service collection for driver registration.
  /// </summary>
  public IServiceCollection Services { get; }

  /// <summary>
  /// Gets the DbContext type to use for EF Core perspective storage.
  /// </summary>
  internal Type DbContextType { get; }

  /// <summary>
  /// Initializes a new instance of EFCoreDriverSelector.
  /// </summary>
  /// <param name="services">The service collection to configure.</param>
  /// <param name="dbContextType">The DbContext type to use for storage.</param>
  internal EFCoreDriverSelector(IServiceCollection services, Type dbContextType) {
    Services = services ?? throw new ArgumentNullException(nameof(services));
    DbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  }

  /// <summary>
  /// Property that acts as the extension point for driver selection.
  /// Driver packages extend IDriverOptions with properties like .Postgres, .InMemory, etc.
  /// </summary>
  public IDriverOptions WithDriver => this;
}
