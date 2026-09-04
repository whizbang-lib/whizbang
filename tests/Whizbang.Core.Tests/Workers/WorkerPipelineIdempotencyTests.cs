using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para><c>AddWhizbangWorkers</c> documents itself as idempotent, and <c>AddWhizbang()</c> calls it
/// internally, so a consumer that also calls it explicitly (the framework's own error messages
/// suggest exactly that) composes it twice. Every registration in it must therefore be a no-op the
/// second time.</para>
/// <para>The regression this fences out: the startup steps were registered with plain
/// <c>AddSingleton</c>, so a second call produced six <see cref="IStartupStep"/> descriptors, the
/// order resolver refused the duplicate names, and the host stopped during startup with the cause
/// buried in a background-service failure (issue #621).</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
[Category("Shard2")]
public sealed class WorkerPipelineIdempotencyTests {
  private static ServiceCollection _compose(int times) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    for (var i = 0; i < times; i++) {
      services.AddWhizbangWorkers();
    }
    return services;
  }

  [Test]
  public async Task AddWhizbangWorkers_CalledTwice_RegistersEachStartupStepOnceAsync() {
    var services = _compose(times: 2);

    var steps = services.Where(sd => sd.ServiceType == typeof(IStartupStep)).ToList();

    await Assert.That(steps.Count).IsEqualTo(3)
      .Because("Assess, Migrate and Rewrite are the framework's three steps; a second call must not "
             + "add three more — that is the duplicate-name refusal that stopped hosts at startup");
  }

  [Test]
  public async Task AddWhizbangWorkers_CalledTwice_RegistersEachStartupStepObserverOnceAsync() {
    var once = _compose(times: 1).Count(sd => sd.ServiceType == typeof(IStartupStepObserver));
    var twice = _compose(times: 2).Count(sd => sd.ServiceType == typeof(IStartupStepObserver));

    await Assert.That(twice).IsEqualTo(once)
      .Because("the pipeline state, logging and metrics observers would each fire twice per step "
             + "otherwise — doubled log lines and doubled metrics with nothing reporting why");
  }

  [Test]
  public async Task AddWhizbangWorkers_CalledTwice_TheOrderResolverAcceptsTheStepSetAsync() {
    var services = _compose(times: 2);
    await using var provider = services.BuildServiceProvider();

    var steps = provider.GetServices<IStartupStep>().ToList();
    var resolved = StartupStepOrderResolver.Resolve([.. steps.Select(s => s.Descriptor)]);

    await Assert.That(resolved.Count).IsEqualTo(3)
      .Because("this is the call StartupPipelineRunner makes on boot; if it throws here it throws "
             + "inside a BackgroundService and the default StopHost behavior takes the host down");
  }

  [Test]
  public async Task AddWhizbangWorkers_CalledTwice_HostsEachWorkerOnceAsync() {
    var once = _compose(times: 1).Count(sd => sd.ServiceType == typeof(IHostedService));
    var twice = _compose(times: 2).Count(sd => sd.ServiceType == typeof(IHostedService));

    await Assert.That(twice).IsEqualTo(once)
      .Because("AddHostedService<T> dedupes by implementation type; this pins that no hosted "
             + "registration in the pipeline bypasses it");
  }

  /// <summary>The generic host supplies this; AddWhizbangWorkers does not and should not.</summary>
  private sealed class StubHostLifetime : IHostApplicationLifetime {
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
  }

  /// <summary>
  /// Contracts a transport consumer or storage driver contributes. A worker that cannot be
  /// built without one of these is waiting on configuration the application supplies, not on a
  /// registration AddWhizbangWorkers forgot.
  /// </summary>
  private static readonly string[] _transportSuppliedContracts = [
    "IWorkChannelWriter",
    "IInboxChannelWriter",
    "JsonSerializerOptions"
  ];

  [Test]
  public async Task AddWhizbangWorkers_EveryHostedServiceItRegistersCanBeConstructedAsync() {
    // The registrations are factories -- `sp => sp.GetRequiredService<HeartbeatWorker>()` -- so
    // they run only when something actually constructs the hosted service. Counting descriptors,
    // which is all the tests above do, never executes them. A worker whose constructor gains a
    // dependency nobody registered therefore stays green here and throws during host startup
    // instead, where it surfaces as a background-service failure with the real cause buried.
    //
    // Composed the way an application composes it: AddWhizbang for the core services, plus the
    // logging and lifetime the generic host always provides. What remains unbuildable after that
    // is the transport-dependent set, and the assertion pins exactly that boundary -- a worker
    // that newly starts needing a transport fails here and has to be a deliberate decision.
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddLogging();
    services.AddSingleton<IHostApplicationLifetime>(new StubHostLifetime());
    services.AddWhizbang();
    services.AddWhizbangWorkers();

    await using var provider = services.BuildServiceProvider();

    var hosted = services.Where(sd => sd.ServiceType == typeof(IHostedService)).ToList();
    var unexpected = new List<string>();

    foreach (var descriptor in hosted) {
      var name = descriptor.ImplementationType?.Name
        ?? descriptor.ImplementationInstance?.GetType().Name
        ?? "factory";

      try {
        // AddHostedService<T>() registers T only under IHostedService, so T itself is not
        // resolvable by name. ActivatorUtilities builds it from the same container, which is
        // the property under test: are this worker's dependencies actually registered.
        var instance = descriptor.ImplementationInstance
          ?? (descriptor.ImplementationFactory is not null
                ? descriptor.ImplementationFactory(provider)
                : ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));

        if (instance is null) {
          unexpected.Add($"{name} resolved to null");
        }
      } catch (Exception ex) {
        if (!_transportSuppliedContracts.Any(c => ex.Message.Contains(c, StringComparison.Ordinal))) {
          unexpected.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
        }
      }
    }

    await Assert.That(hosted).IsNotEmpty()
      .Because("the assertion below is vacuous if nothing was registered to construct");
    await Assert.That(unexpected).IsEmpty()
      .Because("a worker that cannot be built from a configured container is missing a "
             + "registration; it fails at host startup, not here");
  }

}
