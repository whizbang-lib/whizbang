using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Covers the hosted service that wires the integrity manifest receptors into the receptor
/// registry at startup.
/// </summary>
/// <remarks>
/// The registrar is optional infrastructure: a host without a receptor registry — a
/// migration tool, a CLI, anything that references the data package but runs no dispatch —
/// must start cleanly rather than fail on a missing dependency.
/// </remarks>
[Category("Shard1")]
public class IntegrityManifestReceptorRegistrarTests {

  private sealed class CountingRegistry : IReceptorRegistry {
    public List<(Type Message, LifecycleStage Stage)> Registrations { get; } = [];

    /// <summary>Receptors this registry has been asked to remove. Expected to stay empty.</summary>
    public List<(Type Message, LifecycleStage Stage)> Unregistrations { get; } = [];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage)
      => Array.Empty<ReceptorInfo>();

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage)
        where TMessage : IMessage
      => Registrations.Add((typeof(TMessage), stage));

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage
      => Registrations.Add((typeof(TMessage), stage));

    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage)
        where TMessage : IMessage {
      Unregistrations.Add((typeof(TMessage), stage));
      return false;
    }

    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage)
        where TMessage : IMessage {
      Unregistrations.Add((typeof(TMessage), stage));
      return false;
    }
  }

  /// <summary>
  /// An <see cref="IServiceProvider"/> that records what was asked of it. Resolving an absent
  /// optional dependency leaves no other trace, so this is how a test tells "looked and found
  /// nothing" apart from "never looked".
  /// </summary>
  private sealed class ProbingServiceProvider(IServiceProvider inner) : IServiceProvider {
    public List<Type> Requested { get; } = [];

    public object? GetService(Type serviceType) {
      Requested.Add(serviceType);
      return inner.GetService(serviceType);
    }
  }

  private static IntegrityManifestReceptorRegistrar _registrar(IServiceProvider services)
    => new(
      services,
      services.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestRequestReceptor>.Instance,
      NullLogger<IntegrityManifestReceptor>.Instance);

  [Test]
  public async Task StartAsync_WithoutAReceptorRegistry_StartsCleanlyAsync() {
    // A host that references the data package but runs no dispatch has no registry. It
    // must start rather than fail — the receptors simply have nowhere to attach.
    var services = new ProbingServiceProvider(new ServiceCollection().BuildServiceProvider());

    await _registrar(services).StartAsync(CancellationToken.None);

    // The probe is what keeps "started cleanly" from being vacuous: a StartAsync that returned
    // before ever looking for the registry would also not throw, and would silently stop
    // registering the receptors on hosts that DO have one.
    await Assert.That(services.Requested).Contains(typeof(IReceptorRegistry))
      .Because("the registry must be resolved optionally, and the absence handled, not skipped");
  }

  [Test]
  public async Task StartAsync_WithARegistry_RegistersBothReceptorsAsync() {
    var registry = new CountingRegistry();
    var services = new ServiceCollection()
      .AddSingleton<IReceptorRegistry>(registry)
      .BuildServiceProvider();

    await _registrar(services).StartAsync(CancellationToken.None);

    await Assert.That(registry.Registrations).IsNotEmpty();
  }

  [Test]
  public async Task StartAsync_RegistersTheManifestReceptorAtEveryInlineStageAsync() {
    // The manifest arrives by three different routes — locally, through the outbox, and
    // through the inbox — and has to be handled on all of them or convergence depends on
    // which path a peer happened to use.
    var registry = new CountingRegistry();
    var services = new ServiceCollection()
      .AddSingleton<IReceptorRegistry>(registry)
      .BuildServiceProvider();

    await _registrar(services).StartAsync(CancellationToken.None);

    var manifestStages = registry.Registrations
      .Where(r => r.Message == typeof(IntegrityManifest))
      .Select(r => r.Stage)
      .ToList();

    await Assert.That(manifestStages).Contains(LifecycleStage.LocalImmediateInline);
    await Assert.That(manifestStages).Contains(LifecycleStage.PreOutboxInline);
    await Assert.That(manifestStages).Contains(LifecycleStage.PostInboxInline);
  }

  [Test]
  public async Task StopAsync_LeavesTheReceptorsRegisteredAsync() {
    // Stopping the registrar is not a deregistration. The registry outlives it, and a stop that
    // pulled the manifest receptors back out would silently drop convergence traffic on any host
    // that stops and restarts its hosted services.
    var registry = new CountingRegistry();
    var services = new ServiceCollection()
      .AddSingleton<IReceptorRegistry>(registry)
      .BuildServiceProvider();
    var registrar = _registrar(services);
    await registrar.StartAsync(CancellationToken.None);
    var registeredAtStart = registry.Registrations.Count;

    await registrar.StopAsync(CancellationToken.None);

    await Assert.That(registry.Unregistrations).IsEmpty();
    await Assert.That(registry.Registrations.Count).IsEqualTo(registeredAtStart);
  }
}
