using FastEndpoints;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Transports.FastEndpoints;

/// <summary>
/// Base class for the startup status endpoint, FastEndpoints flavor. The consumer declares the
/// concrete endpoint — that is the opt-in, and it keeps the endpoint inside FastEndpoints' own
/// security model: <c>Roles()</c> / <c>Permissions()</c> / <c>AllowAnonymous()</c> and global
/// preprocessors apply to it exactly as to any endpoint the host declares itself.
/// </summary>
/// <remarks>
/// <para>
/// The response is the same two-section <see cref="StartupStatusReport"/> every surface serves,
/// built by <see cref="StartupStatusReporter"/> — the surfaces cannot drift apart in what they
/// disclose. <c>reason</c> strings ride the <see cref="IncludeReasons"/> opt-in, not a verbosity
/// dial, because they carry content the framework does not control.
/// </para>
/// <para>
/// One caution stays with the host: the authentication in front of this endpoint must not resolve
/// roles from the database, or it blocks on the very migration the endpoint exists to report on.
/// And if the availability gate is active, the chosen route must be exempt — with the ASP.NET
/// hosting package present, register it via <c>WhizbangAvailabilityExemptions</c>.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status</docs>
/// <tests>tests/Whizbang.Transports.FastEndpoints.Tests/Unit/WhizbangStartupStatusEndpointBaseTests.cs</tests>
/// <example>
/// // The consumer's endpoint — declaring it IS the opt-in:
/// public sealed class StartupStatusEndpoint : WhizbangStartupStatusEndpointBase {
///     public override void Configure() {
///         Get("/whizbang/startup");
///         Roles("ops");                       // FastEndpoints security, host's choice
///     }
/// }
/// </example>
public abstract class WhizbangStartupStatusEndpointBase : EndpointWithoutRequest<StartupStatusReport> {

  /// <summary>
  /// Whether per-step <c>reason</c> strings and raw fleet failure text are included. Off by
  /// default — reasons originate in exception messages (schema names, constraint names, raw
  /// driver text) and are a separate opt-in level, not a verbosity dial.
  /// </summary>
  protected virtual bool IncludeReasons => false;

  /// <inheritdoc />
  public override async Task HandleAsync(CancellationToken ct) {
    var report = await BuildReportAsync(ct);
    await Send.OkAsync(report, ct);
  }

  /// <summary>
  /// Builds the report from whatever the host registered. Exposed for the consumer's own hooks
  /// and for tests — the projection itself lives in <see cref="StartupStatusReporter"/>.
  /// </summary>
  protected Task<StartupStatusReport> BuildReportAsync(CancellationToken ct) {
    var services = HttpContext.RequestServices;
    return StartupStatusReporter.BuildAsync(
      services.GetService<IStartupPipelineState>(),
      services.GetService<IStartupReadySignal>(),
      services.GetService<IServiceInstanceProvider>(),
      services.GetService<IStartupFleetStatusSource>(),
      IncludeReasons,
      ct);
  }
}
