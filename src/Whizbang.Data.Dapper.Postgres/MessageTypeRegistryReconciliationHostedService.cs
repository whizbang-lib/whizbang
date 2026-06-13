using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Runs <see cref="IMessageTypeRegistryPopulator.PopulateAsync"/> once at host
/// startup to reconcile the persisted message-type registry against the
/// compile-time <see cref="IMessageTypeCatalog"/>. Replaces the pre-2026-06-12
/// <c>services.BuildServiceProvider()</c> + <c>using</c> pattern that ran the
/// populator at registration time — that pattern silently disposed the host's
/// shared <c>ConfigurationManager</c> in <c>WebApplicationBuilder</c> /
/// <c>HostApplicationBuilder</c> hosts, killing every change-token subscription
/// downstream (<c>IFeatureManager</c>, <c>IOptionsMonitor</c>, appsettings.json
/// reloadOnChange, Azure App Configuration refresh). Reported by Bijan Camp
/// (a consumer/a consumer application).
/// </summary>
internal sealed partial class MessageTypeRegistryReconciliationHostedService(
    ILogger<MessageTypeRegistryReconciliationHostedService> logger,
    IMessageTypeCatalog? catalog = null,
    IMessageTypeRegistryPopulator? populator = null) : IHostedService {

  public async Task StartAsync(CancellationToken cancellationToken) {
    if (catalog is null || populator is null) {
      LogSkipped(logger);
      return;
    }
    await populator.PopulateAsync(cancellationToken).ConfigureAwait(false);
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Skipping message type registry reconciliation — no IMessageTypeCatalog registered (did you call services.AddWhizbang() before AddWhizbangPostgres()?)")]
  private static partial void LogSkipped(ILogger logger);
}
