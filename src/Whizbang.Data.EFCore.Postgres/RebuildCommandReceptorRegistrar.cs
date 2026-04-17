using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Hosted service that registers <see cref="RebuildPerspectiveCommandReceptor"/> with
/// <see cref="IReceptorRegistry"/> at application startup. The command is a system-level
/// command defined in <c>Whizbang.Core</c>; source-generated receptor discovery only sees
/// the consumer's own syntax, so a built-in receptor shipped from this driver assembly
/// needs runtime registration to participate in the dispatch pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Registered at the three default lifecycle stages that receptors without <c>[FireAt]</c>
/// fire at: <see cref="LifecycleStage.LocalImmediateInline"/> (same-process dispatch),
/// <see cref="LifecycleStage.PreOutboxInline"/> (distributed sender), and
/// <see cref="LifecycleStage.PostInboxInline"/> (distributed receiver). This matches the
/// compile-time behavior documented on <see cref="IReceptorInvoker"/> so that a caller
/// running <c>IDispatcher.SendAsync(new RebuildPerspectiveCommand(...))</c> hits the
/// receptor regardless of whether the dispatch is local or distributed.
/// </para>
/// <para>
/// The invoker's same-service dedup (<c>ReceptorInvoker</c>) already prevents double-firing
/// when a service dispatches the command to itself — no extra dedup is needed here.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
internal sealed class RebuildCommandReceptorRegistrar(
    IReceptorRegistry registry,
    IServiceScopeFactory scopeFactory,
    ILogger<RebuildPerspectiveCommandReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var receptor = new RebuildPerspectiveCommandReceptor(scopeFactory, receptorLogger);
    // Match the compile-time default-stage behavior of receptors without [FireAt]. Registering
    // only PostInboxInline misses the common case where a service dispatches the command to
    // itself via IDispatcher.SendAsync (that path fires LocalImmediateInline only).
    registry.Register<RebuildPerspectiveCommand>(receptor, LifecycleStage.LocalImmediateInline);
    registry.Register<RebuildPerspectiveCommand>(receptor, LifecycleStage.PreOutboxInline);
    registry.Register<RebuildPerspectiveCommand>(receptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
