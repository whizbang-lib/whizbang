using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Logs, once at startup, any concurrency setting that cannot take effect.
/// </summary>
/// <remarks>
/// <para>
/// Warning, not failure. Serial processing is slow, not incorrect, and a deployment that boots
/// today must keep booting after upgrading — turning an existing (if unintended) configuration into
/// a hard startup failure would convert a performance problem into an outage.
/// </para>
/// <para>
/// Startup rather than per-cycle: the condition is static for the process lifetime, so one line at
/// boot is the entire signal. Repeating it would be the same hot-path log flood this codebase has
/// already been bitten by.
/// </para>
/// </remarks>
/// <docs>operations/workers/concurrency-governor</docs>
/// <tests>tests/Whizbang.Core.Tests/Diagnostics/InertConcurrencyStartupReporterTests.cs</tests>
internal sealed partial class InertConcurrencyStartupReporter : IHostedService {
  private readonly ILogger<InertConcurrencyStartupReporter> _logger;
  private readonly WorkCoordinatorOptions? _coordinator;
  private readonly OrderedStreamProcessorOptions? _orderedStream;
  private readonly OutboxDrainWorkerOptions? _outboxDrain;
  private readonly InboxDispatchWorkerOptions? _inboxDispatch;

  /// <summary>DI constructor. Every option set is optional — a host may configure none of them.</summary>
  public InertConcurrencyStartupReporter(
      ILogger<InertConcurrencyStartupReporter> logger,
      IOptions<WorkCoordinatorOptions>? coordinator = null,
      IOptions<OrderedStreamProcessorOptions>? orderedStream = null,
      IOptions<OutboxDrainWorkerOptions>? outboxDrain = null,
      IOptions<InboxDispatchWorkerOptions>? inboxDispatch = null) {
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _coordinator = coordinator?.Value;
    _orderedStream = orderedStream?.Value;
    _outboxDrain = outboxDrain?.Value;
    _inboxDispatch = inboxDispatch?.Value;
  }

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    foreach (var finding in InertConcurrencyReport.Analyze(
                 _coordinator, _orderedStream, _outboxDrain, _inboxDispatch)) {
      LogInertConcurrency(_logger, finding);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Warning,
    Message = "Whizbang concurrency setting has no effect: {Finding}")]
  static partial void LogInertConcurrency(ILogger logger, string finding);
}
