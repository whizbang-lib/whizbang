using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Startup;

/// <summary>
/// A readiness signal that already exists but the pipeline must not consider itself ready without —
/// transport subscription readiness is the canonical case: the consumer workers complete their
/// <c>SubscriptionsReady</c> task today and nothing consumes it. Implementations surface such
/// signals so <see cref="StartupReadyService"/> can compose them into <c>Ready</c>.
/// </summary>
/// <docs>operations/startup/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupReadyCompositeTests.cs</tests>
public interface IStartupReadinessContributor {
  /// <summary>A stable name for logs and health detail — what the composite is waiting on.</summary>
  string ContributorName { get; }

  /// <summary>Completes when this contributor's part of readiness is satisfied.</summary>
  Task WaitForContributorReadyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The terminal startup signal: the blocking steps have drained <em>and</em> every registered
/// readiness contributor has answered. Sticky per run of the host — this is the instance-level
/// "fully up", one level above <see cref="IStartupPipelineState.IsReady"/> (which is pipeline-only).
/// </summary>
/// <docs>operations/startup/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupReadyCompositeTests.cs</tests>
public interface IStartupReadySignal {
  /// <summary>Whether the composite has been signalled.</summary>
  bool IsReady { get; }

  /// <summary>Completes when the composite is signalled. Sticky — late waiters return immediately.</summary>
  Task WaitForReadyAsync(CancellationToken cancellationToken);
}

/// <summary>Default <see cref="IStartupReadySignal"/>: one sticky completion, any number of waiters.</summary>
/// <docs>operations/startup/startup-pipeline</docs>
public sealed class StartupReadySignal : IStartupReadySignal {
  private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <inheritdoc />
  public bool IsReady => _ready.Task.IsCompleted;

  /// <inheritdoc />
  public Task WaitForReadyAsync(CancellationToken cancellationToken)
    => _ready.Task.WaitAsync(cancellationToken);

  /// <summary>Signals the composite. Idempotent.</summary>
  public void MarkReady() => _ready.TrySetResult();
}

/// <summary>
/// Composes <c>Ready</c> on the one seam that means "after everything":
/// <see cref="IHostedLifecycleService.StartedAsync"/> runs after every hosted service's
/// <c>StartAsync</c> has returned, and nothing in the framework had ever claimed it. It waits for
/// the pipeline's blocking steps to drain, then for every registered contributor (transport
/// subscription readiness among them), and only then marks the signal.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed by construction: a blocking step that fails keeps
/// <see cref="IStartupPipelineState.WaitForReadyAsync"/> pending forever, so the signal never
/// fires and the host never reports itself fully up — the same posture the schema gate takes on a
/// failed migration. The server is already listening by the time <c>StartedAsync</c> runs (every
/// <c>StartAsync</c>, Kestrel's included, has returned), so liveness endpoints keep answering
/// while readiness correctly reports not-ready.
/// </para>
/// <para>
/// A contributor that faults propagates: transport subscription failure is a startup failure
/// today, and composing it into <c>Ready</c> must not soften that.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupReadyCompositeTests.cs</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Integration.Tests/ServiceBusConsumerWorkerIntegrationTests.cs</tests>
public sealed partial class StartupReadyService : IHostedLifecycleService {
  private readonly IStartupPipelineState _pipelineState;
  private readonly StartupReadySignal _signal;
  private readonly IReadOnlyList<IStartupReadinessContributor> _contributors;
  private readonly ILogger<StartupReadyService> _logger;

  /// <summary>Creates the service over the pipeline state, the signal it marks, and the contributors it awaits.</summary>
  public StartupReadyService(
      IStartupPipelineState pipelineState,
      StartupReadySignal signal,
      IReadOnlyList<IStartupReadinessContributor>? contributors = null,
      ILogger<StartupReadyService>? logger = null) {
    ArgumentNullException.ThrowIfNull(pipelineState);
    ArgumentNullException.ThrowIfNull(signal);
    _pipelineState = pipelineState;
    _signal = signal;
    _contributors = contributors ?? [];
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StartupReadyService>.Instance;
  }

