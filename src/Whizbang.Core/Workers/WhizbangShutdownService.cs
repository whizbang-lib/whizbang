using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// Graceful shutdown service that deregisters the instance from the work coordination system.
/// Releases all leases (outbox, inbox, perspective events, receptors, active streams),
/// logs shutdown to wh_log, and removes the instance from wh_service_instances.
///
/// Registration order matters: this service MUST be registered BEFORE the workers
/// so that .NET hosting stops it AFTER them (LIFO ordering). This ensures workers
/// finish in-flight work before deregistration releases their leases.
///
/// K8s compatible: Dockerfile uses <c>exec dotnet</c> (PID 1), so SIGTERM triggers
/// <c>IHostedService.StopAsync</c>. Default <c>terminationGracePeriodSeconds</c> is 30s.
/// </summary>
public sealed partial class WhizbangShutdownService(
  IServiceProvider serviceProvider,
  IServiceInstanceProvider instanceProvider,
  ILogger<WhizbangShutdownService> logger
) : IHostedService {
  /// <summary>No-op on startup.</summary>
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  /// <summary>
  /// Deregisters the instance on graceful shutdown.
  /// Fires after all BackgroundService workers have stopped (LIFO ordering).
  /// </summary>
  public async Task StopAsync(CancellationToken cancellationToken) {
    var sw = Stopwatch.StartNew();
    LogShutdownStarting(logger, instanceProvider.InstanceId, instanceProvider.ServiceName, instanceProvider.HostName);

    try {
      await using var scope = serviceProvider.CreateAsyncScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      await coordinator.DeregisterInstanceAsync(instanceProvider.InstanceId, cancellationToken);
      sw.Stop();

      LogShutdownComplete(logger, instanceProvider.InstanceId, sw.ElapsedMilliseconds);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      sw.Stop();
      LogShutdownFailed(logger, instanceProvider.InstanceId, sw.ElapsedMilliseconds, ex);
      // Don't rethrow — stale cleanup will handle it if deregistration fails
    }
  }

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Information,
    Message = "Whizbang shutdown starting — deregistering instance {InstanceId} ({ServiceName}@{HostName})"
  )]
  private static partial void LogShutdownStarting(ILogger logger, Guid instanceId, string serviceName, string hostName);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Information,
    Message = "Whizbang shutdown complete — instance {InstanceId} deregistered in {ElapsedMs}ms, all leases released"
  )]
  private static partial void LogShutdownComplete(ILogger logger, Guid instanceId, long elapsedMs);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Warning,
    Message = "Whizbang shutdown deregistration failed for instance {InstanceId} after {ElapsedMs}ms — stale cleanup will handle it"
  )]
  private static partial void LogShutdownFailed(ILogger logger, Guid instanceId, long elapsedMs, Exception exception);
}
