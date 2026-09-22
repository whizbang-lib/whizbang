using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Messaging;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Coverage-round tests for DeadLetterOperatorEndpoints. Covers the blank-fingerprint guard on
/// the cohort-release endpoint, which DeadLetterOperatorEndpointsTests does not exercise. The
/// remaining targeted lines (the three "bad id -> 400" handler guards and
/// _tryGetIdFromRoute's own "return false") are documented below as residue: a previous round
/// already established, and DeadLetterOperatorEndpointsTests already pins, that every route
/// reading those guards is mapped "/{id:guid}", so routing rejects a non-guid id with 404
/// before any handler runs -- there is no request shape that reaches the guard with an id that
/// Guid.TryParse would reject.
/// </summary>
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
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) => Task.CompletedTask;
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
}
