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
  /// <para>Ensures all Whizbang database schemas are initialized before starting the application.
  /// This creates all required tables, functions, and extensions (including pgvector if needed).
  /// MUST be called before app.RunAsync() to avoid race conditions where background services
  /// attempt to use the database before schema is ready.</para>
  ///
  /// <para><example>
  /// <code>
  /// var app = builder.Build();</para>
  ///
  /// <para>// Initialize Whizbang database BEFORE starting the app
  /// await app.EnsureWhizbangInitializedAsync();</para>
  ///
  /// <para>await app.RunAsync();
  /// </code>
  /// </example></para>
  /// </summary>
  /// <param name="host">The IHost or WebApplication instance.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>data/turnkey-initialization#idempotency</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangHostExtensionsTests.cs</tests>
  public static async Task EnsureWhizbangInitializedAsync(
      this IHost host,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(host);

    // The registry's idempotence guard is keyed on the provider it is given, and the registered
    // callbacks create their own scopes — so hand it the host's ROOT provider (issue #620). A
    // per-call scope would be a fresh key every time, and this explicit call plus the hosted
    // initializer service would initialize twice; the root is the one identity a host has.
    var logger = host.Services.GetService<ILoggerFactory>()
        ?.CreateLogger("Whizbang.Initialization");

    await DbContextInitializationRegistry.InitializeAllAsync(
        host.Services,
        logger,
        cancellationToken);
  }
}
