// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="IntegrityCheckpointReceptorRegistrar"/>'s registry guard: a host
/// with no registry, and one with only the framework's null-default registry, register nothing; a
/// real registry receives the receptor at the three default lifecycle stages.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptorRegistrar.cs</code-under-test>
[Category("Shard1")]
public class CheckpointReceptorRegistrarBranchTests {

  [Test]
  public async Task StartAsync_EachRegistryShape_RegistersOnlyIntoARealRegistryAsync() {
    var none = await _startAsync(registry: null);
    var nullDefault = new NullDefaultRegistry();
    await _startAsync(nullDefault);
    var real = new RecordingRegistry();
    await _startAsync(real);

    await Assert.That(none).IsTrue()
      .Because("a host with no registry boots without registering anything");
    await Assert.That(nullDefault.Registered).IsEmpty()
      .Because("the null-default registry stands for 'none wired' and must not receive the receptor");
    await Assert.That(real.Registered).IsEquivalentTo([
      LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline,
    ]);
  }

  private static async Task<bool> _startAsync(IReceptorRegistry? registry) {
    var services = new ServiceCollection();
    if (registry is not null) {
      services.AddSingleton(registry);
    }
    await using var sp = services.BuildServiceProvider();
    var registrar = new IntegrityCheckpointReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);
    await registrar.StartAsync(CancellationToken.None);
    return true;
  }

  private class RecordingRegistry : IReceptorRegistry {
    public List<LifecycleStage> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add(stage);
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add(stage);
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class NullDefaultRegistry : RecordingRegistry, INullDefault;
}
