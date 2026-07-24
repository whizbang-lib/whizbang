using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers the transport managed-resource health wiring: the <see cref="ITransport.CheckConnectivityAsync"/>
/// default (returns <c>IsInitialized</c>) and the smart registration in <c>AddWhizbangWorkers</c> — a real
/// probe over a registered transport, or an assumed-healthy placeholder when there is no transport.
/// </summary>
public class TransportHealthWiringTests {

  /// <summary>Minimal <see cref="ITransport"/> that only implements what the health path touches.</summary>
  private sealed class FakeTransport(bool initialized) : ITransport {
    public bool IsInitialized => initialized;
    public TransportCapabilities Capabilities => throw new NotImplementedException();
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
      string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination, TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope,
      TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull => throw new NotImplementedException();
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task DefaultCheckConnectivity_ReturnsIsInitializedAsync(bool initialized) {
    ITransport transport = new FakeTransport(initialized);
    await Assert.That(await transport.CheckConnectivityAsync()).IsEqualTo(initialized);
  }

  private static async Task<ComponentState> _transportStateAsync(ITransport? transport, LifecyclePhase phase) {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();
    if (transport is not null) {
      services.AddSingleton(transport);
    }
    await using var provider = services.BuildServiceProvider();
    var lifecycle = provider.GetRequiredService<IWhizbangLifecycleState>();
    await lifecycle.AdvanceToAsync(phase, CancellationToken.None);
    var source = provider.GetServices<IWhizbangHealthSource>().Single(s => s.Component == "transport");
    return (await source.ReportAsync(CancellationToken.None)).State;
  }

  [Test]
  public async Task RegisteredTransport_Connected_WhileRunning_OperationalAsync()
    => await Assert.That(await _transportStateAsync(new FakeTransport(initialized: true), LifecyclePhase.Running))
      .IsEqualTo(ComponentState.Operational);

  [Test]
  public async Task RegisteredTransport_Disconnected_WhileRunning_FaultedAsync()
    => await Assert.That(await _transportStateAsync(new FakeTransport(initialized: false), LifecyclePhase.Running))
      .IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task RegisteredTransport_Disconnected_WhileMigrating_PausedByDesignAsync()
    // A disconnected transport during a migration is by-design (not probed), not a fault.
    => await Assert.That(await _transportStateAsync(new FakeTransport(initialized: false), LifecyclePhase.Migrating))
      .IsEqualTo(ComponentState.PausedByDesign);

  [Test]
  public async Task NoTransport_AssumedHealthy_WhileRunning_OperationalAsync()
    => await Assert.That(await _transportStateAsync(transport: null, LifecyclePhase.Running))
      .IsEqualTo(ComponentState.Operational);
}
