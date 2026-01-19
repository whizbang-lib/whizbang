using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangPerspectiveBuilderTests.cs:Constructor_WithValidServices_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangPerspectiveBuilderTests.cs:Constructor_WithNullServices_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangPerspectiveBuilderTests.cs:Services_ReturnsInjectedServiceCollectionAsync</tests>
/// Fluent builder for configuring Whizbang perspectives with storage providers.
/// Returned by AddWhizbangPerspectives() and enables chaining storage configuration methods.
/// </summary>
/// <example>
/// Basic usage:
/// <code>
/// services
///     .AddWhizbangPerspectives()
///     .WithEFCore&lt;MyDbContext&gt;()
///     .WithDriver.Postgres;
/// </code>
/// </example>
public sealed class WhizbangPerspectiveBuilder {
  /// <summary>
  /// Gets the service collection for registering services.
  /// </summary>
  public IServiceCollection Services { get; }

  /// <summary>
  /// Initializes a new instance of WhizbangPerspectiveBuilder.
  /// </summary>
  /// <param name="services">The service collection to configure.</param>
  public WhizbangPerspectiveBuilder(IServiceCollection services) {
    Services = services ?? throw new ArgumentNullException(nameof(services));
  }
}
