using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Source-genned JsonSerializerContext for the startup status response. Keeps the endpoint
/// AOT-compatible without reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StartupStatusEndpoints.StartupStatusResponse))]
internal partial class StartupStatusJsonContext : JsonSerializerContext {
}

/// <summary>
/// The opt-in startup status surface: <c>GET /whizbang/startup</c> (overridable), projecting
/// <see cref="IStartupPipelineState"/> into the two-section shape the proposal specifies —
/// <c>instance</c> (this process, from memory, exact and current) and <c>fleet</c> (every live
/// instance, from the database, each row only as fresh as its last heartbeat).
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> A status endpoint publishes internal state, and publishing is a decision the host
/// makes rather than inherits from a package reference. One explicit call mounts it.
/// </para>
/// <para>
/// <b>It inherits the host's authentication.</b> The mapping returns
/// <see cref="IEndpointConventionBuilder"/>, so <c>.RequireAuthorization(…)</c> /
/// <c>.RequireHost(…)</c> / <c>.AllowAnonymous()</c> chain as on any endpoint. The framework
/// contributes no authorization model here. One caution belongs to the host: the authentication in
/// front of this endpoint must not depend on Whizbang having started — a policy that resolves roles
/// from the database blocks on the migration the endpoint exists to report on.
/// </para>
/// <para>
/// <b>It does not share a failure domain with what it reports on.</b> Mapping registers the route
/// with <see cref="WhizbangAvailabilityExemptions"/>, so the availability gate never 503s it while
/// the schema initializes — a startup endpoint that cannot answer until startup finishes is
/// worthless precisely when it is wanted.
/// </para>
/// <para>
/// <b>Terse by default.</b> The default projection is entirely framework-authored content: step
/// names, states, durations. <c>reason</c> strings originate in exception messages — schema names,
/// constraint names, raw driver text — and are a separate opt-in (<c>includeReasons</c>), not a
/// verbosity dial.
/// </para>
/// </remarks>
/// <docs>proposals/startup-pipeline#status</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/StartupStatusEndpointsTests.cs</tests>
public static class StartupStatusEndpoints {

  /// <summary>
  /// Maps the startup status endpoint (default <c>/whizbang/startup</c>) and registers the route
  /// as exempt from the availability gate. Returns the convention builder so host auth chains.
  /// </summary>
  /// <param name="endpoints">The route builder to mount on.</param>
  /// <param name="pattern">The route. Namespaced under <c>/whizbang/</c> by default so one edge
  /// rule covers this endpoint and anything added beside it later; override freely.</param>
  /// <param name="includeReasons">Whether to include per-step <c>reason</c> strings and raw fleet
  /// failure text. Off by default — reasons carry content the framework does not control.</param>
  public static IEndpointConventionBuilder MapWhizbangStartupStatus(
      this IEndpointRouteBuilder endpoints,
      string pattern = "/whizbang/startup",
      bool includeReasons = false) {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentException.ThrowIfNullOrEmpty(pattern);

    // Self-exemption: the surface must not share a failure domain with what it reports on. The
    // availability gate 503s non-exempt paths while the schema initializes; forgetting this
    // registration is exactly the mistake the proposal says mapping must make impossible.
    endpoints.ServiceProvider.GetService<WhizbangAvailabilityExemptions>()?.Add(pattern);

    return endpoints.MapGet(pattern, http => _handleAsync(http, includeReasons));
  }

