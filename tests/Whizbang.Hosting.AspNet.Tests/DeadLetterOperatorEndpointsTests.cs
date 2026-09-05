using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// v0.502 slice C.10 — operator HTTP endpoints for the internal DLQ.
/// Tests cover the five routes (/due, /{id}/retry, /{id}/hold, /{id}/give-up,
/// /scan-now) against a fake <see cref="IDeadLetterRecoveryService"/> so we
/// observe the public contract (status codes, response shape, service call
/// arguments) without touching Postgres.
/// </summary>
public class DeadLetterOperatorEndpointsTests {

  private sealed class FakeRecoveryService : IDeadLetterRecoveryService {
    public Task<IReadOnlyList<string>> GetPassedCampaignFingerprintsAsync(string generation, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<string>>([]);
    // Campaign surface (P1) — inert; operator endpoints do not drive campaigns.
    public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<UnstackedDeadLetter>>([]);
    public Task<int> RecordStacksAsync(IReadOnlyList<(Guid, Whizbang.Core.DeadLetters.StackIdentity)> entries, CancellationToken ct = default) => Task.FromResult(entries.Count);
    public Task<int> PruneStackHistoryAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
    public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) =>
      Task.CompletedTask;
    public Task<int> BeginTrickleWaveAsync(string fingerprint, string generation, int waveSize, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> CountWaveRequarantinesAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) => Task.FromResult(0);
    public List<HeldCohort> Cohorts { get; set; } = [];
    public List<(string Fp, TimeSpan Stagger)> CohortReleases { get; } = [];
    public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<HeldCohort>>([.. Cohorts]);
    public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(new CanaryVerdict(CanaryVerdictKind.Pass, 0, 0, 0));
    public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) {
      CohortReleases.Add((fingerprint, stagger));
      return Task.FromResult(42);
    }

    public int FetchDueCalls;
    public int? LastFetchDueMax;
    public IReadOnlyList<DeadLetterEntry> FetchDueResult { get; set; } = [];

    public readonly List<Guid> ScheduledIds = [];
    public readonly List<DateTimeOffset> ScheduledTimes = [];
    public readonly List<Guid> HeldIds = [];
    public readonly List<Guid> GaveUpIds = [];
    public readonly List<string> ResetGenerations = [];
    public int ResetResult { get; set; }

