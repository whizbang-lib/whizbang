using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Signals;

/// <summary>
/// Hosts the <see cref="SignalBus"/>: starts every DI-registered <see cref="ISignalTransport"/>
/// and <see cref="ISignalSource"/> with the host, so wire doorbells reach subscribers without any
/// component calling <see cref="SignalBus.StartAsync"/> by hand. Before this service existed the
/// bus was registered but never started in a real host — every NOTIFY doorbell was silently
/// dropped and all work pumps ran at poll cadence (issue #505).
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusHostingTests.cs:AddWhizbangSignalBus_HostStartAlone_StartsRegisteredTransportsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Signals/SignalBusHostingTests.cs:AddWhizbangSignalBus_HostStartAlone_StartsRegisteredPullSourcesAsync</tests>
public sealed partial class SignalBusHostedService(
  SignalBus bus,
  ILogger<SignalBusHostedService> logger
) : IHostedService {
  private readonly SignalBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
  private readonly ILogger<SignalBusHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

  /// <inheritdoc />
  public async Task StartAsync(CancellationToken cancellationToken) {
    await _bus.StartAsync(cancellationToken).ConfigureAwait(false);
    LogBusStarted(_logger);
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Signal bus started: transports and pull sources are wired to the host lifecycle")]
  private static partial void LogBusStarted(ILogger logger);
}
