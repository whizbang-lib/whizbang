using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Seam for the actual schema-migration work invoked by <see cref="WhizbangDatabaseInitializerService"/>.
/// Extracting it lets the initializer's blocking/non-blocking + timeout orchestration be unit-tested with
/// a controllable fake, while the static <see cref="DbContextInitializationRegistry"/> remains the single
/// production implementation.
/// </summary>
internal interface ISchemaInitializationRunner {
  /// <summary>
  /// Applies all registered DbContext migrations. Idempotent (advisory-locked, hash-gated); the
  /// migrations themselves decide what, if anything, to run.
  /// </summary>
  Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="ISchemaInitializationRunner"/> — delegates to the static
/// <see cref="DbContextInitializationRegistry"/> that every EFCore Postgres DbContext self-registers into.
/// </summary>
internal sealed class DbContextSchemaInitializationRunner(
    IServiceProvider serviceProvider,
    ILogger<DbContextSchemaInitializationRunner> logger) : ISchemaInitializationRunner {
  public Task RunAsync(CancellationToken cancellationToken)
    => DbContextInitializationRegistry.InitializeAllAsync(serviceProvider, logger, cancellationToken);
}
