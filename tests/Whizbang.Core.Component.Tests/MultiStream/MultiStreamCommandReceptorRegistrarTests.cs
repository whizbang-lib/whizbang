// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.MultiStream;

namespace Whizbang.Core.Component.Tests.MultiStream;

/// <summary>
/// The hosted registrar that registers every multi-stream command's intake receptor when the host starts (#1358).
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#registration</docs>
[Category("Component")]
[Category("MultiStream")]
public class MultiStreamCommandReceptorRegistrarTests {
  private static ServiceProvider _provider(Action<IServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    services.AddMultiStreamCommandHandler<MultiStreamCommandWorkerLoopTests.LoopProbeCommand, MultiStreamCommandWorkerLoopTests.LoopHandler>();
    configure?.Invoke(services);
    return services.BuildServiceProvider();
  }

  private static MultiStreamCommandReceptorRegistrar _registrar(ServiceProvider provider) =>
    provider.GetServices<IHostedService>().OfType<MultiStreamCommandReceptorRegistrar>().Single();

  [Test]
  public async Task StartAsync_RegistersTheIntakeAtTheDefaultReceptorStagesAsync() {
    var registry = new CapturingReceptorRegistry();
    await using var provider = _provider(s => s.AddSingleton<IReceptorRegistry>(registry));
    var registrar = _registrar(provider);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Stages).IsEquivalentTo([
      LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline])
      .Because("the command is recorded whether it is dispatched within the service or arrives from the transport");
    await Assert.That(registry.Receptors.All(r => r is MultiStreamCommandIntake<MultiStreamCommandWorkerLoopTests.LoopProbeCommand>)).IsTrue();
    await registrar.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task StartAsync_WithTheNullRegistry_RegistersNothingAndStartsAsync() {
    await using var provider = _provider(s => s.AddSingleton<IReceptorRegistry>(NullReceptorRegistry.Instance));

    await Assert.That(async () => await _registrar(provider).StartAsync(CancellationToken.None)).ThrowsNothing()
      .Because("a host with no receptor registry has nothing to register into and must still start");
  }

  [Test]
  public async Task StartAsync_WithNoRegistry_RegistersNothingAndStartsAsync() {
    await using var provider = _provider();

    await Assert.That(async () => await _registrar(provider).StartAsync(CancellationToken.None)).ThrowsNothing();
  }

  private sealed class CapturingReceptorRegistry : IReceptorRegistry {
    public List<LifecycleStage> Stages { get; } = [];

    public List<object> Receptors { get; } = [];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
      Stages.Add(stage);
      Receptors.Add(receptor);
    }

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new InvalidOperationException("The intake is a void receptor.");

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }
}
