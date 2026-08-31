using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// The framework's own declared steps. Adopted incrementally: each existing startup behaviour
/// becomes a step here, initially wrapping its current mechanism so nothing changes except that
/// the behaviour gains a name, an outcome, and a barrier others can declare a dependency on.
/// </summary>
/// <docs>operations/startup/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineWiringTests.cs</tests>
public static class FrameworkStartupSteps {
#pragma warning disable CA1707
  /// <summary>The assessment step — where this instance stands, before anything changes.</summary>
  public const string ASSESS = "Assess";
  /// <summary>The migration step — schema initialization, ledger, version stamp.</summary>
  public const string MIGRATE = "Migrate";
  /// <summary>The post-ready table-rewrite step — fleet-exclusive, deliberately unbounded.</summary>
  public const string REWRITE = "Rewrite";
#pragma warning restore CA1707
}

/// <summary>
/// The <c>Migrate</c> step: completes when <see cref="ISchemaReadyGate"/> opens. The gate is thereby
/// demoted from <em>the</em> global barrier to this one step's completion signal — the driver's
/// initializer keeps doing the work and calling <c>MarkReady()</c> exactly as before; the step
/// observes it. Workers then wait on the step they actually depend on
/// (<c>IStartupPipelineState.WaitForAsync("Migrate")</c>) rather than all sharing one boolean.
/// </summary>
/// <docs>operations/startup/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineWiringTests.cs</tests>
public sealed class MigrateStartupStep : IStartupStep {
  private readonly ISchemaReadyGate _schemaReadyGate;

  /// <summary>Creates the step over the schema-ready gate.</summary>
  public MigrateStartupStep(ISchemaReadyGate schemaReadyGate) {
    ArgumentNullException.ThrowIfNull(schemaReadyGate);
    _schemaReadyGate = schemaReadyGate;
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; } = new() {
    Name = FrameworkStartupSteps.MIGRATE,
    // Assessment precedes migration: an instance cleared only to serve — or standing down —
    // must know it BEFORE the migration barrier, not after.
    DependsOn = [FrameworkStartupSteps.ASSESS],
  };

  /// <inheritdoc />
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    await _schemaReadyGate.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
    return new StartupStepReport(StartupStepOutcome.Completed);
  }
}

/// <summary>
/// Hosts the pipeline: runs the registered steps once at startup, through the registered observers.
/// The run is fail-closed by construction — a blocking step that never completes keeps the pipeline
/// (and therefore <see cref="IStartupPipelineState.IsComplete"/>) unfinished, exactly as the schema
/// gate keeps the availability filter refusing writes today.
/// </summary>
/// <docs>operations/startup/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineWiringTests.cs</tests>
public sealed partial class StartupPipelineWorker : BackgroundService {
  private readonly StartupPipelineRunner _runner;
  private readonly ILogger<StartupPipelineWorker> _logger;

  /// <summary>Creates the worker over the runner.</summary>
  public StartupPipelineWorker(StartupPipelineRunner runner, ILogger<StartupPipelineWorker>? logger = null) {
    ArgumentNullException.ThrowIfNull(runner);
    _runner = runner;
    _logger = logger ?? NullLogger<StartupPipelineWorker>.Instance;
  }

  /// <inheritdoc />
  /// <remarks>
  /// Nothing that happens inside the pipeline may stop the host. This is a
  /// <see cref="BackgroundService"/>, so anything escaping here meets the default
  /// <c>HostOptions.BackgroundServiceExceptionBehavior</c> of <c>StopHost</c> and terminates the
  /// process — and because that termination is a graceful stop, the process exits ZERO and logs
  /// an orderly shutdown. A host destroyed this way is indistinguishable, to anything watching
  /// exit codes or restart reasons, from one that was asked to stop. It can recur indefinitely
  /// while every crash signal stays clean.
  ///
  /// Not stopping is also the more informative outcome, because the pipeline is fail-closed: a
  /// run that did not complete leaves the availability filter refusing writes, so the service
  /// stays up and reports unready with the reason in its logs. The runner already handles the
  /// failures it can classify; this is the backstop for everything it cannot — order resolution,
  /// for one, throws before any step runs.
  /// </remarks>
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      await _runner.RunAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
      // host shutdown while a step was still waiting — expected on a fail-closed boot
    } catch (Exception ex) {
      // Critical, not Error: the consequence is that this instance never becomes ready, which
      // outlives the log line and needs to be findable from it. The exception carries its own
      // type and message, so nothing is recomputed to say what failed.
      if (_logger is not null) {
        LogPipelineAbandoned(_logger, ex);
      }
    }
  }

  [LoggerMessage(EventId = 6, Level = LogLevel.Critical,
    Message = "Startup pipeline abandoned. The pipeline did not complete, so this instance stays "
            + "fail-closed and will NOT become ready — it is up but serving nothing. The host is "
            + "deliberately left running: stopping it here would exit zero and look like a "
            + "graceful shutdown to everything watching for crashes.")]
  static partial void LogPipelineAbandoned(ILogger logger, Exception exception);
}
