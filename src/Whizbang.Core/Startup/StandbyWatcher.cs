// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Versioning;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>Cadences and bounds for the standby watcher.</summary>
/// <docs>operations/startup/rolling-upgrades#the-standby-handshake</docs>
[Whizbang.Core.Configuration.RemovedConfigurationKey("Whizbang:StandbyWatcher:RequesterLivenessWindow", "Whizbang:Workers:Heartbeat:IntervalSeconds")]
public sealed class StandbyWatcherOptions {
  /// <summary>How often the watcher checks for an active standby request. The poll is the floor
  /// beneath any faster signal; the handshake's latency budget is bounded by it.</summary>
  public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>How often a serving instance re-assesses its own verdict against the ledger — the
  /// verdict is not a startup-only fact; an instance becomes obsolete the moment a newer peer
  /// migrates underneath it.</summary>
  public TimeSpan ObsolescenceInterval { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The peer side of the standby handshake, and the runtime keeper of the <c>Assess</c> verdict.
/// Watches for an active standby request from a NEWER version: on seeing one, stands down through the one run-control
/// path (#2045), the same path as a pause or a stop: the run permit closes, the work in hand finishes, held work and
/// roles are released, and <c>StandingBy</c> is posted on this instance's row for the migrator to observe. Then watches
/// the outcome:
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration committed</b> (the ledger now records a newer version — the re-assessment says
/// StandDown): the instance exits through the one exit path (#2126) as an intended
/// <see cref="ExitCause.UpgradeStandDown"/> (exit code 0), as the handshake promises. <b>Request withdrawn</b> (the
/// migrator rolled back and cleared) or <b>migrator died</b> (its heartbeat lapsed — the request
/// is void, peers must not be stranded): revival — re-enter the startup pipeline at <c>Assess</c>
/// by re-running the re-entrant runner, then resume. Every path out of standby is bounded.
/// </para>
/// <para>
/// Independently of any handshake, the watcher re-assesses this instance's own verdict on a slow
/// cadence: an instance that was current when it booted becomes obsolete the moment a newer peer
/// migrates underneath it. On StandDown it enters standby (alive, not ready, reapable) and stays
/// there — replacement is the orchestrator's decision, prompted by the readiness it can see.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#the-standby-handshake</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/StandbyWatcherTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StandbyHandshakeE2ETests.cs</tests>
public sealed partial class StandbyWatcher : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider _instanceProvider;
  private readonly ILibraryVersionProvider _versionProvider;
  private readonly IWhizbangRunController _runControl;
  private readonly IStartupAssessor _assessor;
  private readonly IWhizbangInstanceExit _instanceExit;
  private readonly StartupPipelineRunner? _pipelineRunner;
  private readonly StandbyWatcherOptions _options;
  private readonly ILogger<StandbyWatcher> _logger;
  private readonly TimeProvider _time;

  private bool _standingByForHandshake;
  private bool _standingDownAsObsolete;
  private DateTimeOffset _lastObsolescenceCheck = DateTimeOffset.MinValue;

  /// <summary>Creates the watcher. Inert without an instance identity or a coordinator.</summary>
  /// <param name="scopeFactory">Scopes the work coordinator the standby ledger is read through.</param>
  /// <param name="runControl">The run-control path that stands the instance down and resumes it.</param>
  /// <param name="instanceExit">The one exit path, taken when a migration commits beneath this instance (#2126).</param>
  /// <param name="instanceProvider">This instance's identity; the watcher is inert without it.</param>
  /// <param name="versionProvider">This binary's version, ranked against a requester's.</param>
  /// <param name="assessor">Re-assesses this instance's verdict.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="timeProvider">The clock for the poll cadence, requester liveness and the
  /// obsolescence cadence; the host's registered clock.</param>
  /// <param name="pipelineRunner">Re-run on revival; null when the host has no pipeline.</param>
  /// <param name="options">Cadences and bounds; null takes the defaults.</param>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Dependency-injection constructor: every parameter is a registered service or an optional seam the container fills, and a parameter object would only move the list. Same reasoning as Dispatcher.")]
  public StandbyWatcher(
      IServiceScopeFactory scopeFactory,
      IWhizbangRunController runControl,
      IWhizbangInstanceExit instanceExit,
      IServiceInstanceProvider instanceProvider,
      ILibraryVersionProvider versionProvider,
      IStartupAssessor assessor,
      ILogger<StandbyWatcher> logger,
      TimeProvider timeProvider,
      StartupPipelineRunner? pipelineRunner = null,
      StandbyWatcherOptions? options = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(runControl);
    ArgumentNullException.ThrowIfNull(instanceExit);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _scopeFactory = scopeFactory;
    _runControl = runControl;
    _instanceExit = instanceExit;
    _instanceProvider = instanceProvider;
    _versionProvider = versionProvider;
    _assessor = assessor;
    _pipelineRunner = pipelineRunner;
    _options = options ?? new StandbyWatcherOptions();
    _logger = logger;
    _time = timeProvider;
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_instanceProvider is null) {
      return;   // no identity — no handshake to participate in
    }

    while (!stoppingToken.IsCancellationRequested) {
      try {
        await TickForTestsAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }
#pragma warning disable CA1031, RCS1075 // the watcher is a guardian loop: a transient read failure
      // must not kill it — the next tick retries, and doing nothing is the safe default.
      catch (Exception ex) {
        LogTickFailed(_logger, ex);
      }
#pragma warning restore CA1031, RCS1075
      try {
        await Task.Delay(_options.PollInterval, _time, stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        break;
      }
    }
  }

  /// <summary>Test hook: one watcher decision cycle without the loop — the loop is cadence,
  /// this is behaviour.</summary>
  public async Task TickForTestsAsync(CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return;
    }

    var request = await coordinator.GetStandbyRequestAsync(cancellationToken).ConfigureAwait(false);

    if (_standingByForHandshake) {
      await _watchOutcomeAsync(request, cancellationToken).ConfigureAwait(false);
      return;
    }
    if (_standingDownAsObsolete) {
      return;   // alive, not ready, reapable — replacement is the orchestrator's decision
    }

    if (request is not null && _isBindingOnUs(request)) {
      LogEnteringStandby(_logger, request.RequestedBy, request.RequestedVersion);
      await _runControl.HaltAsync(RunHaltReason.StandDown, cancellationToken).ConfigureAwait(false);
      _standingByForHandshake = true;
      return;
    }

    // The verdict is not a startup-only fact — re-assess on the slow cadence.
    if (_assessor.IsConfigured
        && _time.GetUtcNow() - _lastObsolescenceCheck >= _options.ObsolescenceInterval) {
      _lastObsolescenceCheck = _time.GetUtcNow();
      var assessment = await _assessor.AssessAsync(cancellationToken).ConfigureAwait(false);
      if (assessment.Verdict == StartupVerdict.StandDown) {
        LogStandingDownAsObsolete(_logger, assessment.Reason);
        await _runControl.HaltAsync(RunHaltReason.StandDown, cancellationToken).ConfigureAwait(false);
        _standingDownAsObsolete = true;
      }
    }
  }

  private bool _isBindingOnUs(StandbyRequest request) {
    if (request.RequestedBy == _instanceProvider.InstanceId) {
      return false;   // our own request binds our peers, not us
    }
    if (!_requesterIsAlive(request)) {
      return false;   // a dead migrator's request is void
    }
    // Binding only when the requester is NEWER than this binary — an older or equal peer has no
    // standing to drain us, and an unparseable comparison refuses to bind (never stand down on
    // a guess). Without a version of our own we cannot rank ourselves; the conservative reading
    // is to comply.
    if (!SemanticVersion.TryParse(request.RequestedVersion, out var requested)) {
      return false;
    }
    if (!SemanticVersion.TryParse(_versionProvider.LibraryVersion, out var mine)) {
      return true;
    }
    return requested.CompareTo(mine) > 0;
  }

  // #2027: the database's one liveness rule decides (it counts a requester holding its alive-lock between slow beats);
  // the watcher keeps no window of its own.
  private static bool _requesterIsAlive(StandbyRequest request) => request.RequesterIsLive;

  private async Task _watchOutcomeAsync(StandbyRequest? request, CancellationToken cancellationToken) {
    if (request is not null && _requesterIsAlive(request)) {
      return;   // the handshake is still in flight — hold
    }

    // The request is gone (withdrawn) or the migrator died. The outcome is answerable precisely,
    // and the answer is the one Assess already computes: a rollback left the ledger exactly as we
    // last read it, a commit made it newer.
    var verdict = StartupVerdict.Serve;
    string reason = "no assessor registered — treating the withdrawn request as a rollback";
    if (_assessor.IsConfigured) {
      var assessment = await _assessor.AssessAsync(cancellationToken).ConfigureAwait(false);
      verdict = assessment.Verdict;
      reason = assessment.Reason;
    }

    if (verdict == StartupVerdict.StandDown) {
      // The migration committed: the schema moved beneath this binary. Shut down, as the
      // handshake promises — the orchestrator restarts or replaces us.
      // #2126: through the one exit path, as an intended exit (code 0); its entry states the reason.
      _instanceExit.RequestExit(new InstanceExitRequest(ExitCause.UpgradeStandDown,
        "the newer version's migration committed beneath this stood-down instance, so it exits as the standby handshake promises: " + reason));
      return;
    }

    // Rolled back, or the migrator died before changing anything: revival is not a second
    // pipeline — re-enter at Assess by re-running the re-entrant runner, then resume.
    // The path's resume (Resuming, then Running), not through AcceptingCommands: that rung exists for the window where
    // the read models are still being repaired, and this instance already crossed both barriers before standing by. The
    // schema and read-model gates are sticky-open; the stand-down closed only the run permit, which the resume reopens.
    LogReviving(_logger, reason);
    if (_pipelineRunner is not null) {
      _ = await _pipelineRunner.RunAsync(cancellationToken).ConfigureAwait(false);
    }
    await _runControl.ResumeAsync(RunHaltReason.StandDown, cancellationToken).ConfigureAwait(false);
    _standingByForHandshake = false;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Standby watcher tick failed; retrying on the next interval")]
  static partial void LogTickFailed(ILogger logger, Exception ex);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Entering standby: instance {RequestedBy} is migrating to {RequestedVersion} — draining, holding the data plane, posting STANDING BY")]
  static partial void LogEnteringStandby(ILogger logger, Guid requestedBy, string requestedVersion);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Standing down as obsolete: {Reason}")]
  static partial void LogStandingDownAsObsolete(ILogger logger, string reason);

  // EventId 4 was the shutdown's own entry; since #2126 the exit's one entry (WhizbangInstanceExit) states it.

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Standby outcome: reviving — re-entering the pipeline at Assess ({Reason})")]
  static partial void LogReviving(ILogger logger, string reason);
}
