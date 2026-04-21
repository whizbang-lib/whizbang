using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Hosted service that initializes the Whizbang database schema before workers start.
/// Registered as a plain IHostedService (not BackgroundService) so StartAsync blocks
/// until initialization completes, ensuring correct ordering with downstream workers.
/// </summary>
/// <docs>data/turnkey-initialization</docs>
internal sealed class WhizbangDatabaseInitializerService(
    IServiceProvider serviceProvider,
    ILogger<WhizbangDatabaseInitializerService> logger) : IHostedService {

  public async Task StartAsync(CancellationToken cancellationToken) {
    await DbContextInitializationRegistry.InitializeAllAsync(
        serviceProvider, logger, cancellationToken);

    // After schema is ready, reconcile wh_message_type_registry with the compile-time catalog.
    // Runs only if IMessageTypeRegistryPopulator + IMessageTypeCatalog are both registered — the
    // catalog is auto-wired by AddWhizbang's module initializer, the populator by the Postgres
    // driver. Skipping gracefully keeps legacy callers working.
    var populator = serviceProvider.GetService<IMessageTypeRegistryPopulator>();
    var catalog = serviceProvider.GetService<IMessageTypeCatalog>();
    if (populator is not null && catalog is not null) {
      await populator.PopulateAsync(cancellationToken);
    }
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