    public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
      FetchDueCalls++;
      LastFetchDueMax = maxCount;
      return Task.FromResult(FetchDueResult);
    }
    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) => Task.FromResult(true);
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) {
      HeldIds.Add(deadLetterId);
      return Task.CompletedTask;
    }
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) {
      GaveUpIds.Add(deadLetterId);
      return Task.CompletedTask;
    }
    public Task MarkDiscardedAsync(Guid deadLetterId, string note, CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
      ScheduledIds.Add(deadLetterId);
      ScheduledTimes.Add(nextAt);
      return Task.CompletedTask;
    }
    public Task<int> ResetForGenerationAsync(string currentGeneration, int staggerMinutes, CancellationToken ct = default) {
      ResetGenerations.Add(currentGeneration);
      return Task.FromResult(ResetResult);
    }
  }

  private sealed class FixedGenerationProvider(string gen) : IGenerationProvider {
    public string GetGeneration() => gen;
  }

  private static IHost _buildHost(FakeRecoveryService svc, IGenerationProvider? gen = null) {
    return new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddSingleton<IDeadLetterRecoveryService>(svc);
          s.AddSingleton<IGenerationProvider>(gen ?? new FixedGenerationProvider("whizbang/test-1"));
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangDeadLetterEndpoints());
        });
      })
      .Build();
  }

  [Test]
  public async Task GetDue_ReturnsEntriesAsJsonAsync() {
    var entry = new DeadLetterEntry(
      DeadLetterId: (Guid)TrackedGuid.NewMedo(),
      SourceTable: DeadLetterSourceTable.INBOX,
      SourceId: (Guid)TrackedGuid.NewMedo(),
      StreamId: null,
      MessageType: "TestMessage",
      FailureReason: MessageFailureReason.Throttled,
      AttemptsWhenDlq: 5,
      DeadLetteredAt: DateTimeOffset.UtcNow,
      RecoveryStatus: DeadLetterRecoveryStatus.Pending,
      RecoveryAttempts: 0,
      Generation: "whizbang/test-1");
    var svc = new FakeRecoveryService { FetchDueResult = [entry] };

    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.GetAsync("/whizbang/dlq/due?max=42");

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(svc.LastFetchDueMax).IsEqualTo(42);
    var body = await resp.Content.ReadAsStringAsync();
    await Assert.That(body).Contains(entry.DeadLetterId.ToString());
    await Assert.That(body).Contains(((int)MessageFailureReason.Throttled).ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  [Test]
  public async Task GetDue_DefaultMax_WhenQueryMissingAsync() {
    var svc = new FakeRecoveryService();
    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.GetAsync("/whizbang/dlq/due");

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(svc.LastFetchDueMax).IsEqualTo(200);
  }

  [Test]
  public async Task PostRetry_SchedulesIdForImmediateAttemptAsync() {
    var id = (Guid)TrackedGuid.NewMedo();
    var svc = new FakeRecoveryService();
    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync($"/whizbang/dlq/{id}/retry", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(svc.ScheduledIds).Contains(id);
    // nextAttemptAt should be ~now (UTC), within 5 seconds
    await Assert.That((DateTimeOffset.UtcNow - svc.ScheduledTimes[0]).TotalSeconds).IsLessThan(5);
  }

  [Test]
  public async Task PostHold_MarksHoldForReviewAsync() {
    var id = (Guid)TrackedGuid.NewMedo();
    var svc = new FakeRecoveryService();
    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync($"/whizbang/dlq/{id}/hold", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(svc.HeldIds).Contains(id);
  }

  [Test]
  public async Task PostGiveUp_MarksPermanentlyFailedAsync() {
    var id = (Guid)TrackedGuid.NewMedo();
    var svc = new FakeRecoveryService();
    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync($"/whizbang/dlq/{id}/give-up", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    await Assert.That(svc.GaveUpIds).Contains(id);
  }

  [Test]
  public async Task PostScanNow_UsesGenerationProviderWhenQueryMissingAsync() {
    var svc = new FakeRecoveryService { ResetResult = 17 };
    using var host = _buildHost(svc, new FixedGenerationProvider("whizbang/from-provider"));
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync("/whizbang/dlq/scan-now", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(svc.ResetGenerations).Contains("whizbang/from-provider");
    var body = await resp.Content.ReadAsStringAsync();
    await Assert.That(body).Contains("whizbang/from-provider");
    await Assert.That(body).Contains("17");
  }

  [Test]
  public async Task PostScanNow_HonorsExplicitGenerationFromQueryAsync() {
    var svc = new FakeRecoveryService { ResetResult = 3 };
    using var host = _buildHost(svc, new FixedGenerationProvider("whizbang/from-provider"));
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync("/whizbang/dlq/scan-now?generation=whizbang/v0.499", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(svc.ResetGenerations).Contains("whizbang/v0.499");
  }

  [Test]
  public async Task CustomPrefix_RoutesCorrectlyAsync() {
    var svc = new FakeRecoveryService();
    var host = new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddSingleton<IDeadLetterRecoveryService>(svc);
          s.AddSingleton<IGenerationProvider>(new FixedGenerationProvider("g"));
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangDeadLetterEndpoints("/admin/dlq"));
        });
      })
      .Build();
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.GetAsync("/admin/dlq/due");
    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(svc.FetchDueCalls).IsEqualTo(1);
  }

  // --- Route constraint behaviour -------------------------------------------
  // Each id route carries a {id:guid} constraint, so a malformed id is rejected by
  // routing and never reaches the handler. That makes the handlers' own
  // "bad id -> 400" guards defence in depth rather than a reachable path; these
  // tests pin the constraint that keeps them unreachable, so that removing it
  // would fail here rather than silently change the contract from 404 to 400.

  [Test]
  [Arguments("retry")]
  [Arguments("hold")]
  [Arguments("give-up")]
  public async Task PostWithMalformedId_IsRejectedByRoutingAsync(string action) {
    var svc = new FakeRecoveryService();

    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync($"/whizbang/dlq/not-a-guid/{action}", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    await Assert.That(svc.ScheduledIds).IsEmpty();
    await Assert.That(svc.HeldIds).IsEmpty();
    await Assert.That(svc.GaveUpIds).IsEmpty();
  }

  [Test]
  [Arguments("retry")]
  [Arguments("hold")]
  [Arguments("give-up")]
  public async Task PostWithEmptyId_IsRejectedByRoutingAsync(string action) {
    var svc = new FakeRecoveryService();

    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync($"/whizbang/dlq//{action}", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
  }

  [Test]
  public async Task GetCohorts_ListsHeldCohortsAsJsonAsync() {
    var svc = new FakeRecoveryService {
      Cohorts = [new("fp-abc123def45678", 5000, 34)],
    };
    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.GetAsync("/whizbang/dlq/cohorts");

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var body = await resp.Content.ReadAsStringAsync();
    await Assert.That(body).Contains("fp-abc123def45678")
      .Because("the cohort list is the operator's campaign overview — fingerprint, size, "
             + "and type spread per unit");
    await Assert.That(body).Contains("5000");
  }

  [Test]
  public async Task PostCohortRelease_ReleasesStaggered_AndReportsCountAsync() {
    var svc = new FakeRecoveryService();
    using var host = _buildHost(svc);
    await host.StartAsync();
    var client = host.GetTestClient();

    var resp = await client.PostAsync("/whizbang/dlq/cohorts/fp-abc123def45678/release?staggerMinutes=45", content: null);

    await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(svc.CohortReleases.Count).IsEqualTo(1);
    await Assert.That(svc.CohortReleases[0].Fp).IsEqualTo("fp-abc123def45678");
    await Assert.That(svc.CohortReleases[0].Stagger).IsEqualTo(TimeSpan.FromMinutes(45))
      .Because("operator release goes through the SAME staggered-eligibility path as the "
             + "campaigns — there is no firehose endpoint");
    var body = await resp.Content.ReadAsStringAsync();
    await Assert.That(body).Contains("42")
      .Because("the released count is the operator's receipt");
  }

}
