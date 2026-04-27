using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Hosted service that initializes the Whizbang database schema before workers issue SQL.
/// Registered as a plain IHostedService (not BackgroundService) so StartAsync blocks
/// until initialization completes. After migrations succeed, signals <see cref="ISchemaReadyGate"/>
/// so workers (which await the gate at the top of their ExecuteAsync) can proceed.
/// </summary>
/// <remarks>
/// On migration failure, the gate is NOT marked ready — StartAsync throws, the host aborts,
/// and workers never enter their main loop. This keeps the system in a safe halted state
/// instead of running on a broken schema.
/// </remarks>
/// <docs>data/turnkey-initialization</docs>
internal sealed class WhizbangDatabaseInitializerService(
    IServiceProvider serviceProvider,
    ISchemaReadyGate schemaReadyGate,
    ILogger<WhizbangDatabaseInitializerService> logger) : IHostedService {

  public async Task StartAsync(CancellationToken cancellationToken) {
    await DbContextInitializationRegistry.InitializeAllAsync(
        serviceProvider, logger, cancellationToken);
    schemaReadyGate.MarkReady();
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
