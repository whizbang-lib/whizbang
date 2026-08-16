using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Routing;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Increment 3, first barrier: the pipeline actually runs in a host, and <c>Migrate</c> is a real
/// step whose completion signal is <see cref="ISchemaReadyGate"/> — the gate demoted from THE
/// global barrier to one step's completion, exactly as the proposal specifies. From here, workers
/// adopt <c>WaitForAsync("Migrate")</c> one declared dependency at a time.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupPipelineHosting.cs</code-under-test>
[Category("Startup")]
public class StartupPipelineWiringTests {

  private static ServiceProvider _build() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheStateAsBothInterfacesAndAsAnObserverAsync() {
    await using var sp = _build();

    var state = sp.GetRequiredService<IStartupPipelineState>();
    var observers = sp.GetServices<IStartupStepObserver>().ToList();

    await Assert.That(observers.Any(o => ReferenceEquals(o, state))).IsTrue()
      .Because("the state derives its answers from the same notifications every observer gets — "
             + "registering it as an observer IS the wiring");
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheBuiltInLoggingAndMetricsObserversAsync() {
    await using var sp = _build();

    var observers = sp.GetServices<IStartupStepObserver>().ToList();

    await Assert.That(observers.Any(o => o is LoggingStartupStepObserver)).IsTrue();
    await Assert.That(observers.Any(o => o is MetricsStartupStepObserver)).IsTrue();
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersMigrateAsADeclaredStepAsync() {
    await using var sp = _build();

    var steps = sp.GetServices<IStartupStep>().ToList();

    await Assert.That(steps.Any(s => s.Descriptor.Name == FrameworkStartupSteps.MIGRATE)).IsTrue()
      .Because("Migrate is the first framework behaviour to become a declared step");
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersRewriteAsAPostReadyMaintainerStepAsync() {
    await using var sp = _build();

    var rewrite = sp.GetServices<IStartupStep>()
      .FirstOrDefault(s => s.Descriptor.Name == FrameworkStartupSteps.REWRITE);

    await Assert.That(rewrite).IsNotNull();
    await Assert.That(rewrite!.Descriptor.Blocking).IsFalse()
      .Because("deliberately unbounded work must never gate Ready");
    await Assert.That(rewrite.Descriptor.RequiredCapability).IsEqualTo(StartupDuties.MAINTAINER)
      .Because("one instance rewrites; exclusivity follows the required capability");
  }

  [Test]
  public async Task PipelineWorker_MigrateCompletesExactlyWhenTheGateOpensAsync() {
    await using var sp = _build();
    var gate = sp.GetRequiredService<ISchemaReadyGate>();
    var state = sp.GetRequiredService<IStartupPipelineState>();
    var worker = sp.GetRequiredService<StartupPipelineWorker>();

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // While the gate is closed, Migrate must be underway but not complete.
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (state.StatusOf(FrameworkStartupSteps.MIGRATE) != StartupStepStatus.Running
        && DateTime.UtcNow < deadline) {
      await Task.Delay(10);
    }
    await Assert.That(state.StatusOf(FrameworkStartupSteps.MIGRATE)).IsEqualTo(StartupStepStatus.Running)
      .Because("the step is awaiting the gate — running, not finished, not skipped");
    await Assert.That(state.IsComplete).IsFalse();

    gate.MarkReady();
    await state.WaitForAsync(FrameworkStartupSteps.MIGRATE, cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(state.StatusOf(FrameworkStartupSteps.MIGRATE)).IsEqualTo(StartupStepStatus.Completed)
      .Because("the gate opening IS Migrate's completion signal");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task AddWhizbangWorkers_RegistersTheReadyCompositeOnTheLifecycleSeamAsync() {
    await using var sp = _build();

    var signal = sp.GetRequiredService<IStartupReadySignal>();
    // Resolve the service directly rather than enumerating IHostedService — materializing every
    // hosted registration drags in workers whose dependencies the storage driver supplies.
    var readyService = sp.GetRequiredService<StartupReadyService>();

    await Assert.That(signal.IsReady).IsFalse()
      .Because("the composite must start unset — it fires only when the blocking steps drain");
    await Assert.That(readyService is Microsoft.Extensions.Hosting.IHostedLifecycleService).IsTrue()
      .Because("Ready lives on IHostedLifecycleService.StartedAsync — the seam that runs after "
             + "every StartAsync has returned, which nothing had ever claimed");
  }

  [Test]
  public async Task AddTransportConsumer_RegistersTheSameWorkerInstanceAsAReadinessContributorAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<Whizbang.Core.Transports.ITransport>(new Whizbang.Core.Transports.InProcessTransport());
    services.AddSingleton(new System.Text.Json.JsonSerializerOptions());
    services.AddWhizbang()
      .WithRouting(r => {
        r.Inbox.UseSharedTopic("inbox");
        r.Outbox.UseSharedTopic("inbox");
      })
      .AddTransportConsumer();
    await using var sp = services.BuildServiceProvider();

    var worker = sp.GetRequiredService<TransportConsumerWorker>();
    var contributors = sp.GetServices<IStartupReadinessContributor>().ToList();

    await Assert.That(contributors.Any(c => ReferenceEquals(c, worker))).IsTrue()
      .Because("the SAME worker instance the host runs must be the contributor Ready waits on — "
             + "a second instance would answer for subscriptions nobody made; the hosted "
             + "registration forwards to this same singleton");
  }
}
