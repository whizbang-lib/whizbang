using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the logger for the receptor it constructs and hands it straight over, so the category names the type that actually writes the entries.")]
internal sealed class ScheduledStreamCloseReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<ScheduledStreamCloseReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    // INullDefault, not null: every framework dependency resolves now, so "no registry" arrives as
    // the turnkey default rather than as an absent service. Registering into it throws by design,
    // which would take the whole host down on a service that simply declares no receptors.
    if (registry is null or INullDefault) {
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
