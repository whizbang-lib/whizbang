// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>
/// How a stood-down instance stays visible (#1992). The <c>Assess</c> verdict found the schema newer than this binary,
/// so nothing is applied: no bootstrap, no election, no migration pass, no reconcile, and the schema-ready gate stays
/// closed, which holds the data plane and keeps readiness failing while the process stays alive. What this adds is that
/// the instance is seen: it records why it stood down through the one recorder of its stage and phase, registers at
/// once, and keeps its row alive on the heartbeat's cadence until the host stops.
/// </summary>
/// <remarks>
/// <para>
/// The row is kept by <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>, the call the heartbeat itself makes, so a
/// stood-down instance appears to the fleet exactly as any registered instance does. Its stage stays Preflight and its
/// phase is <see cref="LifecyclePhase.StandingBy"/>, so it is never eligible for work. The heartbeat worker does not
/// run: it starts when the schema-ready gate opens, and for this instance it never does.
/// </para>
/// <para>
/// The tables exist because the database is newer than the binary; the registration runs against functions a newer
/// release installed. A beat that fails is logged and tried again on the next beat; a beat the registry refuses (the
/// instance was evicted) ends the beating, since an evicted instance id never becomes valid again, and the instance exits
/// through the one exit path (#2126) so its host restarts it under a new instance id.
/// </para>
/// </remarks>
/// <docs>operations/startup/rolling-upgrades#standing-down</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStandDownTests.cs</tests>
public sealed partial class StartupStandDown {
  private const string STANDING_DOWN = "standing down: ";

  private readonly IServiceScopeFactory _scopes;
  private readonly IServiceInstanceProvider _instance;
  private readonly IStartupStageReporter _stages;
  private readonly string? _libraryVersion;
  private readonly TimeSpan _beatInterval;
  private readonly TimeProvider _time;
  private readonly ILogger _logger;

  /// <summary>Creates the stand-down for one instance.</summary>
  /// <param name="scopes">Creates the scope each beat resolves the work coordinator from.</param>
  /// <param name="instance">The instance that stands down.</param>
  /// <param name="stages">The one recorder of the instance's stage and phase.</param>
  /// <param name="libraryVersion">This binary's library version, recorded with each beat; null when unknown.</param>
  /// <param name="beatInterval">How often the row is kept alive: the heartbeat's own interval.</param>
  /// <param name="timeProvider">The clock the beats wait on.</param>
  /// <param name="logger">Where a failed or refused beat is said.</param>
  public StartupStandDown(
      IServiceScopeFactory scopes,
      IServiceInstanceProvider instance,
      IStartupStageReporter stages,
      string? libraryVersion,
      TimeSpan beatInterval,
      TimeProvider timeProvider,
      ILogger<StartupStandDown> logger) {
    ArgumentNullException.ThrowIfNull(scopes);
    ArgumentNullException.ThrowIfNull(instance);
    ArgumentNullException.ThrowIfNull(stages);
    ArgumentNullException.ThrowIfNull(timeProvider);
    _scopes = scopes;
    _instance = instance;
    _stages = stages;
    _libraryVersion = libraryVersion;
    _beatInterval = beatInterval;
    _time = timeProvider;
    _logger = logger ?? (ILogger)NullLogger.Instance;
  }

  /// <summary>
  /// Raised after each beat, with whether it was accepted, once the wait for the next beat has been scheduled: a
  /// monitor can count the beats, and a test driving a fake clock can advance it as soon as it hears.
  /// </summary>
  /// <docs>operations/startup/rolling-upgrades#standing-down</docs>
  public event Action<bool>? BeatRecorded;

  /// <summary>
  /// The application version each beat records beside the library version (#2027), as recorded
  /// (<see cref="Whizbang.Core.Versioning.ApplicationVersion.Recorded"/>); null when it is unknown.
  /// </summary>
  public string? ApplicationVersion { get; init; }

  /// <summary>
  /// The run-control path the stand-down takes (#2045), or <see langword="null"/> in a host without the worker pipeline,
  /// where only the phase is recorded.
  /// </summary>
  public IWhizbangRunController? RunControl { get; init; }

  /// <summary>
  /// The one exit path (#2126) a refused beat takes: an evicted stood-down instance exits with
  /// <see cref="ExitCause.ClusterEviction"/> so its host restarts it under a new instance id. <see langword="null"/> in a
  /// host without one, where the beating only ends.
  /// </summary>
  public IWhizbangInstanceExit? InstanceExit { get; init; }

  /// <summary>
  /// The liveness window each beat declares (#2027): the one liveness rule judges a stood-down instance by it, so it is
  /// derived from this beat's own cadence the way the heartbeat worker derives its own (three beats; no slow cadence).
  /// </summary>
  public int LivenessWindowSeconds => Whizbang.Core.Workers.HeartbeatLivenessThreshold.StaleThresholdSeconds(
    new Whizbang.Core.Workers.HeartbeatWorkerOptions {
      IntervalSeconds = (int)Math.Ceiling(_beatInterval.TotalSeconds),
      LivenessSourceMode = Whizbang.Core.Workers.HeartbeatLivenessSourceMode.HeartbeatTableOnly,
    });

