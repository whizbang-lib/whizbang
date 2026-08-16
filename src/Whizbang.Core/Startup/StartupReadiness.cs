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

  /// <inheritdoc />
  public async Task StartedAsync(CancellationToken cancellationToken) {
    var watch = Stopwatch.StartNew();

    await _pipelineState.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);

    foreach (var contributor in _contributors) {
      await contributor.WaitForContributorReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    _signal.MarkReady();
    LogReady(_logger, watch.Elapsed.TotalMilliseconds, _contributors.Count);
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
}
