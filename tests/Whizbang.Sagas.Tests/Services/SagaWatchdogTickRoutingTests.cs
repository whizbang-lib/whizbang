using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests.Services;

/// <summary>
/// A saga's completion watchdog has a receiver however the saga was declared.
/// </summary>
/// <remarks>
/// <para>
/// <c>BaseSagaService</c> arms a watchdog tick for every saga it starts, unconditionally. The
/// receiver for that tick was only ever generated into classes marked <c>[Saga]</c>, so a saga
/// service written by hand — a plain subclass registered with the container — armed a tick nothing
/// would ever handle. The tick was delivered on time, found no receptor, and was discarded without a
/// trace, which is how a saga stranded on one lost item stayed stranded with its safety net silently
/// absent.
/// </para>
/// <para>
/// The framework now owns a receiver that routes each tick, by saga name, to the saga service that
/// armed it. It registers only on the receiving side of the pipeline: a tick is armed for a future
/// time, and a receptor on the sending side would run at arming time and re-arm straight away.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas/Services/SagaWatchdogTickRouter.cs</code-under-test>
/// <code-under-test>src/Whizbang.Sagas/Services/SagaWatchdogTickRouterRegistrar.cs</code-under-test>
/// <code-under-test>src/Whizbang.Sagas/SagaServiceCollectionExtensions.cs</code-under-test>
[Category("Sagas")]
public class SagaWatchdogTickRoutingTests {

  private static SagaCompletionWatchdogTickEvent _tick(string sagaName) => new() {
    StreamId = Guid.NewGuid(),
    SagaName = sagaName,
    EntityId = Guid.NewGuid(),
  };

  [Test]
  public async Task Router_DeliversATickToTheSagaThatArmedItAsync() {
    var importer = new RecordingParticipant("Import");
    var mapper = new RecordingParticipant("Mapping");
    var services = new ServiceCollection();
    services.AddScoped<ISagaWatchdogParticipant>(_ => importer);
    services.AddScoped<ISagaWatchdogParticipant>(_ => mapper);
    await using var sp = services.BuildServiceProvider();
    var router = new SagaWatchdogTickRouter(sp.GetRequiredService<IServiceScopeFactory>());

    var tick = _tick("Mapping");
    await router.HandleAsync(tick, CancellationToken.None);

    await Assert.That(mapper.Received).IsEquivalentTo([tick]);
    await Assert.That(importer.Received).IsEmpty()
      .Because("routing is by saga name; every other saga must see nothing");
  }

  [Test]
  public async Task Router_WithNoMatchingSaga_DoesNothingAsync() {
    var importer = new RecordingParticipant("Import");
    var services = new ServiceCollection();
    services.AddScoped<ISagaWatchdogParticipant>(_ => importer);
    await using var sp = services.BuildServiceProvider();
    var router = new SagaWatchdogTickRouter(sp.GetRequiredService<IServiceScopeFactory>());

    await router.HandleAsync(_tick("DeclaredWithTheSagaAttribute"), CancellationToken.None);

    await Assert.That(importer.Received).IsEmpty()
      .Because("a [Saga]-declared saga has its own generated receiver; routing it here too would re-arm twice");
  }

  [Test]
  public async Task Registrar_RegistersTheRouterOnTheReceivingSideOnlyAsync() {
    var registry = new RecordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new SagaWatchdogTickRouterRegistrar(sp, sp.GetRequiredService<IServiceScopeFactory>());

    await registrar.StartAsync(CancellationToken.None);
    await registrar.StopAsync(CancellationToken.None);

    await Assert.That(registry.Stages).IsEquivalentTo([LifecycleStage.PostInboxInline])
      .Because("a tick is armed for a future time; a sending-side receptor would fire at arming and re-arm at once");
    await Assert.That(registry.Receptors.Single()).IsTypeOf<SagaWatchdogTickRouter>();
  }

  [Test]
  public async Task Registrar_WhenTheRegistryIsTheNullDefault_StartsWithoutThrowingAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(NullReceptorRegistry.Instance);
    await using var sp = services.BuildServiceProvider();
    var registrar = new SagaWatchdogTickRouterRegistrar(sp, sp.GetRequiredService<IServiceScopeFactory>());

    await registrar.StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task Registrar_WithNoRegistry_StartsWithoutThrowingAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var registrar = new SagaWatchdogTickRouterRegistrar(sp, sp.GetRequiredService<IServiceScopeFactory>());

    await registrar.StartAsync(CancellationToken.None);
  }

  [Test]
  public async Task AddWhizbangSagas_RegistersTheRouterRegistrarAsync() {
    var services = new ServiceCollection();

    services.AddWhizbangSagas();

    await Assert.That(services.Any(d => d.ServiceType == typeof(IHostedService)
                                     && d.ImplementationType == typeof(SagaWatchdogTickRouterRegistrar)))
      .IsTrue();
  }

  [Test]
  public async Task AddSagaService_RegistersTheServiceAndItsWatchdogParticipationAsOneInstanceAsync() {
    var services = new ServiceCollection();

    services.AddSagaService<RecordingParticipantService>();
    await using var sp = services.BuildServiceProvider();
    await using var scope = sp.CreateAsyncScope();

    var concrete = scope.ServiceProvider.GetRequiredService<RecordingParticipantService>();
    var participant = scope.ServiceProvider.GetServices<ISagaWatchdogParticipant>().Single();

    await Assert.That(participant).IsSameReferenceAs(concrete)
      .Because("the tick must reach the same scoped service the rest of the scope is using");
  }

  private sealed class RecordingParticipant(string sagaName) : ISagaWatchdogParticipant {
    public List<SagaCompletionWatchdogTickEvent> Received { get; } = [];
    public string SagaName => sagaName;

    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(
        SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken) {
      Received.Add(tick);
      return Task.FromResult(WatchdogTickOutcome.ReArmed);
    }
  }

  /// <summary>A participant with a parameterless constructor, as a container-built saga service would be.</summary>
  internal sealed class RecordingParticipantService : ISagaWatchdogParticipant {
    public string SagaName => "Recording";

    public Task<WatchdogTickOutcome> TryRecoverViaWatchdogTickAsync(
        SagaCompletionWatchdogTickEvent tick, CancellationToken cancellationToken) =>
      Task.FromResult(WatchdogTickOutcome.Recovered);
  }

  private sealed class RecordingRegistry : IReceptorRegistry {
    public List<LifecycleStage> Stages { get; } = [];
    public List<object> Receptors { get; } = [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
      Stages.Add(stage);
      Receptors.Add(receptor);
    }

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage => throw new NotSupportedException("the watchdog router has no response");

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      throw new NotSupportedException("never unregistered");

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage => throw new NotSupportedException("never unregistered");

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
  }
}
