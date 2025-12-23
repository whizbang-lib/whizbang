using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_WithValidBuilder_ReturnsEFCoreDriverSelectorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_ReturnedSelector_HasCorrectServicesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_CanChainToWithDriverAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_MultipleContextTypes_CreatesDistinctSelectorsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_ReturnedSelector_ImplementsIDriverOptionsAsync</tests>
/// Extension methods for configuring EF Core as the storage provider for Whizbang perspectives.
/// Uses C# 14 extension blocks for clean syntax without 'this' keyword.
/// Provides extensions for both WhizbangBuilder (unified API) and WhizbangPerspectiveBuilder (legacy).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case", Justification = "Extension blocks are implicitly named by their target type - cannot be explicitly renamed")]
public static class EFCoreExtensions {
  /// <summary>
  /// Extension block for WhizbangBuilder (unified API).
  /// Adds .WithEFCore&lt;TDbContext&gt;() method for selecting EF Core as the storage provider.
  /// </summary>
  extension(WhizbangBuilder builder) {
    /// <summary>
    /// Configures EF Core as the storage provider for all Whizbang infrastructure using the specified DbContext.
    /// Returns an EFCoreDriverSelector that provides .WithDriver property for driver selection.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core DbContext type that contains perspective configurations.</typeparam>
    /// <returns>An EFCoreDriverSelector for selecting the database driver (Postgres, InMemory, etc.).</returns>
    /// <example>
    /// Unified API usage:
    /// <code>
    /// services
    ///     .AddWhizbang()
    ///     .WithEFCore&lt;MyDbContext&gt;()
    ///     .WithDriver.Postgres;
    /// </code>
    /// </example>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_WithValidBuilder_ReturnsEFCoreDriverSelectorAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_ReturnedSelector_HasCorrectServicesAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_CanChainToWithDriverAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_MultipleContextTypes_CreatesDistinctSelectorsAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_ReturnedSelector_ImplementsIDriverOptionsAsync</tests>
    public EFCoreDriverSelector WithEFCore<TDbContext>()
        where TDbContext : DbContext {
      return new EFCoreDriverSelector(builder.Services, typeof(TDbContext));
    }
  }

  /// <summary>
  /// Extension block for WhizbangPerspectiveBuilder (legacy API).
  /// Adds .WithEFCore&lt;TDbContext&gt;() method for selecting EF Core as the storage provider.
  /// </summary>
  extension(WhizbangPerspectiveBuilder builder) {
    /// <summary>
    /// Configures EF Core as the storage provider for perspectives using the specified DbContext.
    /// Returns an EFCoreDriverSelector that provides .WithDriver property for driver selection.
    /// </summary>
    /// <typeparam name="TDbContext">The EF Core DbContext type that contains perspective configurations.</typeparam>
    /// <returns>An EFCoreDriverSelector for selecting the database driver (Postgres, InMemory, etc.).</returns>
    /// <example>
    /// <code>
    /// services
    ///     .AddWhizbangPerspectives()
    ///     .WithEFCore&lt;MyDbContext&gt;()
    ///     .WithDriver.Postgres;
    /// </code>
    /// </example>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_WithValidBuilder_ReturnsEFCoreDriverSelectorAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_ReturnedSelector_HasCorrectServicesAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_CanChainToWithDriverAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_MultipleContextTypes_CreatesDistinctSelectorsAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreExtensionsTests.cs:WithEFCore_ReturnedSelector_ImplementsIDriverOptionsAsync</tests>
    public EFCoreDriverSelector WithEFCore<TDbContext>()
        where TDbContext : DbContext {
      return new EFCoreDriverSelector(builder.Services, typeof(TDbContext));
    }
  }
}
