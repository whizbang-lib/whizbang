using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lineage;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Source-genned JsonSerializerContext for the apply-stack responses. Keeps the endpoints
/// AOT-compatible without reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApplyStackReport))]
[JsonSerializable(typeof(ApplyStackStreamsReport))]
internal partial class ApplyStackJsonContext : JsonSerializerContext {
}

/// <summary>
/// The opt-in apply-stack surface, minimal-API flavor: <c>GET {pattern}</c> serves the path
/// signatures (and the anchored flow view when <c>anchor</c> is given); <c>GET {pattern}/streams</c>
/// is the drill-in. Both build through <see cref="ApplyStackReporter"/>, shared with the
/// FastEndpoints and HotChocolate surfaces so the three cannot drift apart in what they disclose.
/// The VS Code extension's environment/local-API modes consume exactly this surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> The surface publishes event-type topology (never payloads), and publishing is a
/// decision the host makes rather than inherits from a package reference. One explicit call mounts
/// it, disabled otherwise.
/// </para>
/// <para>
/// <b>It inherits the host's authentication.</b> The mapping returns the route group's
/// <see cref="IEndpointConventionBuilder"/>, so <c>.RequireAuthorization(…)</c> /
/// <c>.RequireHost(…)</c> chain over both routes at once. The framework contributes no
/// authorization model here.
/// </para>
/// <para>
/// <b>Scope-filtered.</b> The <c>scope</c> query parameter narrows results by JSONB containment,
/// so a host can constrain a tenant-scoped caller to its own shapes — enforcing that the caller
/// may only pass its own scope is the host's auth policy's job.
/// </para>
/// <para>
/// Query parameters: <c>perspective</c> (association-registry filter), <c>scope</c> (JSON
/// containment), <c>max</c> (signature cap), <c>anchor</c> + <c>radius</c> + <c>branches</c>
/// (the flow view); drill-in adds repeated <c>step</c> parameters (the exact collapsed path) and
/// <c>limit</c>.
/// </para>
/// </remarks>
/// <docs>proposals/pre-destruction-seam#serving-the-view</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/ApplyStackEndpointsTests.cs</tests>
public static class ApplyStackEndpoints {

  /// <summary>
  /// Maps the apply-stack endpoints (default <c>/whizbang/apply-stacks</c> and
  /// <c>…/streams</c>) as one route group. Returns the group's convention builder so host auth
  /// chains over both routes.
  /// </summary>
  /// <param name="endpoints">The route builder to mount on.</param>
  /// <param name="pattern">The route prefix. Namespaced under <c>/whizbang/</c> by default so one
  /// edge rule covers this surface and anything mounted beside it; override freely.</param>
  public static IEndpointConventionBuilder MapWhizbangApplyStacks(
      this IEndpointRouteBuilder endpoints,
      string pattern = "/whizbang/apply-stacks") {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentException.ThrowIfNullOrEmpty(pattern);

    var group = endpoints.MapGroup(pattern);
    group.MapGet("/", _handleSignaturesAsync);
    group.MapGet("/streams", _handleStreamsAsync);
    return group;
  }

  private static async Task _handleSignaturesAsync(HttpContext http) {
    var report = await ApplyStackReporter.BuildAsync(
      http.RequestServices.GetService<IApplyStackQuery>(),
      _readOptions(http),
      anchorEventType: _readString(http, "anchor"),
      radius: _readInt(http, "radius", 3),
      maxBranchesPerColumn: _readInt(http, "branches", 10),
      http.RequestAborted).ConfigureAwait(false);

    http.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(
      http.Response.Body, report,
      ApplyStackJsonContext.Default.ApplyStackReport,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static async Task _handleStreamsAsync(HttpContext http) {
    var report = await ApplyStackReporter.BuildStreamsAsync(
      http.RequestServices.GetService<IApplyStackQuery>(),
      [.. http.Request.Query["step"].Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)],
      _readOptions(http),
      limit: _readInt(http, "limit", 100),
      http.RequestAborted).ConfigureAwait(false);

    http.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(
      http.Response.Body, report,
      ApplyStackJsonContext.Default.ApplyStackStreamsReport,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static ApplyStackQueryOptions _readOptions(HttpContext http) {
    var options = new ApplyStackQueryOptions {
      PerspectiveName = _readString(http, "perspective"),
      ScopeJson = _readString(http, "scope"),
    };
    return _readInt(http, "max", options.MaxSignatures) is var max && max != options.MaxSignatures
      ? options with { MaxSignatures = max }
      : options;
  }

  private static string? _readString(HttpContext http, string name) {
    var value = http.Request.Query[name].FirstOrDefault();
    return string.IsNullOrEmpty(value) ? null : value;
  }

  private static int _readInt(HttpContext http, string name, int fallback) =>
    int.TryParse(http.Request.Query[name].FirstOrDefault(), out var parsed) ? parsed : fallback;
}
