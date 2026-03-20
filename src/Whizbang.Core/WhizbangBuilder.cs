#pragma warning disable S3604, S3928 // Primary constructor field/property initializers are intentional

using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/WhizbangBuilderTests.cs:Constructor_WithValidServices_InitializesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/WhizbangBuilderTests.cs:Constructor_WithNullServices_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/WhizbangBuilderTests.cs:Services_ReturnsInjectedServiceCollectionAsync</tests>
/// Fluent builder for configuring Whizbang infrastructure with storage providers.
/// Returned by AddWhizbang() and enables chaining storage configuration methods.
/// </summary>
/// <example>
/// Unified API usage:
/// <code>
/// services
///     .AddWhizbang()
///     .WithEFCore&lt;MyDbContext&gt;()
///     .WithDriver.Postgres;
/// </code>
/// </example>
/// <remarks>
/// Initializes a new instance of WhizbangBuilder.
/// </remarks>
/// <param name="services">The service collection to configure.</param>
public sealed class WhizbangBuilder(IServiceCollection services) {
  /// <summary>
  /// Gets the service collection for registering services.
  /// </summary>
  public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));
}
