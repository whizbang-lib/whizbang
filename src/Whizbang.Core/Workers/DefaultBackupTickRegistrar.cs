using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// IHostedService that registers Whizbang's built-in backup ticks against
/// <see cref="IBackupTickRegistry"/> at startup. Slice 4c.2 of zero-idle-polling.
/// </summary>
/// <remarks>
/// <para>
/// Default-registered ticks:
/// </para>
/// <list type="bullet">
/// <item><description><c>scheduled-retry</c> — calls
/// <see cref="IWorkCoordinator.NotifyScheduledRetryDueAsync(CancellationToken)"/>
/// to wake the per-instance NOTIFY channels for any rows whose
/// <c>scheduled_for</c> has elapsed. Replaces the standalone
/// the (now-deleted) <c>ScheduledRetryWorker</c>'s 10 s tick — the
/// coordinator now owns the cadence.</description></item>
/// </list>
/// <para>
/// Additional ticks (e.g. <c>stamper-backstop</c>,
/// <c>orphan-discovery</c>) ship in subsequent slices and are registered by
/// the driver-specific service-collection extensions when their dependencies
/// are wired.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
public sealed partial class DefaultBackupTickRegistrar(
  IBackupTickRegistry registry,
  IServiceScopeFactory scopeFactory,
  ISchemaReadyGate schemaReadyGate,
  ILogger<DefaultBackupTickRegistrar> logger
) : IHostedService {
  private readonly IBackupTickRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly ISchemaReadyGate _schemaReadyGate = schemaReadyGate ?? throw new ArgumentNullException(nameof(schemaReadyGate));
  private readonly ILogger<DefaultBackupTickRegistrar> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    _registry.Register(
      name: "scheduled-retry",
      tick: _scheduledRetryTickAsync,
      isEnabled: () => true);
    LogRegistered(_logger, "scheduled-retry");
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  private async Task _scheduledRetryTickAsync(CancellationToken ct) {
    if (!_schemaReadyGate.IsReady) {
      return;
    }
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var streams = await coordinator.NotifyScheduledRetryDueAsync(ct).ConfigureAwait(false);
    if (streams > 0) {
      LogWoke(_logger, streams);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "DefaultBackupTickRegistrar registered '{Name}' tick")]
  static partial void LogRegistered(ILogger logger, string name);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "scheduled-retry tick woke {StreamsWoken} stream(s)")]
  static partial void LogWoke(ILogger logger, int streamsWoken);
}
