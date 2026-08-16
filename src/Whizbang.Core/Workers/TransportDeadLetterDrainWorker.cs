using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Workers;

/// <summary>
/// v0.502 slice C.8/C.9 — periodically drains every registered
/// <see cref="ITransportDeadLetterDrainer"/> (one per transport binding, e.g. ASB subscription
/// or RMQ queue) by re-submitting broker-DLQ messages onto the normal receive path.
/// </summary>
/// <remarks>
/// <para>
/// Symmetric with — but distinct from — <see cref="DeadLetterRecoveryWorker"/> which manages
/// Whizbang's internal <c>wh_dead_letters</c> table. The internal worker is policy-driven and
/// per-row; this worker is transport-aggressive and indiscriminate: anything the broker
/// dropped in DLQ gets one re-submission attempt per tick. Whizbang's event store is
/// source-of-truth, so re-attempts that ultimately fail just land in DLQ again and the
/// underlying events keep replaying through normal claim — no data loss either way.
/// </para>
/// <para>
/// Cadence default is 10 min — broker DLQs aren't latency-sensitive (they exist precisely
/// because the original receive failed). Operators tune <see cref="TransportDeadLetterDrainWorkerOptions.IntervalMinutes"/>
/// upward when their brokers have aggressive per-message timeouts and downward when DLQ
/// volume runs hot during normal operation.
/// </para>
/// <para>
/// No drainers registered → the worker logs once and idles forever. Adding a transport-
/// hosting package that registers an <see cref="ITransportDeadLetterDrainer"/> in DI is
/// enough to activate it.
/// </para>
/// </remarks>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
public partial class TransportDeadLetterDrainWorker(
  IServiceScopeFactory scopeFactory,
  IOptions<TransportDeadLetterDrainWorkerOptions> options,
  WhizbangMetrics whizbangMetrics,
  ILogger<TransportDeadLetterDrainWorker> logger,
  // Startup barrier: draining writes to wh_dead_letters, which may not exist on a first boot.
  // Optional only so existing fixtures construct unchanged; DI always supplies it.
  ISchemaReadyGate? schemaReadyGate = null
) : BackgroundService {
  private readonly ISchemaReadyGate? _schemaReadyGate = schemaReadyGate;

  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
  private readonly TransportDeadLetterDrainWorkerOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
  private readonly ILogger<TransportDeadLetterDrainWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  private readonly Counter<long> _drained = _buildCounter(whizbangMetrics);

  private long _totalDrained;

  /// <summary>Meter name used by <see cref="TransportDeadLetterDrainWorker"/>.</summary>
#pragma warning disable CA1707
  public const string METER_NAME = "Whizbang.TransportDeadLetterDrain";
#pragma warning restore CA1707

  private static Counter<long> _buildCounter(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);
    return meter.CreateCounter<long>(
      name: "whizbang.transport_dlq.drained",
      description: "Messages re-submitted from a transport broker's dead-letter queue back onto the normal receive path.");
  }

  /// <summary>Cumulative count of broker-DLQ messages re-submitted since process start.</summary>
  public long TotalDrained => Interlocked.Read(ref _totalDrained);

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    LogStarted(_logger, _options.IntervalMinutes);

    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken);
      } catch (OperationCanceledException) {
        return;
      }
    }

    if (!_options.Enabled) {
      LogDisabled(_logger);
      try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); } catch (OperationCanceledException) { }
      return;
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      } catch (Exception ex) {
        // Individual drainer failures already get logged inside DrainOnceAsync; this
        // catches truly unexpected aggregate failures (DI resolution issues, etc.).
        LogCycleError(_logger, ex);
      }

      try {
        await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }
    }

    LogStopped(_logger);
  }

  /// <summary>
  /// Runs one drain pass across every registered <see cref="ITransportDeadLetterDrainer"/>.
  /// Public so operators can trigger a sweep on demand (e.g. from a maintenance endpoint)
  /// and so the regression suite can exercise the dispatch logic without spinning the loop.
  /// </summary>
  public async Task DrainOnceAsync(CancellationToken ct) {
    using var scope = _scopeFactory.CreateScope();
    var drainers = scope.ServiceProvider.GetServices<ITransportDeadLetterDrainer>().ToList();
    if (drainers.Count == 0) {
      // First tick logs once; subsequent ticks stay quiet (no drainers = no work).
      return;
    }

    foreach (var drainer in drainers) {
      try {
        var drained = await drainer.DrainDeadLetterQueueAsync(_options.MaxPerTick, ct).ConfigureAwait(false);
        if (drained > 0) {
          Interlocked.Add(ref _totalDrained, drained);
          _drained.Add(drained,
            new KeyValuePair<string, object?>("transport", drainer.TransportName));
          LogDrained(_logger, drained, drainer.TransportName);
        }
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        // One drainer failing doesn't stop the others — log and continue.
        LogDrainerError(_logger, drainer.TransportName, ex);
      }
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "TransportDeadLetterDrainWorker started: intervalMinutes={IntervalMinutes}")]
  static partial void LogStarted(ILogger logger, int intervalMinutes);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "TransportDeadLetterDrainWorker disabled — broker DLQ messages stay in DLQ until manually drained")]
  static partial void LogDisabled(ILogger logger);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "TransportDeadLetterDrainWorker stopped")]
  static partial void LogStopped(ILogger logger);

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "TransportDeadLetterDrainWorker cycle failed; will retry on next tick")]
  static partial void LogCycleError(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
    Message = "TransportDeadLetterDrainWorker drainer={Transport} failed; will retry next tick")]
  static partial void LogDrainerError(ILogger logger, string transport, Exception ex);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information,
    Message = "TransportDeadLetterDrainWorker drained {Count} message(s) from transport={Transport}")]
  static partial void LogDrained(ILogger logger, int count, string transport);
}

/// <summary>Configuration for <see cref="TransportDeadLetterDrainWorker"/>.</summary>
/// <docs>operations/dead-letter-queue/transport-recovery</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportDeadLetterDrainWorkerTests.cs:NoDrainersRegistered_NoOpAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportDeadLetterDrainWorkerTests.cs:DrainersInvoked_AllReturnNonZero_TotalAccumulatedAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportDeadLetterDrainWorkerTests.cs:MultipleDrainOnceCalls_AccumulateTotalAsync</tests>
public sealed class TransportDeadLetterDrainWorkerOptions {
  /// <summary>
  /// Killswitch. Default <c>true</c>. When <c>false</c>, broker DLQ messages stay in DLQ
  /// indefinitely — operators have to drain them manually via the broker's own tooling.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Backstop cadence between drain sweeps, in minutes. Default <c>10</c>. Higher values
  /// reduce broker load; lower values reduce DLQ growth latency during incidents.
  /// </summary>
  public int IntervalMinutes { get; set; } = 10;

  /// <summary>
  /// Maximum messages re-submitted per drainer per tick. Bounds runtime of a single sweep
  /// when a DLQ is large. Default <c>500</c>. Subsequent ticks pick up the remainder.
  /// </summary>
  public int MaxPerTick { get; set; } = 500;
}
