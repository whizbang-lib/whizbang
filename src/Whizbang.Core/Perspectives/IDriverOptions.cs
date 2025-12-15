using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marker interface for driver selection in fluent perspective configuration API.
/// Extension properties from driver packages (Postgres, InMemory, SqlServer, etc.) extend this interface
/// to provide discoverable driver options via IntelliSense.
/// </summary>
/// <example>
/// Extension property from Whizbang.Data.EFCore.Postgres:
/// <code>
/// extension(IDriverOptions options) {
///     public WhizbangPerspectiveBuilder Postgres {
///         get {
///             // Registration logic
///             return new WhizbangPerspectiveBuilder(options.Services);
///         }
///     }
/// }
/// </code>
/// </example>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:WithDriver_ReturnsIDriverOptionsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:ImplementsIDriverOptions_InterfaceAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InMemoryDriverExtensionsTests.cs:InMemory_WithNonEFCoreDriverOptions_ThrowsInvalidOperationExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_WithNonEFCoreDriverOptions_ThrowsInvalidOperationExceptionAsync</tests>
public interface IDriverOptions {
  /// <summary>
  /// Gets the service collection for driver registration.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:Services_ReturnsCorrectServiceCollectionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreDriverSelectorTests.cs:IDriverOptions_Services_ReturnsSameAsDirectPropertyAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/InMemoryDriverExtensionsTests.cs:InMemory_ReturnedBuilder_HasSameServicesAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresDriverExtensionsTests.cs:Postgres_ReturnedBuilder_HasSameServicesAsync</tests>
  IServiceCollection Services { get; }
}
