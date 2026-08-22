using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Workers;

/// <summary>
/// <para>Graceful shutdown service that deregisters the instance from the work coordination system.
/// Releases all leases (outbox, inbox, perspective events, receptors, active streams),
/// logs shutdown to wh_log, and removes the instance from wh_service_instances.</para>
///
/// <para>Registration order matters: this service MUST be registered BEFORE the workers
/// so that .NET hosting stops it AFTER them (LIFO ordering). This ensures workers
/// finish in-flight work before deregistration releases their leases.</para>
///
/// <para>K8s compatible: Dockerfile uses <c>exec dotnet</c> (PID 1), so SIGTERM triggers
/// <c>IHostedService.StopAsync</c>. Default <c>terminationGracePeriodSeconds</c> is 30s.</para>
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

    // Deliberately NOT `cancellationToken`. StopAsync runs on the shutdown path, where the host's
    // token is already cancelled by the time cleanup executes — forwarding it cancels the
    // deregistration statement mid-flight, abandoning the instance row at exactly the moment we are
    // trying to remove it. Bound the work on its own clock instead, so a wedged store still cannot
    // hold the process past the orchestrator's grace period and earn a hard kill.
    using var deregisterWindow = new CancellationTokenSource(_deregisterTimeout);

    try {
      await using var scope = serviceProvider.CreateAsyncScope();
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();

      await coordinator.DeregisterInstanceAsync(instanceProvider.InstanceId, deregisterWindow.Token);
      sw.Stop();

      LogShutdownComplete(logger, instanceProvider.InstanceId, sw.ElapsedMilliseconds);
    } catch (OperationCanceledException ex) {
      // Never silent: swallowing this without a trace would strand the instance row AND erase the
      // only evidence that it happened, which is strictly worse than the crash this replaces.
      sw.Stop();
      LogShutdownDeregisterAbandoned(logger, instanceProvider.InstanceId, sw.ElapsedMilliseconds, ex);
    } catch (Exception ex) {
      sw.Stop();
      LogShutdownFailed(logger, instanceProvider.InstanceId, sw.ElapsedMilliseconds, ex);
      // Don't rethrow — a failed deregistration must not turn a graceful stop into a crash exit.
    }
  }

  /// <summary>
  /// Independent budget for deregistration, since the caller's token is already cancelled here.
  /// </summary>
  private static readonly TimeSpan _deregisterTimeout = TimeSpan.FromSeconds(5);

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

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Warning,
    Message = "Whizbang shutdown abandoned deregistration for instance {InstanceId} after {ElapsedMs}ms — the store did not "
      + "complete within the deregistration budget. Shutdown continues; the instance row remains in the fleet table until "
      + "stale-instance cleanup reaps it, so fleet membership may over-count this instance until then"
  )]
  private static partial void LogShutdownDeregisterAbandoned(ILogger logger, Guid instanceId, long elapsedMs, Exception exception);
}