  /// <summary>
  /// How often the wait is probed for narration. The composite stays fail-closed and unbounded —
  /// but never silent: a boot blocked here names what it is waiting on, on a backoff, because a
  /// hang with no output gives a consumer nothing to diagnose (issue #493).
  /// </summary>
  internal TimeSpan WaitProbeInterval { get; init; } = TimeSpan.FromSeconds(5);

  /// <inheritdoc />
  public async Task StartedAsync(CancellationToken cancellationToken) {
    var watch = Stopwatch.StartNew();

    // Cancellation here means the host is being told to stop while startup is still waiting —
    // an ordinary rollout SIGTERM, a liveness restart, an operator abandoning a slow boot. It
    // must NOT escape: StartedAsync runs on IHostedLifecycleService, and Host.StartAsync aborts
    // on the first exception and rethrows, so propagating it turns a routine stop into an
    // UNHANDLED exception and a non-zero exit — which an orchestrator reads as a crash and
    // restarts, cancelling the next startup the same way. That is a self-sustaining crash loop
    // manufactured out of a normal deploy. Leaving quietly is correct: readiness is a composite
    // that is fail-closed by construction, so never signalling it is the honest outcome, and the
    // narration above has already said what the wait was blocked on. Genuine faults still
    // propagate — only cancellation is graceful.
    try {
      await _narratedWaitAsync(
        _pipelineState.WaitForReadyAsync(cancellationToken), _describePipeline, watch, cancellationToken)
        .ConfigureAwait(false);

      foreach (var contributor in _contributors) {
        await _narratedWaitAsync(
          contributor.WaitForContributorReadyAsync(cancellationToken),
          () => $"readiness contributor '{contributor.ContributorName}'", watch, cancellationToken)
          .ConfigureAwait(false);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      LogCanceledDuringStartup(_logger, watch.Elapsed.TotalSeconds);
      return;
    }

    _signal.MarkReady();
    LogReady(_logger, watch.Elapsed.TotalMilliseconds, _contributors.Count);
  }

  private string _describePipeline() {
    if (!_pipelineState.HasRunStarted) {
      return "the startup pipeline (no run has started yet)";
    }
    var pending = _pipelineState.SnapshotSteps()
      .Where(step => step.Blocking && step.Status is not StartupStepStatus.Completed)
      .Select(step => $"{step.Name} ({step.Status})")
      .ToList();
    return pending.Count == 0
      ? "the startup pipeline"
      : $"startup pipeline step(s) {string.Join(", ", pending)}";
  }

  /// <summary>Awaits, narrating on a backoff (probes 3, 10, 30, then every 60) what is blocking.</summary>
  private async Task _narratedWaitAsync(
      Task wait, Func<string> describe, Stopwatch watch, CancellationToken cancellationToken) {
    var probes = 0;
    var nextNarration = 3;
    while (true) {
      var winner = await Task.WhenAny(wait, Task.Delay(WaitProbeInterval, cancellationToken))
        .ConfigureAwait(false);
      if (ReferenceEquals(winner, wait)) {
        await wait.ConfigureAwait(false);   // propagate faults/cancellation
        return;
      }
      probes++;
      if (probes >= nextNarration) {
        nextNarration = nextNarration switch { 3 => 10, 10 => 30, _ => nextNarration + 60 };
        LogStillWaiting(_logger, watch.Elapsed.TotalSeconds, describe());
      }
    }
  }

  /// <inheritdoc />
  public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  /// <inheritdoc />
  public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  /// <inheritdoc />
  public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Startup ready: blocking steps drained and {ContributorCount} contributor(s) answered after {ElapsedMs:F0} ms")]
  static partial void LogReady(ILogger logger, double elapsedMs, int contributorCount);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Startup is not ready after {ElapsedSeconds:F0}s — still waiting on {Waiting}. "
            + "The composite is fail-closed by design; this narration exists so the wait is never silent")]
  static partial void LogStillWaiting(ILogger logger, double elapsedSeconds, string waiting);

  [LoggerMessage(EventId = 3, Level = LogLevel.Information,
    Message = "Startup canceled after {ElapsedSeconds:F0}s while waiting for readiness — the host is "
            + "stopping. Readiness was never signalled (the composite is fail-closed); shutting down cleanly")]
  static partial void LogCanceledDuringStartup(ILogger logger, double elapsedSeconds);
}
