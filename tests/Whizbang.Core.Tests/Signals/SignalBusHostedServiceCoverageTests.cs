using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Covers <see cref="SignalBusHostedService.StopAsync"/>, which no existing test calls at all —
/// <c>SignalBusHostingTests</c> and <c>SignalBusProbeTests</c> only ever start the hosted service.
/// Two distinct cancellation shapes live in <c>StopAsync</c>/<c>_probeLoopAsync</c>: a cancellation
/// observed WHILE a probe is actively in flight (must not be misreported as a probe failure), and
/// the host's own stop-wait itself being canceled while the probe loop has not yet unwound (must
/// not propagate and break host shutdown).
/// </summary>
public class SignalBusHostedServiceCoverageTests {

  /// <summary>Blocks inside PublishAsync until the token IT WAS CALLED WITH cancels — the shape
  /// of a probe actively in flight when the host asks the service to stop.</summary>
  private sealed class BlocksUntilCanceledTransport : ISignalTransport {
    public TaskCompletionSource Publishing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      Publishing.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); // throws when the probe's own token cancels
    }
  }

  /// <summary>Ignores the probe's own cancellation and hangs until explicitly <see cref="Release"/>d
  /// — stands in for a slow/unresponsive transport whose PublishAsync has not yet unwound when the
  /// HOST's own stop-wait token fires. Release-able (rather than truly infinite) so the test leaves
  /// no background task running after it completes.</summary>
  private sealed class HangsUntilReleasedTransport : ISignalTransport, IDisposable {
    private readonly CancellationTokenSource _release = new();
    public TaskCompletionSource Publishing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      Publishing.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, _release.Token).ConfigureAwait(false);
    }

    public void Release() => _release.Cancel();

    /// <summary>Releases the hang and disposes the token source (CA1001).</summary>
    public void Dispose() => _release.Dispose();
  }

  /// <summary>
  /// If the catch this hits were merged into the generic catch below it, a stop landing while a
  /// probe was actively in flight would call <c>MarkProbeResult(false, ...)</c> and report the
  /// wire route DEGRADED — a false alarm fired by the act of shutting down, not by anything
  /// actually wrong with the route.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StopAsync_CancelsWhileProbeInFlight_DoesNotReportAProbeFailureAsync(CancellationToken testToken) {
    var transport = new BlocksUntilCanceledTransport();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    services.AddSingleton<ISignalTransport>(transport);
    await using var provider = services.BuildServiceProvider();
    var hosted = provider.GetServices<IHostedService>().Single();
    var state = provider.GetRequiredService<SignalBusLivenessState>();

    await hosted.StartAsync(CancellationToken.None);
    await transport.Publishing.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken); // the probe is now in flight

    await hosted.StopAsync(CancellationToken.None);

    await Assert.That(state.WireRouteVerified).IsNull()
      .Because("the cancellation happened before any probe verdict landed — StopAsync racing the "
             + "loop's own in-flight probe must leave the verdict exactly where it was, not stamp "
             + "a spurious Degraded/failed reading onto a route nobody actually found broken");
  }

  /// <summary>
  /// If this catch were removed, a slow-to-cancel (or misbehaving) transport still unwinding when
  /// the host's OWN stop-wait token fires would let the OperationCanceledException escape
  /// StopAsync entirely — breaking the generic host shutdown sequence instead of completing it,
  /// for every other hosted service sharing that shutdown.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task StopAsync_HostStopTokenAlreadyCanceled_CompletesWithoutThrowingAsync(CancellationToken testToken) {
    var transport = new HangsUntilReleasedTransport();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangSignalBus();
    services.AddSingleton<ISignalTransport>(transport);
    await using var provider = services.BuildServiceProvider();
    var hosted = provider.GetServices<IHostedService>().Single();

    await hosted.StartAsync(CancellationToken.None);
    await transport.Publishing.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken); // probe loop is now stuck forever

    // The host's own stop-wait token is already canceled by the time StopAsync is asked to run —
    // the probe loop (stuck in a transport that never observes cancellation) has not unwound.
    await hosted.StopAsync(new CancellationToken(canceled: true));
    // No exception reaching here IS the assertion: StopAsync must swallow this rather than
    // propagate it into the host's shutdown sequence.

    // Clean up: let the still-running probe loop unwind (it observes _stopCts, canceled above,
    // once the transport itself stops hanging) instead of leaving a background task running.
    transport.Release();
  }
}
