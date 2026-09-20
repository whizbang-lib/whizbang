using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Startup;

/// <summary>The verdict <c>Assess</c> produces before anything is changed.</summary>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
public enum StartupVerdict {
  /// <summary>Fresh database, or this version has work to do and no live conflict — contend to migrate.</summary>
  Migrate,
  /// <summary>The schema is at (or below) this version with nothing pending — proceed to serve.</summary>
  Serve,
  /// <summary>
  /// The ledger records a version newer than this binary — never apply anything; release
  /// capabilities, hold the data plane, report not-ready-while-alive. Also the refusal verdict
  /// for an unparseable version: every wrong answer at that point is worse than stopping.
  /// </summary>
  StandDown,
}

/// <summary>What <c>Assess</c> decided, and why.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Reason">Framework-authored explanation (versions involved, what was compared).</param>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
public sealed record StartupAssessment(StartupVerdict Verdict, string Reason);

/// <summary>
/// Computes the <c>Assess</c> verdict: this binary's library version against the versions the
/// migration ledger records. Supplied by the storage driver — only a driver can read the ledger.
/// A read: no lock, no transaction, no DDL.
/// </summary>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
public interface IStartupAssessor {
  /// <summary>Reads the ledger and produces the verdict. Implementations should throw on an
  /// unreadable ledger only when refusing is safer than proceeding; the step maps a throw to a
  /// failed (fail-closed) assessment.</summary>
  Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The <c>Assess</c> step: decides where this instance stands before anything changes. It runs on
/// <b>every instance</b>, unlike <c>Migrate</c> — an instance that will never win the migrator
/// duty still needs to know whether it is obsolete — and it is ordered before the migration
/// barrier so an instance cleared only to serve never contends for a duty it must not perform.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="StartupVerdict.StandDown"/> verdict reports as a FAILED blocking step, which is
/// the pipeline's fail-closed posture doing exactly what it should: readiness never fires, the
/// composite <c>Ready</c> never signals, health reports Faulted with the step's name, and the
/// status surface shows the verdict — not-ready-while-alive, which is precisely the state that
/// tells an orchestrator to replace the instance and a load balancer to stop sending it traffic.
/// </para>
/// <para>
/// The verdict cannot be a startup-only fact — an instance that was current when it booted
/// becomes obsolete the moment a newer peer migrates underneath it. The standby watcher carries
/// that; this step establishes the verdict at boot.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#assess</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/AssessStartupStepTests.cs</tests>
public sealed partial class AssessStartupStep : IStartupStep {
  private readonly IStartupAssessor? _assessor;
  private readonly ILogger<AssessStartupStep> _logger;

  /// <summary>Creates the step over the driver-supplied assessor, when one is registered.</summary>
  public AssessStartupStep(IStartupAssessor? assessor = null, ILogger<AssessStartupStep>? logger = null) {
    _assessor = assessor;
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AssessStartupStep>.Instance;
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; } = new() {
    Name = FrameworkStartupSteps.ASSESS,
  };

  /// <inheritdoc />
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    if (_assessor is null) {
      return new StartupStepReport(StartupStepOutcome.Skipped,
        "no assessor registered — the storage driver supplies the verdict machinery");
    }

    var assessment = await _assessor.AssessAsync(cancellationToken).ConfigureAwait(false);
    LogVerdict(_logger, assessment.Verdict, assessment.Reason);

    return assessment.Verdict switch {
      // Standing down is fail-closed by construction: a failed blocking step keeps readiness
      // pending forever — not-ready-while-alive, exactly what an obsolete instance must report.
      StartupVerdict.StandDown => new StartupStepReport(StartupStepOutcome.Failed, assessment.Reason),
      _ => new StartupStepReport(StartupStepOutcome.Completed, assessment.Reason),
    };
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Assess verdict: {Verdict} — {Reason}")]
  static partial void LogVerdict(ILogger logger, StartupVerdict verdict, string reason);
}
