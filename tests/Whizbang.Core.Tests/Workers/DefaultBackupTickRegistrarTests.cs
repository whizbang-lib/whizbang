using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 4c.2 of zero-idle-polling — locks the
/// <see cref="DefaultBackupTickRegistrar"/> contract.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>StartAsync registers exactly one tick named <c>"scheduled-retry"</c>.</description></item>
/// <item><description>The registered tick's IsEnabled() returns <c>true</c> by default.</description></item>
/// <item><description>StopAsync is a no-op — registrations live for the process lifetime, the coordinator owns its lifecycle.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
public class DefaultBackupTickRegistrarTests {

  private sealed class FakeSchemaReadyGate(bool isReady) : ISchemaReadyGate {
    public bool IsReady { get; } = isReady;
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) =>
      IsReady ? Task.CompletedTask : Task.Delay(Timeout.Infinite, cancellationToken);
    public void MarkReady() { }
  }

  [Test]
  public async Task StartAsync_RegistersScheduledRetryTickAsync() {
    var registry = new BackupTickRegistry();
    var services = new ServiceCollection();
    using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var registrar = new DefaultBackupTickRegistrar(
      registry,
      scopeFactory,
      new FakeSchemaReadyGate(isReady: true),
      NullLogger<DefaultBackupTickRegistrar>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registrations).Count().IsEqualTo(1)
      .Because("Exactly one tick — scheduled-retry — ships in Slice 4c.2; additional ticks (stamper-backstop, orphan-discovery) come from driver-specific extensions.");
    await Assert.That(registry.Registrations[0].Name).IsEqualTo("scheduled-retry");
  }

  [Test]
  public async Task RegisteredTick_IsEnabled_ReturnsTrueByDefaultAsync() {
    var registry = new BackupTickRegistry();
    var services = new ServiceCollection();
    using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var registrar = new DefaultBackupTickRegistrar(
      registry,
      scopeFactory,
      new FakeSchemaReadyGate(isReady: true),
      NullLogger<DefaultBackupTickRegistrar>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    var registration = registry.Registrations[0];
    await Assert.That(registration.IsEnabled()).IsTrue()
      .Because("No-op killswitch by default — operators who want to disable can register a wrapped predicate at the consumer side.");
  }

  [Test]
  public async Task StopAsync_LeavesRegistrationsIntactAsync() {
    var registry = new BackupTickRegistry();
    var services = new ServiceCollection();
    using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var registrar = new DefaultBackupTickRegistrar(
      registry,
      scopeFactory,
      new FakeSchemaReadyGate(isReady: true),
      NullLogger<DefaultBackupTickRegistrar>.Instance);

    await registrar.StartAsync(CancellationToken.None);
    await registrar.StopAsync(CancellationToken.None);

    await Assert.That(registry.Registrations).Count().IsEqualTo(1)
      .Because("StopAsync is intentionally a no-op — the coordinator owns its own shutdown lifecycle. Unregistering here would create a race between the registrar's stop and the coordinator's final tick.");
  }

  /// <summary>Counts scheduled-retry wakes and reports how many streams each one woke.</summary>
  private sealed class _countingCoordinator(int streamsWoken) : NoOpWorkCoordinator, IWorkCoordinator {
    public int Calls { get; private set; }
    public Task<int> NotifyScheduledRetryDueAsync(CancellationToken cancellationToken = default) {
      Calls++;
      return Task.FromResult(streamsWoken);
    }
  }

  private static (DefaultBackupTickRegistrar Registrar, BackupTickRegistry Registry, ServiceProvider Provider)
      _build(bool schemaReady, IWorkCoordinator coordinator) {
    var registry = new BackupTickRegistry();
    var services = new ServiceCollection();
    services.AddScoped(_ => coordinator);
    var provider = services.BuildServiceProvider();
    var registrar = new DefaultBackupTickRegistrar(
      registry,
      provider.GetRequiredService<IServiceScopeFactory>(),
      new FakeSchemaReadyGate(schemaReady),
      NullLogger<DefaultBackupTickRegistrar>.Instance);
    return (registrar, registry, provider);
  }

  [Test]
  public async Task RegisteredTick_BeforeTheSchemaIsReady_AsksTheCoordinatorNothingAsync() {
    // The tick is registered at startup and the coordinator begins polling it immediately, so it
    // can fire while migrations are still running. What it calls reads the schedule tables — a
    // query against tables that do not exist yet throws inside a backup tick, on a timer, once
    // per polling cycle, for as long as the migration takes.
    var coordinator = new _countingCoordinator(streamsWoken: 3);
    var (registrar, registry, provider) = _build(schemaReady: false, coordinator);
    await using var _ = provider;

    await registrar.StartAsync(CancellationToken.None);
    await registry.Registrations[0].Tick(CancellationToken.None);

    await Assert.That(coordinator.Calls).IsEqualTo(0)
      .Because("the gate is the only thing standing between a backup tick and a table the "
             + "migration has not created yet");
  }

  [Test]
  public async Task RegisteredTick_OnceReady_WakesTheStreamsWhoseRetriesAreDueAsync() {
    // This tick is the backstop for scheduled retries: without it a retry whose time has come
    // waits for some other signal to wake its stream. Registering it and never calling through is
    // indistinguishable, from the outside, from registering a tick that does nothing at all.
    var coordinator = new _countingCoordinator(streamsWoken: 3);
    var (registrar, registry, provider) = _build(schemaReady: true, coordinator);
    await using var _ = provider;

    await registrar.StartAsync(CancellationToken.None);
    await registry.Registrations[0].Tick(CancellationToken.None);

    await Assert.That(coordinator.Calls).IsEqualTo(1)
      .Because("the registered delegate has to reach the coordinator, or the retry backstop is a "
             + "name in a registry with nothing behind it");
  }

  [Test]
  public async Task RegisteredTick_WhenNothingWasDue_StillCompletesQuietlyAsync() {
    // The common case by a wide margin: the tick runs on every polling cycle and usually finds
    // nothing. It has to stay silent then — a log line per cycle per process buries the one that
    // reports real work, and this fires for the life of the service.
    var coordinator = new _countingCoordinator(streamsWoken: 0);
    var (registrar, registry, provider) = _build(schemaReady: true, coordinator);
    await using var _ = provider;

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(async () => await registry.Registrations[0].Tick(CancellationToken.None))
      .ThrowsNothing()
      .Because("finding nothing due is the normal outcome, not an error condition");
    await Assert.That(coordinator.Calls).IsEqualTo(1)
      .Because("it still asked — the quiet path is 'asked and got zero', not 'did not ask'");
  }
}
