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

  /// <summary>
  /// DI constructor. Resolves each option set the way its WORKER does.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The workers call <c>GetRequiredService&lt;WorkCoordinatorOptions&gt;()</c> — a plain singleton.
  /// A host that registers the options that way and never calls <c>Configure&lt;T&gt;</c> still leaves
  /// <c>IOptions&lt;T&gt;</c> resolvable, but DEFAULT-CONSTRUCTED. Reading <c>IOptions</c> therefore
  /// reports <c>ParallelizeStreams = false</c> on a system where it is genuinely <c>true</c>.
  /// </para>
  /// <para>
  /// That is not a hypothetical. The first deployment of this diagnostic warned about inert
  /// concurrency on a service whose pod spec had both flags set to <c>true</c> — a false positive on
  /// a healthy configuration, which is the precise failure this feature exists to avoid. The
  /// original tests missed it because they constructed options with <c>Options.Create</c> and so
  /// never exercised the registration shape the framework actually uses.
  /// </para>
  /// <para>
  /// Direct singleton wins; <c>IOptions</c> is the fallback for hosts that do use <c>Configure</c>.
  /// Reporting on options nobody reads is worse than reporting nothing.
  /// </para>
  /// </remarks>
  public InertConcurrencyStartupReporter(
      ILogger<InertConcurrencyStartupReporter> logger,
      IServiceProvider? services = null,
      IOptions<WorkCoordinatorOptions>? coordinator = null,
      IOptions<OrderedStreamProcessorOptions>? orderedStream = null,
      IOptions<OutboxDrainWorkerOptions>? outboxDrain = null,
      IOptions<InboxDispatchWorkerOptions>? inboxDispatch = null) {
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _coordinator = _resolve(services, coordinator);
    _orderedStream = _resolve(services, orderedStream);
    _outboxDrain = _resolve(services, outboxDrain);
    _inboxDispatch = _resolve(services, inboxDispatch);
  }

  /// <summary>Prefers a directly-registered singleton, since that is what the workers read.</summary>
  private static T? _resolve<T>(IServiceProvider? services, IOptions<T>? fallback) where T : class
    => services?.GetService(typeof(T)) as T ?? fallback?.Value;

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
