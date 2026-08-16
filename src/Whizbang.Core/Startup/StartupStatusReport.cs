using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Startup;

/// <summary>
/// The status response every surface projects — minimal-API, FastEndpoints and HotChocolate all
/// serve this same shape, built by <see cref="StartupStatusReporter"/>, so the three surfaces
/// cannot drift apart in what they disclose.
/// </summary>
/// <param name="Instance">The process that answered — read from memory, exact and current.</param>
/// <param name="Fleet">Every live instance from the database, or why they cannot be seen.</param>
/// <docs>proposals/startup-pipeline#status</docs>
public sealed record StartupStatusReport(InstanceStatusSection Instance, FleetStatusSection Fleet);

/// <summary>The responder's own pipeline state.</summary>
/// <param name="InstanceId">This instance's id — the key that finds its row in the fleet section.</param>
/// <param name="ServiceName">The service this instance belongs to.</param>
/// <param name="Started">Whether the pipeline has begun. False is "not started", which is not an empty run.</param>
/// <param name="CurrentStep">The step executing right now, when one is.</param>
/// <param name="PipelineComplete">Whether the whole run has finished (non-blocking steps included).</param>
/// <param name="PipelineReady">Whether the blocking steps have drained without failure.</param>
/// <param name="Ready">The composite: blocking steps drained AND every readiness contributor answered.</param>
/// <param name="Steps">The run's steps in planned order with live status; absent before the run begins.</param>
/// <docs>proposals/startup-pipeline#status</docs>
public sealed record InstanceStatusSection(
  Guid? InstanceId, string? ServiceName, bool Started, string? CurrentStep,
  bool PipelineComplete, bool PipelineReady, bool Ready, IReadOnlyList<StepStatusEntry>? Steps);

/// <summary>One step of the current run, as every surface reports it.</summary>
/// <param name="Name">The step's declared name.</param>
/// <param name="Blocking">Whether the step gates readiness.</param>
/// <param name="Status">Where the step currently stands.</param>
/// <param name="DurationMs">How long it took, once finished.</param>
/// <param name="Outcome">What it did, once finished.</param>
/// <param name="Reason">Why — only when the host opted into reasons.</param>
/// <docs>proposals/startup-pipeline#status</docs>
public sealed record StepStatusEntry(
  string Name, bool Blocking, StartupStepStatus Status,
  double? DurationMs, StartupStepOutcome? Outcome, string? Reason);

/// <summary>The fleet section: live instances, or an honest statement of why they cannot be seen.</summary>
/// <param name="Available">Whether the fleet could be read.</param>
/// <param name="Reason">Why not, when it could not.</param>
/// <param name="Instances">The live instances, when it could.</param>
/// <docs>proposals/startup-pipeline#status</docs>
public sealed record FleetStatusSection(
  bool Available, string? Reason, IReadOnlyList<FleetStatusEntry>? Instances);

/// <summary>One live instance from the database.</summary>
/// <param name="InstanceId">The instance's id.</param>
/// <param name="ServiceName">The service it belongs to.</param>
/// <param name="HostName">Where it runs.</param>
/// <param name="LastSeenSecondsAgo">Seconds since its last heartbeat — freshness is per row, judged by the reader.</param>
/// <param name="Capabilities">What the instance currently holds — which one is the migrator, as a query.</param>
/// <docs>proposals/startup-pipeline#status</docs>
public sealed record FleetStatusEntry(
  Guid InstanceId, string ServiceName, string HostName, double LastSeenSecondsAgo,
  IReadOnlyList<string> Capabilities);

/// <summary>
/// Builds the <see cref="StartupStatusReport"/> every surface serves. One implementation carries
/// the proposal's constraints for all of them: honest degradation (not-started and
/// fleet-unavailable are stated conditions, never empty collections) and the information-disclosure
/// boundary (<c>reason</c> strings carry content the framework does not control and ride a
/// separate opt-in).
/// </summary>
/// <docs>proposals/startup-pipeline#status</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStatusReporterTests.cs</tests>
public static class StartupStatusReporter {

  /// <summary>Builds the two-section report from whatever is registered — every argument optional,
  /// because the surface must answer regardless of what the host wired.</summary>
  public static async Task<StartupStatusReport> BuildAsync(
      IStartupPipelineState? state,
      IStartupReadySignal? readySignal,
      IServiceInstanceProvider? instanceProvider,
      IStartupFleetStatusSource? fleetSource,
      bool includeReasons,
      CancellationToken cancellationToken) {
    return new StartupStatusReport(
      _projectInstance(state, readySignal, instanceProvider, includeReasons),
      await _projectFleetAsync(fleetSource, includeReasons, cancellationToken).ConfigureAwait(false));
  }

  private static InstanceStatusSection _projectInstance(
      IStartupPipelineState? state, IStartupReadySignal? readySignal,
      IServiceInstanceProvider? instanceProvider, bool includeReasons) {
    // Degrade honestly: no pipeline registered, or registered but not yet begun, is "not started"
    // — never an empty step list, because an empty list and a pipeline that has not begun must
    // not serialize identically.
    if (state is null || !state.HasRunStarted) {
      return new InstanceStatusSection(
        instanceProvider?.InstanceId, instanceProvider?.ServiceName,
        Started: false, CurrentStep: null,
        PipelineComplete: false, PipelineReady: false,
        Ready: readySignal?.IsReady ?? false, Steps: null);
    }

    var snapshot = state.SnapshotSteps();
    string? currentStep = null;
    var steps = new List<StepStatusEntry>(snapshot.Count);
    foreach (var step in snapshot) {
      if (step.Status == StartupStepStatus.Running) {
        currentStep = step.Name;
      }
      steps.Add(new StepStatusEntry(
        step.Name, step.Blocking, step.Status,
        step.Duration?.TotalMilliseconds, step.Outcome,
        includeReasons ? step.Reason : null));
    }

    return new InstanceStatusSection(
      instanceProvider?.InstanceId, instanceProvider?.ServiceName,
      Started: true, currentStep,
      state.IsComplete, state.IsReady,
      readySignal?.IsReady ?? false, steps);
  }

  private static async Task<FleetStatusSection> _projectFleetAsync(
      IStartupFleetStatusSource? source, bool includeReasons, CancellationToken cancellationToken) {
    if (source is null) {
      return new FleetStatusSection(
        Available: false,
        Reason: "no fleet status source registered — the storage driver supplies one",
        Instances: null);
    }

    try {
      var instances = await source.GetFleetAsync(cancellationToken).ConfigureAwait(false);
      var now = DateTimeOffset.UtcNow;
      var rows = new List<FleetStatusEntry>(instances.Count);
      foreach (var instance in instances) {
        rows.Add(new FleetStatusEntry(
          instance.InstanceId, instance.ServiceName, instance.HostName,
          Math.Max(0, (now - instance.LastHeartbeatAt).TotalSeconds),
          instance.Capabilities));
      }
      return new FleetStatusSection(Available: true, Reason: null, rows);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
    } catch (Exception ex) {
      // Unreachable is a stated condition, never an empty list — "no other instances" and
      // "cannot see the other instances" mean opposite things during an incident. The raw
      // failure text rides the same opt-in as step reasons: it is driver-authored content.
      return new FleetStatusSection(
        Available: false,
        Reason: includeReasons ? ex.Message : "fleet query failed",
        Instances: null);
    }
  }
}
