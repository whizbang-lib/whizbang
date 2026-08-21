#pragma warning disable CA1707 // Test method names can contain underscores

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Turnkey lock for the commit→perspective-visible chain: plain <c>AddWhizbang()</c> plus the
/// Postgres driver's notification registration must pre-register EVERY component the chain
/// needs — hosted and resolvable — with nothing left for the consumer to opt into. The
/// regression class this fences out is the silent worker-never-starts signature: a hosted
/// registration quietly missing means no errors, no logs, just work that accumulates
/// unprocessed. Everything remains reconfigurable (options callbacks, configuration binding),
/// but standing a service up must require no additional wiring.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
public class TurnkeyVisibilityChainRegistrationTests {

  private static ServiceCollection _composeTurnkey() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddWhizbang();
    services.AddWhizbangPostgresNotifications();
    return services;
  }

  [Test]
  public async Task Turnkey_HostsEveryWorkerInTheVisibilityChainAsync() {
    var services = _composeTurnkey();

    // Hosted singletons registered via AddHostedService(sp => sp.GetRequiredService<T>())
    // resolve as factories; the singleton-of-T registration is the assertable descriptor.
    foreach (var worker in new[] {
        typeof(ClaimWorker),
        typeof(PerspectiveWorker),
        typeof(HeartbeatWorker),
        typeof(InboxDispatchWorker),
        typeof(OutboxPublishWorker),
        typeof(BackupTickCoordinator),
        typeof(PgSharedNotifyConnection),
        typeof(PgWorkNotificationListener),
        typeof(PgCommitOrderStamperWorker) }) {
      await Assert.That(services.Any(sd => sd.ServiceType == worker)).IsTrue()
        .Because($"{worker.Name} is part of the commit→perspective-visible chain and must be pre-registered turnkey");
    }

    // The hosted-service list must actually include the chain's drivers — a singleton that is
    // never hosted is the exact silent worker-never-starts signature.
    var hostedCount = services.Count(sd => sd.ServiceType == typeof(IHostedService));
    await Assert.That(hostedCount).IsGreaterThanOrEqualTo(10)
      .Because("the worker pipeline + notification stack host their components unconditionally");
  }

  [Test]
  public async Task Turnkey_NotifySignalingGate_IsTheSharedConnectionAsync() {
    var services = _composeTurnkey();
    await using var provider = services.BuildServiceProvider();

    // The gate is what relaxes ClaimWorker onto doorbell-driven wake in production; it must
    // resolve turnkey AND be the shared notify connection (one probe, one truth).
    var gate = provider.GetService<INotifySignalingGate>();
    var shared = provider.GetService<ISharedNotifyConnection>();

    await Assert.That(gate).IsNotNull()
      .Because("without the gate registered, ClaimWorker silently falls back to tight polling — a cadence production does not run");
    await Assert.That(ReferenceEquals(gate, shared)).IsTrue()
      .Because("gate and shared connection must be the same singleton so availability probes and LISTEN share one connection");
  }

  [Test]
  public async Task Turnkey_PerspectiveWorker_HostedExactlyOnce_EvenWithGeneratedRegistrationAsync() {
    // The core pipeline hosts PerspectiveWorker unconditionally AND the generated
    // AddPerspectiveRunners() still emits its own TryAdd + AddHostedService for back-compat.
    // Both must collapse to ONE hosted entry: a second IHostedService descriptor resolving the
    // same singleton means StartAsync runs twice on one BackgroundService — two execute loops,
    // double claim/fetch churn, and exhausted connection pools in tightly-pooled hosts.
    var services = _composeTurnkey();
    services.AddPerspectiveRunners();

    var hostedPerspectiveWorkers = services.Count(sd =>
      sd.ServiceType == typeof(IHostedService) &&
      (sd.ImplementationType == typeof(PerspectiveWorker)
       || sd.ImplementationFactory?.Method.ReturnType == typeof(PerspectiveWorker)));

    await Assert.That(hostedPerspectiveWorkers).IsEqualTo(1)
      .Because("core registration + generated AddPerspectiveRunners() must dedupe to exactly one hosted PerspectiveWorker — "
             + "a second IHostedService descriptor for the same singleton means StartAsync runs twice on one BackgroundService "
             + "(two execute loops, double claim/fetch churn). Resolving the full hosted set needs a real host environment, so "
             + "the descriptor count is the assertable seam.");
  }

  [Test]
  public async Task Turnkey_ClaimWorker_IsTypeRegistered_SoOptionalDependenciesFlowAsync() {
    var services = _composeTurnkey();

    // ClaimWorker must be registered BY TYPE (constructor injection), not by a hand-rolled
    // factory: its gate/listener/channel dependencies are optional parameters, and a factory
    // that omits them compiles fine while silently degrading the production cadence contract.
    var descriptor = services.Single(sd => sd.ServiceType == typeof(ClaimWorker));
    await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(ClaimWorker))
      .Because("type registration lets DI flow every registered optional dependency (signaling gate, listener, channels) into ClaimWorker");
  }
}
