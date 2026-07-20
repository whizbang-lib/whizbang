using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Registers <see cref="ScheduledStreamCloseReceptor"/> with <see cref="IReceptorRegistry"/> at startup.
/// <see cref="ScheduledStreamClose"/> is a framework command defined in <c>Whizbang.Core</c>, and
/// source-generated receptor discovery only sees the consumer's own syntax, so a built-in receptor needs
/// runtime registration to join the dispatch pipeline. Registered at the three default lifecycle stages a
/// receptor without <c>[FireAt]</c> fires at (<see cref="LifecycleStage.LocalImmediateInline"/> /
/// <see cref="LifecycleStage.PreOutboxInline"/> / <see cref="LifecycleStage.PostInboxInline"/>), so the
/// occurrence reaches it whether it fires in-process or arrives over the inbox. No-ops if no registry is
/// present (schema-only / diagnostic hosts still boot).
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
internal sealed class ScheduledStreamCloseReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledStreamCloseReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null) {
      return Task.CompletedTask;
    }
    var receptor = new ScheduledStreamCloseReceptor(scopeFactory, receptorLogger);
    registry.Register<ScheduledStreamClose>(receptor, LifecycleStage.LocalImmediateInline);
    registry.Register<ScheduledStreamClose>(receptor, LifecycleStage.PreOutboxInline);
    registry.Register<ScheduledStreamClose>(receptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
