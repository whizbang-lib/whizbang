using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
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
}
