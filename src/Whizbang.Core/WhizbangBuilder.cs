using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core;

/// <summary>
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
public sealed class WhizbangBuilder {
  /// <summary>
  /// Gets the service collection for registering services.
  /// </summary>
  public IServiceCollection Services { get; }

  /// <summary>
  /// Initializes a new instance of WhizbangBuilder.
  /// </summary>
  /// <param name="services">The service collection to configure.</param>
  public WhizbangBuilder(IServiceCollection services) {
    Services = services ?? throw new ArgumentNullException(nameof(services));
  }
}
