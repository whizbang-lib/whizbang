using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Registers <see cref="IntegrityCheckpointReceptor"/> with <see cref="IReceptorRegistry"/> at startup.
/// <see cref="IntegrityCheckpoint"/> is a framework event defined in <c>Whizbang.Core</c>, and
/// source-generated receptor discovery only sees the consumer's own syntax, so a built-in receptor needs
/// runtime registration to join the dispatch pipeline. Registered at the three default lifecycle stages a
/// receptor without <c>[FireAt]</c> fires at, so the checkpoint reaches it whether it fires in-process or
/// arrives over the inbox. No-ops if no registry is present (schema-only / diagnostic hosts still boot).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointReceptorTests.cs</tests>
internal sealed class IntegrityCheckpointReceptorRegistrar(
    IServiceProvider services,
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityCheckpointReceptor> receptorLogger) : IHostedService {

  public Task StartAsync(CancellationToken cancellationToken) {
    var registry = services.GetService<IReceptorRegistry>();
    if (registry is null) {
      return Task.CompletedTask;
    }
    var receptor = new IntegrityCheckpointReceptor(scopeFactory, receptorLogger);
    registry.Register<IntegrityCheckpoint>(receptor, LifecycleStage.LocalImmediateInline);
    registry.Register<IntegrityCheckpoint>(receptor, LifecycleStage.PreOutboxInline);
    registry.Register<IntegrityCheckpoint>(receptor, LifecycleStage.PostInboxInline);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
