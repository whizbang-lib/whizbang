using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Signals;

/// <summary>
/// Hosts the <see cref="SignalBus"/>: starts every DI-registered <see cref="ISignalTransport"/>
/// and <see cref="ISignalSource"/> with the host, so wire doorbells reach subscribers without any
/// component calling <see cref="SignalBus.StartAsync"/> by hand. Before this service existed the
/// bus was registered but never started in a real host — every NOTIFY doorbell was silently
/// dropped and all work pumps ran at poll cadence (issue #505).
/// </summary>
/// <remarks>
/// After the bus starts, a background loop verifies the wire route <em>functionally</em>: each
/// transport publishes a loopback <see cref="SignalBusProbeSignal"/> targeted at this instance and
/// must deliver it back through the full route (routing maps → wire → typed dispatch) within
/// <see cref="SignalBusOptions.ProbeTimeoutMilliseconds"/>. A socket-level self-test cannot catch a
/// dead routing layer — the exact false-healthy that hid #505. The probe re-runs every
/// <see cref="SignalBusOptions.ReProbeIntervalMilliseconds"/> so a listener that dies mid-run is
/// caught even when idle. Verdicts land in <see cref="SignalBusLivenessState"/>, which the
/// <c>signal-bus</c> health component reports.
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusHostingTests.cs:AddWhizbangSignalBus_HostStartAlone_StartsRegisteredTransportsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusProbeTests.cs:HostedStart_ProbeVerifiesWireRoute_ViaInMemoryAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusProbeTests.cs:HostedStart_DeadTransport_ProbeMarksWireRouteFailedAsync</tests>
public sealed partial class SignalBusHostedService(
  SignalBus bus,
  SignalBusLivenessState liveness,
  IEnumerable<ISignalTransport> transports,
  ILogger<SignalBusHostedService> logger,
  IServiceInstanceProvider instanceProvider,
  IOptions<SignalBusOptions>? options = null,
  TimeProvider? timeProvider = null
) : IHostedService, IDisposable {
  private readonly SignalBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
  private readonly SignalBusLivenessState _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
  private readonly ISignalTransport[] _transports = (transports ?? throw new ArgumentNullException(nameof(transports))).ToArray();
  private readonly ILogger<SignalBusHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly SignalBusOptions _options = options?.Value ?? new SignalBusOptions();
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider;
  private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

  private readonly CancellationTokenSource _stopCts = new();
  private Task? _probeLoop;

  /// <inheritdoc />
  public async Task StartAsync(CancellationToken cancellationToken) {
    await _bus.StartAsync(cancellationToken).ConfigureAwait(false);
    LogBusStarted(_logger, _transports.Length);
    _probeLoop = Task.Run(() => _probeLoopAsync(_stopCts.Token), CancellationToken.None);
  }

  /// <inheritdoc />
  public async Task StopAsync(CancellationToken cancellationToken) {
    await _stopCts.CancelAsync().ConfigureAwait(false);
    if (_probeLoop is not null) {
      try {
        await _probeLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        // Host stop raced the loop's own cancellation — both mean "stop"; nothing to surface.
      }
    }
  }

  /// <inheritdoc />
  public void Dispose() => _stopCts.Dispose();

  private async Task _probeLoopAsync(CancellationToken stopping) {
    while (!stopping.IsCancellationRequested) {
      try {
        var failed = await _probeAllTransportsAsync(stopping).ConfigureAwait(false);
        _liveness.MarkProbeResult(failed is null, _time.GetUtcNow(), failed);
        if (failed is null) {
          LogProbeVerified(_logger, _transports.Length);
        } else {
          LogProbeFailed(_logger, failed, _options.ProbeTimeoutMilliseconds);
        }
      } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
        return;
      } catch (Exception ex) {
        // A probe that cannot even run is an unverified route, never a silent pass — the exact
        // silent-failure class this service exists to surface. Mark failed and keep the loop alive.
        _liveness.MarkProbeResult(false, _time.GetUtcNow(), failedTransport: ex.GetType().Name);
        LogProbeError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromMilliseconds(_options.ReProbeIntervalMilliseconds), _time, stopping).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
    }
  }

  /// <summary>
  /// Probe each transport individually — publishing through ONE transport at a time and awaiting
  /// the typed loopback — so the in-memory transport's instant loopback can never vouch for a dead
  /// wire transport. Returns the first failing transport's type name, or null when all delivered.
  /// </summary>
  private async Task<string?> _probeAllTransportsAsync(CancellationToken stopping) {
    // With an instance identity, target our own channel — in a fleet, only this instance must ring.
    // Without one (single-process/test hosts) fall back to broadcast, which is still a self-loopback.
    var instanceId = _instanceProvider.InstanceId;
    var target = instanceId == Guid.Empty ? SignalTarget.Broadcast : SignalTarget.Instance(instanceId);
    foreach (var transport in _transports) {
      var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var subscription = _bus.Subscribe<SignalBusProbeSignal>(_ => {
        delivered.TrySetResult();
        return ValueTask.CompletedTask;
      });
      await transport.PublishAsync(new SignalBusProbeSignal(), target, stopping).ConfigureAwait(false);
      try {
        await delivered.Task
          .WaitAsync(TimeSpan.FromMilliseconds(_options.ProbeTimeoutMilliseconds), _time, stopping)
          .ConfigureAwait(false);
      } catch (TimeoutException) {
        return transport.GetType().Name;
      }
    }
    return null;
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Signal bus started: {TransportCount} transport(s) and all pull sources are wired to the host lifecycle")]
  private static partial void LogBusStarted(ILogger logger, int transportCount);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Signal bus wire route verified: all {TransportCount} transport(s) delivered the loopback probe")]
  private static partial void LogProbeVerified(ILogger logger, int transportCount);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Signal bus wire route FAILED its self-test: transport {Transport} did not deliver the loopback probe within {TimeoutMs}ms. " +
              "Doorbells are NOT reaching this instance — work pumps are running on polling fallback and every hop pays the poll interval. " +
              "Check the direct (non-pooled) notify connection string, the shared notify connection's LISTEN subscriptions, and that the transport was started")]
  private static partial void LogProbeFailed(ILogger logger, string transport, int timeoutMs);

  [LoggerMessage(Level = LogLevel.Error,
    Message = "Signal bus wire-route probe threw instead of completing — the route is UNVERIFIED and marked degraded; the probe loop stays alive and will retry")]
  private static partial void LogProbeError(ILogger logger, Exception exception);
}
