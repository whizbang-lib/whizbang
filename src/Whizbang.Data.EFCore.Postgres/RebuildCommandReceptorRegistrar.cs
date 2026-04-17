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
/// Registered at <see cref="LifecycleStage.PostInboxInline"/> — the stage where distributed
/// commands are invoked after arriving through the inbox. This matches the default-stage
/// behavior documented on <see cref="IReceptorInvoker"/>.
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
internal sealed class RebuildCommandReceptorRegistrar(
    IReceptorRegistry registry,
    IServiceScopeFactory scopeFactory,
    ILogger<RebuildPerspectiveCommandReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var receptor = new RebuildPerspectiveCommandReceptor(scopeFactory, receptorLogger);
    registry.Register<RebuildPerspectiveCommand>(receptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
