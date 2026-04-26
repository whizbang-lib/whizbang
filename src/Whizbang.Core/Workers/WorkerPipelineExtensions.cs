using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Workers;

/// <summary>
/// DI registration helpers for the Phase C worker pipeline. Invoked automatically by
/// <see cref="ServiceCollectionExtensions.AddWhizbang(IServiceCollection)"/>; consumers
/// don't need to call this directly. Exposed publicly so advanced scenarios can register
/// just the worker pipeline (e.g., to host workers in a separate process).
/// </summary>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public static class WorkerPipelineExtensions {
  /// <summary>
  /// Registers the new work-pump worker pipeline (HeartbeatWorker, ClaimWorker, InboxHandlerWorker,
  /// and the four batched-flush workers + their channel interfaces). Idempotent — calling
  /// multiple times has no additional effect.
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
    // TryAdd so AddWhizbang() (which calls this) is safe to invoke multiple times.
    services.TryAddSingleton<IOutboxCompletionChannel>(sp => _resolveHostedSingleton<OutboxCompletionFlushWorker>(sp));
    services.TryAddSingleton<IPerspectiveCompletionChannel>(sp => _resolveHostedSingleton<PerspectiveCompletionFlushWorker>(sp));
    services.TryAddSingleton<IFailureChannel>(sp => _resolveHostedSingleton<FailureFlushWorker>(sp));
    services.TryAddSingleton<ILeaseRenewalChannel>(sp => _resolveHostedSingleton<LeaseRenewalWorker>(sp));
    services.TryAddSingleton<IInboxHandlerCommitChannel>(sp => _resolveHostedSingleton<InboxHandlerWorker>(sp));

    // NoOp notification listener by default — driver-specific extensions
    // (e.g., AddWhizbangPostgresNotifications) replace it with the real listener.
    services.TryAddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();

    // AddOptions<T>() is idempotent (uses TryAdd internally for IOptions<T>).
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
