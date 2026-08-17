using FastEndpoints;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lineage;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>The apply-stack signatures/flow request, bound from the query string.</summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
public sealed class ApplyStackApiRequest {
  /// <summary>Restrict paths to this perspective's event types (association registry). Null = all.</summary>
  public string? Perspective { get; set; }

  /// <summary>JSONB containment filter on each event's scope. Null = no scope filter.</summary>
  public string? Scope { get; set; }

  /// <summary>Maximum signatures returned, heaviest first.</summary>
  public int Max { get; set; } = 200;

  /// <summary>Anchor event type for the flow view; null skips the flow.</summary>
  public string? Anchor { get; set; }

  /// <summary>Flow radius either side of the anchor.</summary>
  public int Radius { get; set; } = 3;

  /// <summary>Long-tail collapse threshold per flow column.</summary>
  public int Branches { get; set; } = 10;
}

/// <summary>
/// Base class for the apply-stack signatures/flow endpoint, FastEndpoints flavor. The consumer
/// declares the concrete endpoint — that is the opt-in, and it keeps the endpoint inside
/// FastEndpoints' own security model: <c>Roles()</c> / <c>Permissions()</c> /
/// <c>AllowAnonymous()</c> apply to it exactly as to any endpoint the host declares itself.
/// </summary>
/// <remarks>
/// The response is the same <see cref="ApplyStackReport"/> every surface serves, built by
/// <see cref="ApplyStackReporter"/> — the surfaces cannot drift apart in what they disclose:
/// event-type topology and counts, never payloads.
/// </remarks>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/WhizbangApplyStackEndpointBaseTests.cs</tests>
/// <example>
/// // The consumer's endpoint — declaring it IS the opt-in:
/// public sealed class ApplyStackEndpoint : WhizbangApplyStackEndpointBase {
///     public override void Configure() {
///         Get("/whizbang/apply-stacks");
///         Roles("ops");                       // FastEndpoints security, host's choice
///     }
/// }
/// </example>
public abstract class WhizbangApplyStackEndpointBase : Endpoint<ApplyStackApiRequest, ApplyStackReport> {

  /// <inheritdoc />
  public override async Task HandleAsync(ApplyStackApiRequest req, CancellationToken ct) {
    var report = await BuildReportAsync(req, ct);
    await Send.OkAsync(report, ct);
  }

  /// <summary>
  /// Builds the report from whatever the host registered. Exposed for the consumer's own hooks
  /// and for tests — the projection itself lives in <see cref="ApplyStackReporter"/>.
  /// </summary>
  protected Task<ApplyStackReport> BuildReportAsync(ApplyStackApiRequest req, CancellationToken ct) {
    ArgumentNullException.ThrowIfNull(req);
    return ApplyStackReporter.BuildAsync(
      HttpContext.RequestServices.GetService<IApplyStackQuery>(),
      new ApplyStackQueryOptions {
        PerspectiveName = req.Perspective,
        ScopeJson = req.Scope,
        MaxSignatures = req.Max,
      },
      req.Anchor,
      req.Radius,
      req.Branches,
      ct);
  }
}

/// <summary>The apply-stack drill-in request, bound from the query string.</summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
public sealed class ApplyStackStreamsApiRequest {
  /// <summary>The exact collapsed path, one <c>step</c> parameter per element.</summary>
  public List<string> Step { get; set; } = [];

  /// <summary>Restrict paths to this perspective's event types (association registry). Null = all.</summary>
  public string? Perspective { get; set; }

  /// <summary>JSONB containment filter on each event's scope. Null = no scope filter.</summary>
  public string? Scope { get; set; }

  /// <summary>Maximum stream ids returned.</summary>
  public int Limit { get; set; } = 100;
}

/// <summary>
/// Base class for the apply-stack drill-in endpoint ("which streams took this exact path"),
/// FastEndpoints flavor — same opt-in-by-declaration and security posture as
/// <see cref="WhizbangApplyStackEndpointBase"/>.
/// </summary>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/WhizbangApplyStackEndpointBaseTests.cs</tests>
public abstract class WhizbangApplyStackStreamsEndpointBase : Endpoint<ApplyStackStreamsApiRequest, ApplyStackStreamsReport> {

  /// <inheritdoc />
  public override async Task HandleAsync(ApplyStackStreamsApiRequest req, CancellationToken ct) {
    var report = await BuildReportAsync(req, ct);
    await Send.OkAsync(report, ct);
  }

  /// <summary>
  /// Builds the drill-in report from whatever the host registered — the projection lives in
  /// <see cref="ApplyStackReporter"/>.
  /// </summary>
  protected Task<ApplyStackStreamsReport> BuildReportAsync(ApplyStackStreamsApiRequest req, CancellationToken ct) {
    ArgumentNullException.ThrowIfNull(req);
    return ApplyStackReporter.BuildStreamsAsync(
      HttpContext.RequestServices.GetService<IApplyStackQuery>(),
      req.Step,
      new ApplyStackQueryOptions {
        PerspectiveName = req.Perspective,
        ScopeJson = req.Scope,
      },
      req.Limit,
      ct);
  }
}