  /// <summary>
  /// Builds the stand-down from a host's services: the instance identity (none means nothing to register as, and
  /// <see langword="null"/> is returned), the stage recorder, the library version, the heartbeat interval and the clock.
  /// </summary>
  /// <param name="services">The host's root services.</param>
  /// <returns>The stand-down, or <see langword="null"/> when the host has no instance identity.</returns>
  public static StartupStandDown? FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.GetService<IServiceInstanceProvider>() is not { } instance) {
      return null;
    }
    var interval = services.GetService<IOptions<HeartbeatWorkerOptions>>()?.Value.IntervalSeconds
      ?? new HeartbeatWorkerOptions().IntervalSeconds;
    return new StartupStandDown(
      services.GetRequiredService<IServiceScopeFactory>(),
      instance,
      services.GetService<IStartupStageReporter>() ?? NullStartupStageReporter.Instance,
      services.GetService<ILibraryVersionProvider>()?.LibraryVersion,
      TimeSpan.FromSeconds(interval),
      services.GetService<TimeProvider>() ?? TimeProvider.System,
      services.GetService<ILogger<StartupStandDown>>()!) {
      ApplicationVersion = services.GetService<Whizbang.Core.Versioning.IApplicationVersionProvider>()?.ApplicationVersion.Recorded,
      RunControl = services.GetService<IWhizbangRunController>(),
      InstanceExit = services.GetService<IWhizbangInstanceExit>(),
    };
  }

  /// <summary>
  /// Records the stand-down: the stage stays Preflight, its message names the reason (the binary's version and the
  /// schema's), and the phase is standing by. Held by the recorder until the instance's row exists.
  /// </summary>
  /// <param name="assessment">The stand-down verdict.</param>
  /// <param name="cancellationToken">Canceled when the host stops.</param>
  /// <returns>A task that completes once the state is recorded or held.</returns>
  public async Task RecordAsync(StartupAssessment assessment, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(assessment);
    LogStandingDown(_logger, assessment.Reason);
    await _stages.EnterAsync(StartupStage.Preflight, STANDING_DOWN + assessment.Reason, cancellationToken).ConfigureAwait(false);
    if (RunControl is { } runControl) {
      // #2045: the same path as a stand-down while running, a pause or a stop. At Preflight nothing is held, so it
      // closes the permit and writes the phase.
      await runControl.HaltAsync(RunHaltReason.StandDown, cancellationToken).ConfigureAwait(false);
    } else {
      // A host without the worker pipeline has no permit, no held work and no roles: only the report applies.
      await _stages.EnterPhaseAsync(LifecyclePhase.StandingBy, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Registers the instance at once and keeps its row alive on the heartbeat's cadence until <paramref name="stop"/>
  /// is canceled or the registry refuses it. Each accepted beat writes whatever the stage recorder holds.
  /// </summary>
  /// <param name="stop">Canceled when the host stops.</param>
  /// <returns>A task that completes when the beating ends; it never faults.</returns>
  public async Task KeepVisibleAsync(CancellationToken stop) {
    try {
      while (await _beatAsync(stop).ConfigureAwait(false) is { } accepted) {
        var wait = Task.Delay(_beatInterval, _time, stop);
        BeatRecorded?.Invoke(accepted);
        await wait.ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      // Only the host stopping cancels here (a beat that failed in any other way was counted as failed): the row ages
      // out of the fleet the way any stopped instance's does.
    }
  }

  /// <summary>One beat: whether it was accepted, or null when beating should end.</summary>
  private async Task<bool?> _beatAsync(CancellationToken stop) {
    bool accepted;
    try {
      await using var scope = _scopes.CreateAsyncScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator?.RecordsInstanceTransitions != true) {
        return null;   // this host keeps no instance rows: there is nothing to be visible in
      }
      accepted = await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(
        _instance.InstanceId, _instance.ServiceName, _instance.HostName, _instance.ProcessId,
        LibraryVersion: _libraryVersion, StaleThresholdSeconds: LivenessWindowSeconds,
        ApplicationVersion: ApplicationVersion), stop).ConfigureAwait(false);
    }
#pragma warning disable CA1031 // logged with what it costs, and retried on the next beat
    catch (Exception ex) when (!stop.IsCancellationRequested) {
#pragma warning restore CA1031
      LogBeatFailed(_logger, ex, _instance.InstanceId);
      return false;
    }
    if (!accepted) {
      if (InstanceExit is { } exit) {
        // #2126: nothing keeps running sidelined. The exit's one entry states the eviction.
        exit.RequestExit(new InstanceExitRequest(ExitCause.ClusterEviction,
          "the heartbeat of this stood-down instance was refused: the fleet evicted it (a peer reaped it and tombstoned it)"));
      } else {
        LogEvicted(_logger, _instance.InstanceId);
      }
      return null;
    }
    await _stages.InstanceRegisteredAsync(stop).ConfigureAwait(false);
    return true;
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Standing down: {Reason}. Nothing is applied to the schema, the data plane is held and readiness stays failing; "
      + "the instance stays registered until the host stops")]
  static partial void LogStandingDown(ILogger logger, string reason);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "Registering stood-down instance {InstanceId} failed; until a later beat lands, the fleet cannot see it or "
      + "why it stood down")]
  static partial void LogBeatFailed(ILogger logger, Exception ex, Guid instanceId);

  [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
    Message = "Stood-down instance {InstanceId} was evicted by the registry; it stops registering and is no longer visible to the fleet")]
  static partial void LogEvicted(ILogger logger, Guid instanceId);
}
