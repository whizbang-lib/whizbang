using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Releases the read-model barrier: waits for the schema gate (Migrate), then for the
/// perspective startup scan — registry init, orphan reconcile, rewind repair — when a
/// <see cref="PerspectiveWorker"/> is registered, then opens <see cref="IReadModelsReadyGate"/>.
/// A host with no perspectives has no read models to repair, so the schema gate alone releases it.
/// </summary>
/// <remarks>
/// Fail-closed like everything else in the band: a migration that never completes, or a startup
/// scan that never finishes, keeps the barrier closed and every lens read refusing — which is the
/// honest answer while the read models cannot be trusted.
/// </remarks>
/// <docs>proposals/startup-pipeline#seams</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ReadModelsReadyDriverTests.cs</tests>
public sealed partial class ReadModelsReadyDriver : BackgroundService {
  private readonly IReadModelsReadyGate _readModelsGate;
  private readonly ISchemaReadyGate _schemaReadyGate;
  private readonly IServiceProvider _services;
  private readonly ILogger<ReadModelsReadyDriver> _logger;

  /// <summary>Creates the driver over the two signals it composes.</summary>
  public ReadModelsReadyDriver(
      IReadModelsReadyGate readModelsGate,
      ISchemaReadyGate schemaReadyGate,
      IServiceProvider services,
      ILogger<ReadModelsReadyDriver>? logger = null) {
    ArgumentNullException.ThrowIfNull(readModelsGate);
    ArgumentNullException.ThrowIfNull(schemaReadyGate);
    ArgumentNullException.ThrowIfNull(services);
    _readModelsGate = readModelsGate;
    _schemaReadyGate = schemaReadyGate;
    _services = services;
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReadModelsReadyDriver>.Instance;
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      await _schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);

      // Resolved lazily, not in the constructor: the worker is registered by the consumer's
      // generated code only when perspectives exist, and resolving it must not race hosting.
      var perspectiveWorker = _services.GetService<PerspectiveWorker>();
      if (perspectiveWorker is not null) {
        await perspectiveWorker.StartupScanComplete.WaitAsync(stoppingToken).ConfigureAwait(false);
      }

      _readModelsGate.MarkReady();
      LogReleased(_logger, perspectiveWorker is not null);
    } catch (OperationCanceledException) {
      // host shutdown while the barrier was still closed — fail-closed, reads kept refusing
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Read-model barrier released (perspective startup scan awaited: {ScanAwaited}) — lens reads resume")]
  static partial void LogReleased(ILogger logger, bool scanAwaited);
}
