using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Workers;

/// <summary>
/// DI registration helpers for the Phase C worker pipeline. Registers all 8 new workers
/// + their channel interfaces. Call from a host's <c>ConfigureServices</c> alongside the
/// driver-specific <c>AddWhizbangEFCorePostgres</c> / <c>AddWhizbangDapperPostgres</c>
/// registration so <c>IWorkCoordinator</c> is resolvable.
/// Phase C of work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public static class WorkerPipelineExtensions {
  /// <summary>
  /// Registers the new work-pump worker pipeline (HeartbeatWorker, ClaimWorker, InboxHandlerWorker,
  /// and the four batched-flush workers + their channel interfaces).
  /// Existing legacy <see cref="WorkCoordinatorPublisherWorker"/> is NOT registered here — caller
  /// chooses whether to keep the legacy poller alongside (mixed mode for migration) or drop it.
  /// </summary>
  /// <param name="services">DI service collection.</param>
  /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
  public static IServiceCollection AddWhizbangWorkers(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    // Hosted services — each runs as BackgroundService.
    services.AddHostedService<HeartbeatWorker>();
    services.AddHostedService<ClaimWorker>();
    services.AddHostedService<OutboxCompletionFlushWorker>();
    services.AddHostedService<PerspectiveCompletionFlushWorker>();
    services.AddHostedService<FailureFlushWorker>();
    services.AddHostedService<LeaseRenewalWorker>();
    services.AddHostedService<InboxHandlerWorker>();

    // Channel interfaces — singletons resolved to the same hosted-service instance.
    // Producers (publisher worker, perspective process worker, inbox dispatch) get the
    // channel surface for enqueuing work items.
    services.AddSingleton<IOutboxCompletionChannel>(sp => _resolveHostedSingleton<OutboxCompletionFlushWorker>(sp));
    services.AddSingleton<IPerspectiveCompletionChannel>(sp => _resolveHostedSingleton<PerspectiveCompletionFlushWorker>(sp));
    services.AddSingleton<IFailureChannel>(sp => _resolveHostedSingleton<FailureFlushWorker>(sp));
    services.AddSingleton<ILeaseRenewalChannel>(sp => _resolveHostedSingleton<LeaseRenewalWorker>(sp));
    services.AddSingleton<IInboxHandlerCommitChannel>(sp => _resolveHostedSingleton<InboxHandlerWorker>(sp));

    // NoOp notification listener by default — driver-specific extensions
    // (e.g., AddWhizbangPostgresNotifications) can replace with the real listener.
    services.TryAddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();

    // Default options — callers override via .Configure<...> after this call.
    services.AddOptions<HeartbeatWorkerOptions>();
    services.AddOptions<ClaimWorkerOptions>();
    services.AddOptions<OutboxCompletionFlushWorkerOptions>();
    services.AddOptions<PerspectiveCompletionFlushWorkerOptions>();
    services.AddOptions<FailureFlushWorkerOptions>();
    services.AddOptions<LeaseRenewalWorkerOptions>();
    services.AddOptions<InboxHandlerWorkerOptions>();

    return services;
  }

  /// <summary>
  /// AddHostedService registers as IHostedService; we want the same instance to satisfy
  /// the channel-surface interfaces too. This finds the hosted instance by type.
  /// </summary>
  private static T _resolveHostedSingleton<T>(IServiceProvider sp) where T : class {
    foreach (var hosted in sp.GetServices<IHostedService>()) {
      if (hosted is T match) {
        return match;
      }
    }
    throw new InvalidOperationException(
      $"{typeof(T).Name} is not registered as a hosted service. " +
      "Did you call AddWhizbangWorkers() before resolving the channel interface?");
  }
}
