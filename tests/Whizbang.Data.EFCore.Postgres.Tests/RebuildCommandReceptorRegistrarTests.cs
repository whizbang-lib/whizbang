using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The runtime registration that lets <c>RebuildPerspectiveCommand</c> reach its receptor.
/// </summary>
/// <remarks>
/// <para>Receptor discovery is source-generated from the consumer's own syntax, so a receptor
/// shipped inside this driver assembly is invisible to it and has to be registered at startup
/// instead. Nothing else puts this receptor in the dispatch pipeline, and a rebuild command that
/// finds no receptor does not fail loudly — it is simply dispatched into silence.</para>
///
/// <para>Which stages it registers at is the part worth pinning down. A receptor without
/// <c>[FireAt]</c> fires at three stages, and a service dispatching the command to itself takes
/// only the LocalImmediateInline path. Registering just PostInboxInline would still pass any test
/// that sends the command over a transport while quietly breaking the in-process case.</para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RebuildCommandReceptorRegistrar.cs</code-under-test>
[Category("Core")]
[Category("Shard1")]
public class RebuildCommandReceptorRegistrarTests {

  private static RebuildCommandReceptorRegistrar _registrar(IReceptorRegistry? registry) {
    var services = new ServiceCollection();
    services.AddLogging();
    if (registry is not null) {
      services.AddSingleton(registry);
    }
    var provider = services.BuildServiceProvider();
    return new RebuildCommandReceptorRegistrar(
      provider,
      provider.GetRequiredService<IServiceScopeFactory>(),
      provider.GetRequiredService<ILogger<RebuildPerspectiveCommandReceptor>>());
  }

  [Test]
  public async Task RegistersTheReceptorAtEveryStageAnUnattributedReceptorFiresAtAsync() {
    // A service that dispatches this command to itself only ever reaches LocalImmediateInline.
    // Registering the distributed stages alone would leave in-process rebuilds dispatched into
    // silence — no receptor, no error, no rebuild.
    var registry = new _recordingRegistry();

    await _registrar(registry).StartAsync(CancellationToken.None);

    await Assert.That(registry.StagesFor(typeof(RebuildPerspectiveCommand)))
      .IsEquivalentTo(new[] {
        LifecycleStage.LocalImmediateInline,
        LifecycleStage.PreOutboxInline,
        LifecycleStage.PostInboxInline,
      })
      .Because("these are the three stages a receptor without [FireAt] fires at, and the command "
             + "has to reach its receptor whether the dispatch is local or distributed");
  }

  [Test]
  public async Task WithoutAReceptorRegistry_TheHostStillStartsAsync() {
    // Schema-only tools and diagnostic hosts wire this driver without a dispatcher. Startup is
    // the wrong place to insist on one: a hosted service that throws here takes the whole process
    // down over a receptor that host was never going to dispatch to.
    var registrar = _registrar(registry: null);

    await Assert.That(async () => await registrar.StartAsync(CancellationToken.None))
      .ThrowsNothing()
      .Because("a host with no dispatcher has nothing to register against, and that is a normal "
             + "configuration rather than a misconfiguration to fail on");
  }

  [Test]
  public async Task StoppingRegistersNothingAndCompletesAsync() {
    // Registration is startup-only; the receptor lives as long as the registry does. Stop having
    // work to do would mean the registrar held state it never took.
    var registry = new _recordingRegistry();
    var registrar = _registrar(registry);
    await registrar.StartAsync(CancellationToken.None);
    var afterStart = registry.Registrations.Count;

    await registrar.StopAsync(CancellationToken.None);

    await Assert.That(registry.Registrations.Count).IsEqualTo(afterStart)
      .Because("shutdown does not unregister — the registry outlives the registrar and tearing "
             + "the receptor out on stop would be a different lifetime than the one documented");
  }

  /// <summary>Records what was registered and where, which is the whole contract here.</summary>
  private sealed class _recordingRegistry : IReceptorRegistry {
    public List<(Type Message, LifecycleStage Stage)> Registrations { get; } = [];

    public IReadOnlyList<LifecycleStage> StagesFor(Type message) =>
      [.. Registrations.Where(r => r.Message == message).Select(r => r.Stage)];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage)
        where TMessage : IMessage => Registrations.Add((typeof(TMessage), stage));

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage => Registrations.Add((typeof(TMessage), stage));

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage)
        where TMessage : IMessage => false;

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage => false;
  }
}
