using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// The framework's own declared steps. Adopted incrementally: each existing startup behaviour
/// becomes a step here, initially wrapping its current mechanism so nothing changes except that
/// the behaviour gains a name, an outcome, and a barrier others can declare a dependency on.
/// </summary>
/// <docs>proposals/startup-pipeline</docs>
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
/// <docs>proposals/startup-pipeline</docs>
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
/// <docs>proposals/startup-pipeline</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupPipelineWiringTests.cs</tests>
public sealed class StartupPipelineWorker : BackgroundService {
  private readonly StartupPipelineRunner _runner;

  /// <summary>Creates the worker over the runner.</summary>
  public StartupPipelineWorker(StartupPipelineRunner runner) {
    ArgumentNullException.ThrowIfNull(runner);
    _runner = runner;
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    try {
      await _runner.RunAsync(stoppingToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
      // host shutdown while a step was still waiting — expected on a fail-closed boot
    }
  }
}
