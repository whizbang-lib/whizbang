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
}
