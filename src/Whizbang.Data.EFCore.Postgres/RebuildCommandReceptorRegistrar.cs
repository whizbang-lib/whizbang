using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = "The registrar never logs. It receives the logger for the receptor it constructs and hands it straight over, so the category names the type that actually writes the entries.")]
internal sealed class RebuildCommandReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<RebuildPerspectiveCommandReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    // Receptor registry is optional — hosts that wire the Postgres driver without a dispatcher
    // (minimal diagnostic hosts, schema-only tools) should still boot.
    var registry = services.GetService<IReceptorRegistry>();
    // INullDefault, not null: every framework dependency resolves now, so "no registry" arrives as
    // the turnkey default rather than as an absent service. Registering into it throws by design,
    // which would take the whole host down on a service that simply declares no receptors.
    if (registry is null or INullDefault) {
      return Task.CompletedTask;
    }

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
