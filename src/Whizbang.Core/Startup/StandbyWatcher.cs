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
/// <docs>proposals/startup-pipeline#handshake</docs>
public sealed class StandbyWatcherOptions {
  /// <summary>How often the watcher checks for an active standby request. The poll is the floor
  /// beneath any faster signal; the handshake's latency budget is bounded by it.</summary>
  public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>How often a serving instance re-assesses its own verdict against the ledger — the
  /// verdict is not a startup-only fact; an instance becomes obsolete the moment a newer peer
  /// migrates underneath it.</summary>
  public TimeSpan ObsolescenceInterval { get; set; } = TimeSpan.FromSeconds(60);

  /// <summary>How stale the requester's heartbeat may be before its request is void. Matches the
  /// fleet's ordinary staleness threshold: a dead migrator must not strand its peers in standby.</summary>
  public TimeSpan RequesterLivenessWindow { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The peer side of the standby handshake, and the runtime keeper of the <c>Assess</c> verdict.
/// Watches for an active standby request from a NEWER version: on seeing one, drains and holds by
/// advancing the lifecycle to <see cref="LifecyclePhase.StandingBy"/> — which pauses every
/// run-control participant and, through the instance-state participant, posts <c>StandingBy</c>
/// on this instance's row for the migrator to observe. Then watches the outcome:
/// </summary>
/// <remarks>
/// <para>
/// <b>Migration committed</b> (the ledger now records a newer version — the re-assessment says
/// StandDown): the instance shuts down, as the handshake promises. <b>Request withdrawn</b> (the
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
/// <docs>proposals/startup-pipeline#handshake</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StandbyWatcherTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StandbyHandshakeE2ETests.cs</tests>
public sealed partial class StandbyWatcher : BackgroundService {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider? _instanceProvider;
  private readonly ILibraryVersionProvider? _versionProvider;
  private readonly IWhizbangLifecycleState _lifecycle;
  private readonly IStartupAssessor? _assessor;
  private readonly IHostApplicationLifetime _hostLifetime;
  private readonly StartupPipelineRunner? _pipelineRunner;
  private readonly ISchemaReadyGate? _schemaReadyGate;
  private readonly StandbyWatcherOptions _options;
  private readonly ILogger<StandbyWatcher> _logger;

  private bool _standingByForHandshake;
  private bool _standingDownAsObsolete;
  private DateTimeOffset _lastObsolescenceCheck = DateTimeOffset.MinValue;

  /// <summary>Creates the watcher. Inert without an instance identity or a coordinator.</summary>
  public StandbyWatcher(
      IServiceScopeFactory scopeFactory,
      IWhizbangLifecycleState lifecycle,
      IHostApplicationLifetime hostLifetime,
      IServiceInstanceProvider? instanceProvider = null,
      ILibraryVersionProvider? versionProvider = null,
      IStartupAssessor? assessor = null,
      StartupPipelineRunner? pipelineRunner = null,
      ISchemaReadyGate? schemaReadyGate = null,
      StandbyWatcherOptions? options = null,
      ILogger<StandbyWatcher>? logger = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(lifecycle);
    ArgumentNullException.ThrowIfNull(hostLifetime);
    _scopeFactory = scopeFactory;
    _lifecycle = lifecycle;
    _hostLifetime = hostLifetime;
    _instanceProvider = instanceProvider;
    _versionProvider = versionProvider;
    _assessor = assessor;
    _pipelineRunner = pipelineRunner;
    _schemaReadyGate = schemaReadyGate;
    _options = options ?? new StandbyWatcherOptions();
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StandbyWatcher>.Instance;
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if (_instanceProvider is null) {
      return;   // no identity — no handshake to participate in
    }
    if (_schemaReadyGate is not null) {
      try {
        await _schemaReadyGate.WaitForReadyAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        return;
      }
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
        await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
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
      await _lifecycle.AdvanceToAsync(LifecyclePhase.StandingBy, cancellationToken).ConfigureAwait(false);
      _standingByForHandshake = true;
      return;
    }

    // The verdict is not a startup-only fact — re-assess on the slow cadence.
    if (_assessor is not null
        && DateTimeOffset.UtcNow - _lastObsolescenceCheck >= _options.ObsolescenceInterval) {
      _lastObsolescenceCheck = DateTimeOffset.UtcNow;
      var assessment = await _assessor.AssessAsync(cancellationToken).ConfigureAwait(false);
      if (assessment.Verdict == StartupVerdict.StandDown) {
        LogStandingDownAsObsolete(_logger, assessment.Reason);
        await _lifecycle.AdvanceToAsync(LifecyclePhase.StandingBy, cancellationToken).ConfigureAwait(false);
        _standingDownAsObsolete = true;
      }
    }
  }

  private bool _isBindingOnUs(StandbyRequest request) {
    if (request.RequestedBy == _instanceProvider!.InstanceId) {
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
    if (!SemanticVersion.TryParse(_versionProvider?.LibraryVersion, out var mine)) {
      return true;
    }
    return requested.CompareTo(mine) > 0;
  }

  private bool _requesterIsAlive(StandbyRequest request) =>
    request.RequesterLastHeartbeatAt is { } heardAt
      && DateTimeOffset.UtcNow - heardAt <= _options.RequesterLivenessWindow;

  private async Task _watchOutcomeAsync(StandbyRequest? request, CancellationToken cancellationToken) {
    if (request is not null && _requesterIsAlive(request)) {
      return;   // the handshake is still in flight — hold
    }

    // The request is gone (withdrawn) or the migrator died. The outcome is answerable precisely,
    // and the answer is the one Assess already computes: a rollback left the ledger exactly as we
    // last read it, a commit made it newer.
    var verdict = StartupVerdict.Serve;
    string reason = "no assessor registered — treating the withdrawn request as a rollback";
    if (_assessor is not null) {
      var assessment = await _assessor.AssessAsync(cancellationToken).ConfigureAwait(false);
      verdict = assessment.Verdict;
      reason = assessment.Reason;
    }

    if (verdict == StartupVerdict.StandDown) {
      // The migration committed: the schema moved beneath this binary. Shut down, as the
      // handshake promises — the orchestrator restarts or replaces us.
      LogShuttingDownAfterCommit(_logger, reason);
      _hostLifetime.StopApplication();
      return;
    }

    // Rolled back, or the migrator died before changing anything: revival is not a second
    // pipeline — re-enter at Assess by re-running the re-entrant runner, then resume.
    LogReviving(_logger, reason);
    if (_pipelineRunner is not null) {
      _ = await _pipelineRunner.RunAsync(cancellationToken).ConfigureAwait(false);
    }
    await _lifecycle.AdvanceToAsync(LifecyclePhase.Running, cancellationToken).ConfigureAwait(false);
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

  [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
    Message = "Standby outcome: migration committed — shutting down as the handshake promises ({Reason})")]
  static partial void LogShuttingDownAfterCommit(ILogger logger, string reason);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information,
    Message = "Standby outcome: reviving — re-entering the pipeline at Assess ({Reason})")]
  static partial void LogReviving(ILogger logger, string reason);
}
