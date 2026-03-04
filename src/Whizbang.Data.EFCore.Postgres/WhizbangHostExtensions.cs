using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension methods for IHost/WebApplication to initialize Whizbang infrastructure.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
public static class WhizbangHostExtensions {
  /// <summary>
  /// Ensures all Whizbang database schemas are initialized before starting the application.
  /// This creates all required tables, functions, and extensions (including pgvector if needed).
  /// MUST be called before app.RunAsync() to avoid race conditions where background services
  /// attempt to use the database before schema is ready.
  ///
  /// <example>
  /// <code>
  /// var app = builder.Build();
  ///
  /// // Initialize Whizbang database BEFORE starting the app
  /// await app.EnsureWhizbangInitializedAsync();
  ///
  /// await app.RunAsync();
  /// </code>
  /// </example>
  /// </summary>
  /// <param name="host">The IHost or WebApplication instance.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static async Task EnsureWhizbangInitializedAsync(
      this IHost host,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(host);

    using var scope = host.Services.CreateScope();
    var logger = scope.ServiceProvider.GetService<ILoggerFactory>()
        ?.CreateLogger("Whizbang.Initialization");

    await DbContextInitializationRegistry.InitializeAllAsync(
        scope.ServiceProvider,
        logger,
        cancellationToken);
  }
}
