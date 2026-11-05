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
using Whizbang.Core.Signals;
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
/// StandDown): the instance shuts down, as the handshake promises. <b>Request withdrawn</b> (the
/// migrator rolled back and cleared) or <b>migrator died</b> (its heartbeat lapsed — the request
/// is void, peers must not be stranded): revival — re-enter the startup pipeline at <c>Assess</c>
/// by re-running the re-entrant runner, then resume. Every path out of standby is bounded.
/// </para>
/// <para>
/// Independently of any handshake, the watcher re-assesses this instance's own verdict on a slow
/// cadence: an instance that was current when it booted becomes obsolete the moment a newer peer
/// migrates underneath it. On StandDown it enters standby (alive, not ready, reapable) and stays
/// there while the verdict says so.
/// </para>
/// <para>
/// <b>A newer version online (#2022).</b> The re-assessment is the one Assess verdict, which reads the ledger AND the
/// instances online, by both versions, through the one version rule. Besides the slow cadence, three signals make the
/// next tick ask at once: <see cref="NewerVersionOnlineSignal"/> (a newer instance is serving), and
/// <see cref="InstanceDiedSignal"/> and <see cref="InstanceLeavingSignal"/> (an instance went away). Standing down says
/// why on the instance's row (the stage message names the newer version and where it was seen) before the run-control
/// path writes StandingBy, and is logged once. A stood-down instance resumes when the verdict no longer says StandDown:
/// the verdict a fresh start of this binary would get. A newer instance that died leaves nothing newer online, so the
/// older instances serve again and vote for the roles normally; one that migrated the schema first leaves its newer
/// library version in the ledger, which keeps them down.
/// </para>
/// <para>
/// Once this instance serves (AcceptingCommands or Running), it announces itself once with a durable
/// <see cref="NewerVersionOnlineSignal"/> when the one rule finds an older instance online, and again after each resume,
/// so an older instance that resumed meanwhile hears it.
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
  private readonly IHostApplicationLifetime _hostLifetime;
  private readonly StartupPipelineRunner? _pipelineRunner;
  private readonly StandbyWatcherOptions _options;
  private readonly ILogger<StandbyWatcher> _logger;
  private readonly TimeProvider _time;

  private bool _standingByForHandshake;
  private bool _standingDownAsObsolete;
  private bool _announced;
  private int _askNow;
  private ISignalSubscription[]? _subscriptions;
  private DateTimeOffset _lastObsolescenceCheck = DateTimeOffset.MinValue;

  /// <summary>Creates the watcher. Inert without an instance identity or a coordinator.</summary>
  /// <param name="scopeFactory">Scopes the work coordinator the standby ledger is read through.</param>
  /// <param name="runControl">The run-control path that stands the instance down and resumes it.</param>
  /// <param name="hostLifetime">Stopped when a migration commits beneath this instance.</param>
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
      IHostApplicationLifetime hostLifetime,
      IServiceInstanceProvider instanceProvider,
      ILibraryVersionProvider versionProvider,
      IStartupAssessor assessor,
      ILogger<StandbyWatcher> logger,
      TimeProvider timeProvider,
      StartupPipelineRunner? pipelineRunner = null,
      StandbyWatcherOptions? options = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(runControl);
    ArgumentNullException.ThrowIfNull(hostLifetime);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _scopeFactory = scopeFactory;
    _runControl = runControl;
    _hostLifetime = hostLifetime;
    _instanceProvider = instanceProvider;
    _versionProvider = versionProvider;
    _assessor = assessor;
    _pipelineRunner = pipelineRunner;
    _options = options ?? new StandbyWatcherOptions();
    _logger = logger;
    _time = timeProvider;
  }

  /// <summary>
  /// The signal bus (#2022): a newer version online, and an instance that died or left, make the next tick ask the verdict
  /// again; this instance's own announcement is published on it. Null: only the slow cadence asks, and nothing is announced.
  /// </summary>
  public ISignalBus? Signals { get; init; }

  /// <summary>Reads the versions of the instances online, for this instance's announcement (#2022); null announces nothing.</summary>
  public IOnlineVersionReader? OnlineVersions { get; init; }

  /// <summary>This instance's application version, compared with the library version when announcing (#2022).</summary>
  public IApplicationVersionProvider? ApplicationVersion { get; init; }

  /// <summary>This instance's phase: it announces itself only once it serves (#2022); null announces nothing.</summary>
  public IWhizbangLifecycleState? Lifecycle { get; init; }

  /// <summary>Records why the instance stood down or resumed on its row's stage message (#2022); null records nothing.</summary>
  public IStartupStageReporter? Stages { get; init; }

  /// <summary>Releases the signal subscriptions, then the loop.</summary>
  public override void Dispose() {
    foreach (var subscription in Interlocked.Exchange(ref _subscriptions, null) ?? []) {
      subscription.Dispose();
    }
    base.Dispose();
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
    _subscribeOnce();
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

    if (!_standingDownAsObsolete && request is not null && _isBindingOnUs(request)) {
      LogEnteringStandby(_logger, request.RequestedBy, request.RequestedVersion);
      await _runControl.HaltAsync(RunHaltReason.StandDown, cancellationToken).ConfigureAwait(false);
      _standingByForHandshake = true;
      return;
    }

    await _announceOnceServingAsync(cancellationToken).ConfigureAwait(false);
    await _keepTheVerdictAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Asks the next tick to read the verdict again, whatever the slow cadence says (#2022).</summary>
  private ValueTask _askOnTheNextTick() {
    Volatile.Write(ref _askNow, 1);
    return ValueTask.CompletedTask;
  }

  private void _subscribeOnce() {
    if (Signals is not { } bus || _subscriptions is not null) {
      return;
    }
    _subscriptions = [
      bus.Subscribe<NewerVersionOnlineSignal>(_ => _askOnTheNextTick()),
      bus.Subscribe<InstanceDiedSignal>(_ => _askOnTheNextTick()),
      bus.Subscribe<InstanceLeavingSignal>(_ => _askOnTheNextTick()),
    ];
  }

  /// <summary>
  /// The verdict is not a startup-only fact: re-assess on the slow cadence, or at once when a signal asked. Stands down
  /// on StandDown and, once stood down for it, resumes when the verdict no longer says so (#2022).
  /// </summary>
  private async Task _keepTheVerdictAsync(CancellationToken cancellationToken) {
    var asked = Interlocked.Exchange(ref _askNow, 0) == 1;
    if (!_assessor.IsConfigured || (!asked && _time.GetUtcNow() - _lastObsolescenceCheck < _options.ObsolescenceInterval)) {
      return;
    }
    _lastObsolescenceCheck = _time.GetUtcNow();
    var assessment = await _assessor.AssessAsync(cancellationToken).ConfigureAwait(false);
    var standDown = assessment.Verdict == StartupVerdict.StandDown;
    if (standDown == _standingDownAsObsolete) {
      return;   // nothing changed: no churn, and each transition is logged once
    }
    if (standDown) {
      LogStandingDownAsObsolete(_logger, assessment.Reason);
      // Said on the row before the phase: the stage message names the newer version and where it was seen.
      await _sayAsync("standing down: " + assessment.Reason, cancellationToken).ConfigureAwait(false);
      await _runControl.HaltAsync(RunHaltReason.StandDown, cancellationToken).ConfigureAwait(false);
      _standingDownAsObsolete = true;
      return;
    }
    LogResumingAfterStandDown(_logger, assessment.Reason);
    await _reviveAsync(cancellationToken).ConfigureAwait(false);
    await _sayAsync("resumed: " + assessment.Reason, cancellationToken).ConfigureAwait(false);
    _standingDownAsObsolete = false;
    _announced = false;   // serving again: an older instance that resumed meanwhile must hear about this one
  }

  private async Task _sayAsync(string message, CancellationToken cancellationToken) {
    if (Stages?.Current is { } current) {
      await Stages.EnterAsync(current.Stage, message, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Once serving, announces this instance when the one version rule finds an older instance online (#2022). Asked once
  /// per time the instance comes to serve; a failed read or publish throws, and the next tick tries again.
  /// </summary>
  private async Task _announceOnceServingAsync(CancellationToken cancellationToken) {
    if (_announced || _standingDownAsObsolete || Signals is not { } bus || OnlineVersions is not { } fleet
        || Lifecycle?.Phase is not (LifecyclePhase.AcceptingCommands or LifecyclePhase.Running)) {
      return;
    }
    var own = new VersionSighting(_versionProvider.LibraryVersion, ApplicationVersion?.ApplicationVersion.Recorded,
      _instanceProvider.InstanceId);
    var older = UpgradeOrder.OlderThan(own, await fleet.ReadOnlineAsync(cancellationToken).ConfigureAwait(false));
    if (older.Count > 0) {
      await bus.PublishAsync(new NewerVersionOnlineSignal(), SignalTarget.Broadcast, cancellationToken).ConfigureAwait(false);
      LogAnnouncedNewerVersion(_logger, own.LibraryVersion, own.ApplicationVersion, older.Count);
    }
    _announced = true;
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
    // #2022: ordered by the one version rule.
    if (!UpgradeOrder.IsReadable(request.RequestedVersion)) {
      return false;
    }
    return UpgradeOrder.CompareLibrary(request.RequestedVersion, _versionProvider.LibraryVersion) is not <= 0;
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
      LogShuttingDownAfterCommit(_logger, reason);
      _hostLifetime.StopApplication();
      return;
    }

    // Rolled back, or the migrator died before changing anything: revival is not a second
    // pipeline — re-enter at Assess by re-running the re-entrant runner, then resume.
    // The path's resume (Resuming, then Running), not through AcceptingCommands: that rung exists for the window where
    // the read models are still being repaired, and this instance already crossed both barriers before standing by. The
    // schema and read-model gates are sticky-open; the stand-down closed only the run permit, which the resume reopens.
    LogReviving(_logger, reason);
    await _reviveAsync(cancellationToken).ConfigureAwait(false);
    _standingByForHandshake = false;
  }

  /// <summary>Re-enters the startup pipeline at Assess, then lifts the stand-down through the one run-control path.</summary>
  private async Task _reviveAsync(CancellationToken cancellationToken) {
    if (_pipelineRunner is not null) {
      _ = await _pipelineRunner.RunAsync(cancellationToken).ConfigureAwait(false);
    }
    await _runControl.ResumeAsync(RunHaltReason.StandDown, cancellationToken).ConfigureAwait(false);
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

  [LoggerMessage(EventId = 6, Level = LogLevel.Information,
    Message = "Resuming after standing down: nothing newer is recorded or online any more ({Reason})")]
  static partial void LogResumingAfterStandDown(ILogger logger, string reason);

  [LoggerMessage(EventId = 7, Level = LogLevel.Information,
    Message = "Newer version online: this instance (library {LibraryVersion}, application {ApplicationVersion}) is newer "
      + "than {Older} online instance(s); announcing it, and they stand down")]
  static partial void LogAnnouncedNewerVersion(ILogger logger, string? libraryVersion, string? applicationVersion, int older);
}
