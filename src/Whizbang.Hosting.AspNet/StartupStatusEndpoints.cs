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
[JsonSerializable(typeof(StartupStatusReport))]
internal partial class StartupStatusJsonContext : JsonSerializerContext {
}

/// <summary>
/// The opt-in startup status surface: <c>GET /whizbang/startup</c> (overridable), serving the
/// two-section <see cref="StartupStatusReport"/> the proposal specifies — <c>instance</c> (this
/// process, from memory, exact and current) and <c>fleet</c> (every live instance, from the
/// database, each row only as fresh as its last heartbeat). The projection itself lives in
/// <see cref="StartupStatusReporter"/>, shared with the FastEndpoints and HotChocolate surfaces
/// so the three cannot drift apart in what they disclose.
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
    var services = http.RequestServices;
    var report = await StartupStatusReporter.BuildAsync(
      services.GetService<IStartupPipelineState>(),
      services.GetService<IStartupReadySignal>(),
      services.GetService<IServiceInstanceProvider>(),
      services.GetService<IStartupFleetStatusSource>(),
      includeReasons,
      http.RequestAborted).ConfigureAwait(false);

    http.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(
      http.Response.Body, report,
      StartupStatusJsonContext.Default.StartupStatusReport,
      http.RequestAborted).ConfigureAwait(false);
  }
}
