using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.Offloads;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers the offload managed-resource health wiring: the <see cref="IMessageBodyStore.CheckConnectivityAsync"/>
/// default (returns <see langword="true"/>) and the smart registration in <c>AddWhizbangWorkers</c> — a real
/// probe over a registered store, or an assumed-healthy placeholder when no offload is configured.
/// </summary>
public class OffloadHealthWiringTests {

  /// <summary>Store that uses the default connectivity (always reachable).</summary>
  private sealed class DefaultStore : IMessageBodyStore {
    public string ProviderName => "fake";
    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType,
      MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim,
      MessageBodyDownloadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(MessageBodyClaim claim,
      MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  }

  /// <summary>Store that reports unreachable — proves the source uses the probe, not assumed-healthy.</summary>
  private sealed class UnreachableStore : IMessageBodyStore {
    public string ProviderName => "fake";
    public ValueTask<bool> CheckConnectivityAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public Task<MessageBodyClaim> UploadAsync(ReadOnlyMemory<byte> body, string contentType,
      MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<ReadOnlyMemory<byte>> DownloadAsync(MessageBodyClaim claim,
      MessageBodyDownloadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task DeleteAsync(MessageBodyClaim claim,
      MessageBodyDeleteOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
  }

  [Test]
  public async Task DefaultCheckConnectivity_ReturnsTrueAsync() {
    IMessageBodyStore store = new DefaultStore();
    await Assert.That(await store.CheckConnectivityAsync()).IsTrue();
  }

  private static async Task<ComponentState> _offloadStateAsync(IMessageBodyStore? store, LifecyclePhase phase) {
    var services = new ServiceCollection();
    services.AddWhizbangWorkers();
    if (store is not null) {
      services.AddSingleton(store);
    }
    await using var provider = services.BuildServiceProvider();
    var lifecycle = provider.GetRequiredService<IWhizbangLifecycleState>();
    await lifecycle.AdvanceToAsync(phase, CancellationToken.None);
    var source = provider.GetServices<IWhizbangHealthSource>().Single(s => s.Component == "offload");
    return (await source.ReportAsync(CancellationToken.None)).State;
  }

  [Test]
  public async Task RegisteredStore_Reachable_WhileRunning_OperationalAsync()
    => await Assert.That(await _offloadStateAsync(new DefaultStore(), LifecyclePhase.Running))
      .IsEqualTo(ComponentState.Operational);

  [Test]
  public async Task RegisteredStore_Unreachable_WhileRunning_FaultedAsync()
    => await Assert.That(await _offloadStateAsync(new UnreachableStore(), LifecyclePhase.Running))
      .IsEqualTo(ComponentState.Faulted);

  [Test]
  public async Task NoStore_AssumedHealthy_WhileRunning_OperationalAsync()
    => await Assert.That(await _offloadStateAsync(store: null, LifecyclePhase.Running))
      .IsEqualTo(ComponentState.Operational);
}
