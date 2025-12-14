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
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:Services_ReturnsCorrectServiceCollectionAsync</tests>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:IDriverOptions_Services_ReturnsSameAsDirectPropertyAsync</tests>
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
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:Constructor_WithNullServices_ThrowsArgumentNullExceptionAsync</tests>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:Constructor_WithNullDbContextType_ThrowsArgumentNullExceptionAsync</tests>
  internal EFCoreDriverSelector(IServiceCollection services, Type dbContextType) {
    Services = services ?? throw new ArgumentNullException(nameof(services));
    DbContextType = dbContextType ?? throw new ArgumentNullException(nameof(dbContextType));
  }

  /// <summary>
  /// Property that acts as the extension point for driver selection.
  /// Driver packages extend IDriverOptions with properties like .Postgres, .InMemory, etc.
  /// </summary>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:WithDriver_ReturnsIDriverOptionsAsync</tests>
  /// <tests>Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:WithDriver_ReturnsSelfAsync</tests>
  public IDriverOptions WithDriver => this;
}