  private static async Task _handleAsync(HttpContext http, bool includeReasons) {
    var state = http.RequestServices.GetService<IStartupPipelineState>();
    var readySignal = http.RequestServices.GetService<IStartupReadySignal>();
    var instanceProvider = http.RequestServices.GetService<IServiceInstanceProvider>();

    var response = new StartupStatusResponse {
      Instance = _projectInstance(state, readySignal, instanceProvider, includeReasons),
      Fleet = await _projectFleetAsync(http, includeReasons).ConfigureAwait(false),
    };

    http.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(
      http.Response.Body, response,
      StartupStatusJsonContext.Default.StartupStatusResponse,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static InstanceSection _projectInstance(
      IStartupPipelineState? state, IStartupReadySignal? readySignal,
      IServiceInstanceProvider? instanceProvider, bool includeReasons) {
    // Degrade honestly: no pipeline registered, or registered but not yet begun, is "not started"
    // — never an empty step list, because an empty list and a pipeline that has not begun must
    // not serialize identically.
    if (state is null || !state.HasRunStarted) {
      return new InstanceSection {
        InstanceId = instanceProvider?.InstanceId,
        ServiceName = instanceProvider?.ServiceName,
        Started = false,
        Ready = readySignal?.IsReady ?? false,
      };
    }

    var snapshot = state.SnapshotSteps();
    string? currentStep = null;
    var steps = new List<StepEntry>(snapshot.Count);
    foreach (var step in snapshot) {
      if (step.Status == StartupStepStatus.Running) {
        currentStep = step.Name;
      }
      steps.Add(new StepEntry {
        Name = step.Name,
        Blocking = step.Blocking,
        Status = step.Status,
        DurationMs = step.Duration?.TotalMilliseconds,
        Outcome = step.Outcome,
        Reason = includeReasons ? step.Reason : null,
      });
    }

    return new InstanceSection {
      InstanceId = instanceProvider?.InstanceId,
      ServiceName = instanceProvider?.ServiceName,
      Started = true,
      CurrentStep = currentStep,
      PipelineComplete = state.IsComplete,
      PipelineReady = state.IsReady,
      Ready = readySignal?.IsReady ?? false,
      Steps = steps,
    };
  }

  private static async Task<FleetSection> _projectFleetAsync(HttpContext http, bool includeReasons) {
    var source = http.RequestServices.GetService<IStartupFleetStatusSource>();
    if (source is null) {
      return new FleetSection {
        Available = false,
        Reason = "no fleet status source registered — the storage driver supplies one",
      };
    }

    try {
      var instances = await source.GetFleetAsync(http.RequestAborted).ConfigureAwait(false);
      var now = DateTimeOffset.UtcNow;
      var rows = new List<FleetEntry>(instances.Count);
      foreach (var instance in instances) {
        rows.Add(new FleetEntry {
          InstanceId = instance.InstanceId,
          ServiceName = instance.ServiceName,
          HostName = instance.HostName,
          LastSeenSecondsAgo = Math.Max(0, (now - instance.LastHeartbeatAt).TotalSeconds),
        });
      }
      return new FleetSection { Available = true, Instances = rows };
    } catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) {
      throw;
    } catch (Exception ex) {
      // Unreachable is a stated condition, never an empty list — "no other instances" and
      // "cannot see the other instances" mean opposite things during an incident. The raw
      // failure text rides the same opt-in as step reasons: it is driver-authored content.
      return new FleetSection {
        Available = false,
        Reason = includeReasons ? ex.Message : "fleet query failed",
      };
    }
  }

  /// <summary>The status response: this instance from memory, the fleet from the database.</summary>
  public sealed class StartupStatusResponse {
    /// <summary>The process that answered this request — exact and current.</summary>
    public required InstanceSection Instance { get; init; }
    /// <summary>Every live instance, or an honest statement of why they cannot be seen.</summary>
    public required FleetSection Fleet { get; init; }
  }

  /// <summary>The responder's own pipeline state, read from memory.</summary>
  public sealed class InstanceSection {
    /// <summary>This instance's id — the key that finds its row in the fleet section.</summary>
    public Guid? InstanceId { get; init; }
    /// <summary>The service this instance belongs to.</summary>
    public string? ServiceName { get; init; }
    /// <summary>Whether the pipeline has begun. False is "not started", which is not an empty run.</summary>
    public required bool Started { get; init; }
    /// <summary>The step executing right now, when one is.</summary>
    public string? CurrentStep { get; init; }
    /// <summary>Whether the whole run has finished (non-blocking steps included).</summary>
    public bool PipelineComplete { get; init; }
    /// <summary>Whether the blocking steps have drained without failure.</summary>
    public bool PipelineReady { get; init; }
    /// <summary>The composite: blocking steps drained AND every readiness contributor answered.</summary>
    public bool Ready { get; init; }
    /// <summary>The run's steps in planned order with live status.</summary>
    public IReadOnlyList<StepEntry>? Steps { get; init; }
  }

  /// <summary>One step of the current run.</summary>
  public sealed class StepEntry {
    /// <summary>The step's declared name.</summary>
    public required string Name { get; init; }
    /// <summary>Whether the step gates readiness.</summary>
    public required bool Blocking { get; init; }
    /// <summary>Where the step currently stands.</summary>
    public required StartupStepStatus Status { get; init; }
    /// <summary>How long it took, once finished.</summary>
    public double? DurationMs { get; init; }
    /// <summary>What it did, once finished.</summary>
    public StartupStepOutcome? Outcome { get; init; }
    /// <summary>Why — only when the host opted into reasons.</summary>
    public string? Reason { get; init; }
  }

  /// <summary>The fleet section: live instances, or why they cannot be seen.</summary>
  public sealed class FleetSection {
    /// <summary>Whether the fleet could be read.</summary>
    public required bool Available { get; init; }
    /// <summary>Why not, when it could not.</summary>
    public string? Reason { get; init; }
    /// <summary>The live instances, when it could.</summary>
    public IReadOnlyList<FleetEntry>? Instances { get; init; }
  }

  /// <summary>One live instance from the database.</summary>
  public sealed class FleetEntry {
    /// <summary>The instance's id.</summary>
    public required Guid InstanceId { get; init; }
    /// <summary>The service it belongs to.</summary>
    public required string ServiceName { get; init; }
    /// <summary>Where it runs.</summary>
    public required string HostName { get; init; }
    /// <summary>Seconds since its last heartbeat — freshness is per row, judged by the reader.</summary>
    public required double LastSeenSecondsAgo { get; init; }
  }
}
