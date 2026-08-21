using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Locks the hosted-start contract for the signal bus: registering the bus in DI must be enough
/// for the HOST to start every transport and pull source — no component may depend on a manual
/// <see cref="SignalBus.StartAsync"/> call. This is the regression lock for the production gap
/// where every deployed host silently dropped all wire doorbells because nothing ever started
/// the bus (issue #505): tests started it by hand, masking the missing wiring.
/// </summary>
public class SignalBusHostingTests {
  private sealed class RecordingTransport : ISignalTransport {
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
      Started.TrySetResult();
      return Task.CompletedTask;
    }

    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
  }

  private sealed class RecordingPullSource : ISignalSource {
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) {
      Started.TrySetResult();
      return Task.CompletedTask;
    }
  }

  private static async Task _startAllHostedServicesAsync(IServiceProvider provider) {
    foreach (var hosted in provider.GetServices<IHostedService>()) {
      await hosted.StartAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task AddWhizbangSignalBus_HostStartAlone_StartsRegisteredTransportsAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    var transport = new RecordingTransport();
    services.AddSingleton<ISignalTransport>(transport);

    await using var provider = services.BuildServiceProvider();
    await _startAllHostedServicesAsync(provider);

    await Assert.That(transport.Started.Task.IsCompleted)
      .IsTrue()
      .Because("host start alone must start every DI-registered signal transport — no manual SignalBus.StartAsync");
  }

  [Test]
  public async Task AddWhizbangSignalBus_HostStartAlone_StartsRegisteredPullSourcesAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    var source = new RecordingPullSource();
    services.AddSingleton<ISignalSource>(source);

    await using var provider = services.BuildServiceProvider();
    await _startAllHostedServicesAsync(provider);

    await Assert.That(source.Started.Task.IsCompleted)
      .IsTrue()
      .Because("pull sources (the reconciliation backstops) are started by the same hosted start");
  }

  [Test]
  public async Task AddWhizbangSignalBus_CalledTwice_HostedStartIsIdempotentAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    services.AddWhizbangSignalBus();

    await using var provider = services.BuildServiceProvider();
    var hostedCount = provider.GetServices<IHostedService>().Count();

    await Assert.That(hostedCount)
      .IsEqualTo(1)
      .Because("AddWhizbangSignalBus is documented idempotent — a second call must not double-start the bus");
  }
}
