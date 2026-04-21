using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Hosted service that initializes the Whizbang database schema before workers start.
/// Registered as a plain IHostedService (not BackgroundService) so StartAsync blocks
/// until initialization completes, ensuring correct ordering with downstream workers.
/// </summary>
/// <remarks>
/// Message-type-registry population happens per-DbContext inside the init callback
/// (right after <c>EnsureWhizbangDatabaseInitializedAsync</c> + migration 039 create the
/// table), not here — each DbContext owns its own database and connection, and running
/// globally against a singleton <c>NpgsqlDataSource</c> would hit the wrong DB in
/// multi-DbContext consumers.
/// </remarks>
/// <docs>data/turnkey-initialization</docs>
internal sealed class WhizbangDatabaseInitializerService(
    IServiceProvider serviceProvider,
    ILogger<WhizbangDatabaseInitializerService> logger) : IHostedService {

  public async Task StartAsync(CancellationToken cancellationToken) {
    await DbContextInitializationRegistry.InitializeAllAsync(
        serviceProvider, logger, cancellationToken);
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
