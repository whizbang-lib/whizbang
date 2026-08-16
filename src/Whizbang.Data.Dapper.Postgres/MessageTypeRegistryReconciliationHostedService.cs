using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Runs <see cref="IMessageTypeRegistryPopulator.PopulateAsync"/> once at
/// startup — after the schema gate opens, when one is registered — to reconcile
/// the persisted message-type registry against the compile-time
/// <see cref="IMessageTypeCatalog"/>. Replaces the pre-2026-06-12
/// <c>services.BuildServiceProvider()</c> + <c>using</c> pattern that ran the
/// populator at registration time — that pattern silently disposed the host's
/// shared <c>ConfigurationManager</c> in <c>WebApplicationBuilder</c> /
/// <c>HostApplicationBuilder</c> hosts, killing every change-token subscription
/// downstream (<c>IFeatureManager</c>, <c>IOptionsMonitor</c>, appsettings.json
/// reloadOnChange, Azure App Configuration refresh). Reported by a consumer.
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/>, not a blocking <c>StartAsync</c>: the registry rows
/// live in tables the migration creates, so populating inline at host startup both raced a
/// non-blocking initializer and stalled every later hosted service behind DB work. A populate
/// failure still stops the host (the runtime's default background-service exception behavior),
/// it just no longer serializes startup. Mirrors <c>TypeDefinitionReconcilerHostedService</c>.
/// </remarks>
internal sealed partial class MessageTypeRegistryReconciliationHostedService(
    ILogger<MessageTypeRegistryReconciliationHostedService> logger,
    IMessageTypeCatalog? catalog = null,
    IMessageTypeRegistryPopulator? populator = null,
    Whizbang.Core.Workers.ISchemaReadyGate? schemaReadyGate = null) : BackgroundService {

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (catalog is null || populator is null) {
      LogSkipped(logger);
      return;
    }

    if (schemaReadyGate is not null) {
      try {
        await schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
    }

    await populator.PopulateAsync(stoppingToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Skipping message type registry reconciliation — no IMessageTypeCatalog registered (did you call services.AddWhizbang() before AddWhizbangPostgres()?)")]
  private static partial void LogSkipped(ILogger logger);
}
