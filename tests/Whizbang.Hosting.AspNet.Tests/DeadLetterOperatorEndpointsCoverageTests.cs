using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Messaging;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Coverage-round tests for DeadLetterOperatorEndpoints: the blank-fingerprint guard on the
/// cohort-release endpoint, and the per-handler "bad id -&gt; 400" guards.
/// </summary>
/// <remarks>
/// The id guards cannot be reached over HTTP through the shipped map, because every route that
/// reads them is mapped "/{id:guid}" and routing answers 404 first. They are reached here by
/// invoking the mapped handler's own request delegate with route values the constraint would
/// have rejected -- which is the shape the guard exists for: the handlers are mounted on a
/// RouteGroupBuilder the caller gets back, so a consumer that remaps them, or middleware that
/// rewrites route values, can hand them an id the constraint never saw.
/// </remarks>
public class DeadLetterOperatorEndpointsCoverageTests {

  private sealed class FakeRecoveryService : IDeadLetterRecoveryService {
    public Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<UnstackedDeadLetter>>([]);
    public Task<int> RecordStacksAsync(IReadOnlyList<(Guid, Whizbang.Core.DeadLetters.StackIdentity)> entries, CancellationToken ct = default) =>
      Task.FromResult(entries.Count);
    public Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
    public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<HeldCohort>>([]);
    public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(new CanaryVerdict(CanaryVerdictKind.Pass, 0, 0, 0));

    public readonly List<(string Fp, TimeSpan Stagger)> CohortReleases = [];
    public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) {
      CohortReleases.Add((fingerprint, stagger));
      return Task.FromResult(42);
    }

    public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);
    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) => Task.FromResult(true);

    /// <summary>Every (operation, id) an endpoint acted on. Empty means no guard was bypassed.</summary>
    public readonly List<(string Operation, Guid Id)> ActedOn = [];

    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) {
      ActedOn.Add(("hold", deadLetterId));
      return Task.CompletedTask;
    }
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) {
      ActedOn.Add(("give-up", deadLetterId));
      return Task.CompletedTask;
    }
    public Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
      ActedOn.Add(("retry", deadLetterId));
      return Task.CompletedTask;
    }
    public Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) => Task.FromResult(0);
  }

  private static IHost _buildHost(FakeRecoveryService svc) {
    return new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddSingleton<IDeadLetterRecoveryService>(svc);
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangDeadLetterEndpoints());
        });
      })
      .Build();
  }

  // An operator releasing a cohort with a blank fingerprint would otherwise reach the recovery
  // service with nothing to match against. The guard is what turns that into a clear 400
  // instead of a call that matches nothing -- or, depending on the store, everything.
  [Test]
  public async Task PostCohortRelease_WithWhitespaceFingerprint_ReturnsBadRequestAsync() {
    var svc = new FakeRecoveryService();
    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    // %20 decodes to a single space: a non-empty route segment, so routing matches the
    // unconstrained {fingerprint} parameter and the handler's own IsNullOrWhiteSpace guard is
    // what has to catch it (an empty segment, by contrast, never reaches the handler at all).
    var resp = await client.PostAsync("/whizbang/dlq/cohorts/%20/release", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.BadRequest)
      .Because("a blank fingerprint identifies no cohort, so the request must be rejected before it reaches the recovery service");
    await Assert.That(svc.CohortReleases).IsEmpty()
      .Because("the guard has to short-circuit before any release is attempted");
  }

  /// <summary>
  /// Builds the host and hands back the route builder the endpoints were mapped onto, so a test
  /// can reach a handler's request delegate directly instead of through routing.
  /// </summary>
  private static async Task<(IHost Host, IEndpointRouteBuilder Routes)> _buildRoutedHostAsync(FakeRecoveryService svc) {
    IEndpointRouteBuilder? routes = null;
    var host = new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddSingleton<IDeadLetterRecoveryService>(svc);
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => {
            routes = e;
            e.MapWhizbangDeadLetterEndpoints();
          });
        });
      })
      .Build();
    await host.StartAsync();
    return (host, routes!);
  }

  private static RequestDelegate _handlerFor(IEndpointRouteBuilder routes, string routeSuffix) =>
    routes.DataSources
      .SelectMany(source => source.Endpoints)
      .OfType<RouteEndpoint>()
      .Single(endpoint => endpoint.RoutePattern.RawText!.EndsWith(routeSuffix, StringComparison.Ordinal))
      .RequestDelegate!;

  /// <summary>
  /// Three id shapes the route constraint would have rejected, one per id-taking endpoint: no id
  /// at all, an id that is not a guid, and an id that is not even a string. Each is a distinct
  /// arm of the handler's guard.
  /// </summary>
  [Test]
  [Arguments("/{id:guid}/retry", "absent", null)]
  [Arguments("/{id:guid}/hold", "text", "not-a-guid")]
  [Arguments("/{id:guid}/give-up", "non-string", 42)]
  public async Task IdTakingEndpoints_RejectAnIdTheRouteConstraintWouldHaveCaught_WithoutActingAsync(
      string routeSuffix, string shape, object? rawRouteValue) {
    var svc = new FakeRecoveryService();
    var (host, routes) = await _buildRoutedHostAsync(svc);
    using (host) {
      var http = new DefaultHttpContext();
      if (rawRouteValue is not null) {
        http.Request.RouteValues["id"] = rawRouteValue;
      }

      await _handlerFor(routes, routeSuffix)(http);

      await Assert.That(http.Response.StatusCode).IsEqualTo(StatusCodes.Status400BadRequest)
        .Because($"an id of shape '{shape}' identifies no dead letter, so the handler must reject it");
      await Assert.That(svc.ActedOn).IsEmpty()
        .Because("the guard exists so a malformed id never reaches the recovery service — "
               + "retrying, holding or giving up on Guid.Empty would hit whatever row owns it");
      await host.StopAsync();
    }
  }

  /// <summary>
  /// The same handlers act normally on a well-formed id, so the guard above is rejecting the id
  /// rather than refusing every direct invocation.
  /// </summary>
  [Test]
  [Arguments("/{id:guid}/retry", "retry")]
  [Arguments("/{id:guid}/hold", "hold")]
  [Arguments("/{id:guid}/give-up", "give-up")]
  public async Task IdTakingEndpoints_ActOnAWellFormedIdAsync(string routeSuffix, string operation) {
    var svc = new FakeRecoveryService();
    var (host, routes) = await _buildRoutedHostAsync(svc);
    using (host) {
      var id = Guid.NewGuid();
      var http = new DefaultHttpContext { RequestServices = host.Services };
      http.Request.RouteValues["id"] = id.ToString();

      await _handlerFor(routes, routeSuffix)(http);

      await Assert.That(http.Response.StatusCode).IsEqualTo(StatusCodes.Status204NoContent);
      await Assert.That(svc.ActedOn).IsEquivalentTo([(operation, id)]);
      await host.StopAsync();
    }
  }
}
