// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="OsvClient"/> against a stub of api.osv.dev: the requests it sends, how it
/// follows pages, and that every failure is a failed check rather than an empty answer.
/// </summary>
/// <tests>Whizbang.CLI/Audit/OsvClient.cs</tests>
public class OsvClientTests {
  private const string CORE = "SoftwareExtravaganza.Whizbang.Core";
  private const string DATA = "SoftwareExtravaganza.Whizbang.Data.Postgres";
  private const string USER_AGENT = "whizbang-cli/1.2.3";
  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

  private static readonly ResolvedPackage _core = new(CORE, "1.0.0");
  private static readonly ResolvedPackage _data = new(DATA, "1.0.0");

  private static OsvClient _client(StubOsvHandler handler, TimeProvider? time = null) =>
    new(handler, USER_AGENT, _timeout, time ?? TimeProvider.System);

  private static Func<StubOsvHandler.SentRequest, CancellationToken, Task<HttpResponseMessage>> _answers(
      Func<StubOsvHandler.SentRequest, string> json) =>
    (request, _) => Task.FromResult(StubOsvHandler.Json(json(request)));

  [Test]
  public async Task FindAdvisoriesAsync_AsksOsvAboutEachExactVersionInOneBatchAsync() {
    var handler = new StubOsvHandler();
    using var client = _client(handler);

    await client.FindAdvisoriesAsync([_core, _data], CancellationToken.None);

    await Assert.That(handler.Requests).Count().IsEqualTo(1);
    var request = handler.Requests[0];
    await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
    await Assert.That(request.Uri.ToString()).IsEqualTo("https://api.osv.dev/v1/querybatch");
    var queries = JsonDocument.Parse(request.Body!).RootElement.GetProperty("queries");
    await Assert.That(queries.GetArrayLength()).IsEqualTo(2);
    await Assert.That(queries[0].GetProperty("package").GetProperty("name").GetString()).IsEqualTo(CORE);
    await Assert.That(queries[0].GetProperty("package").GetProperty("ecosystem").GetString()).IsEqualTo("NuGet");
    await Assert.That(queries[0].GetProperty("version").GetString()).IsEqualTo("1.0.0");
    await Assert.That(queries[1].GetProperty("package").GetProperty("name").GetString()).IsEqualTo(DATA);
    await Assert.That(queries[0].TryGetProperty("page_token", out _)).IsFalse()
      .Because("a first page carries no token; sending an empty one is a different question");
  }

  [Test]
  public async Task FindAdvisoriesAsync_NoAdvisories_ReturnsNoneAndFetchesNothingElseAsync() {
    var handler = new StubOsvHandler();
    using var client = _client(handler);

    var matches = await client.FindAdvisoriesAsync([_core], CancellationToken.None);

    await Assert.That(matches).IsEmpty();
    await Assert.That(handler.Requests).Count().IsEqualTo(1);
  }

  [Test]
  public async Task FindAdvisoriesAsync_NoPackages_SendsNothingAsync() {
    var handler = new StubOsvHandler();
    using var client = _client(handler);

    var matches = await client.FindAdvisoriesAsync([], CancellationToken.None);

    await Assert.That(matches).IsEmpty();
    await Assert.That(handler.Requests).IsEmpty();
  }

  [Test]
  public async Task FindAdvisoriesAsync_Hit_FetchesTheFullRecordAsync() {
    // The batch answer carries only ids. Severity, CVE and the fixed version live in the record.
    var handler = new StubOsvHandler {
      Respond = _answers(r => r.Method == HttpMethod.Post
        ? """{"results":[{"vulns":[{"id":"GHSA-test-0001","modified":"2026-10-01T00:00:00Z"}]}]}"""
        : StubOsvHandler.Advisory("GHSA-test-0001", CORE, "CRITICAL", "CVE-2026-0001", ["1.0.1"])),
    };
    using var client = _client(handler);

    var matches = await client.FindAdvisoriesAsync([_core], CancellationToken.None);

    await Assert.That(handler.Requests[1].Method).IsEqualTo(HttpMethod.Get);
    await Assert.That(handler.Requests[1].Uri.ToString()).IsEqualTo("https://api.osv.dev/v1/vulns/GHSA-test-0001");
    await Assert.That(matches).Count().IsEqualTo(1);
    await Assert.That(matches[0].Package).IsEqualTo(_core);
    var advisory = matches[0].Advisory;
    await Assert.That(advisory.Id).IsEqualTo("GHSA-test-0001");
    await Assert.That(advisory.Summary).IsEqualTo("Summary of GHSA-test-0001");
    await Assert.That(advisory.Aliases).IsEquivalentTo(["CVE-2026-0001"]);
    await Assert.That(advisory.DatabaseSpecific!.Severity).IsEqualTo("CRITICAL");
    await Assert.That(advisory.Affected[0].Package!.Name).IsEqualTo(CORE);
    await Assert.That(advisory.Affected[0].Ranges[0].Events[1].Fixed).IsEqualTo("1.0.1");
  }

  [Test]
  public async Task FindAdvisoriesAsync_OneAdvisoryForTwoPackages_FetchesItOnceAndMatchesBothAsync() {
    var handler = new StubOsvHandler {
      Respond = _answers(r => r.Method == HttpMethod.Post
        ? """{"results":[{"vulns":[{"id":"GHSA-test-0002"}]},{"vulns":[{"id":"GHSA-test-0002"}]}]}"""
        : StubOsvHandler.Advisory("GHSA-test-0002", CORE)),
    };
    using var client = _client(handler);

    var matches = await client.FindAdvisoriesAsync([_core, _data], CancellationToken.None);

    await Assert.That(handler.Requests.Count(r => r.Method == HttpMethod.Get)).IsEqualTo(1);
    await Assert.That(matches.Select(m => m.Package)).IsEquivalentTo([_core, _data]);
  }

  [Test]
  public async Task FindAdvisoriesAsync_ResultWithANextPage_FollowsItForThatQueryOnlyAsync() {
    // OSV pages each result on its own. Stopping at the first page would drop every advisory on
    // the later ones, and re-asking for a query that is finished would repeat it.
    var batches = 0;
    var handler = new StubOsvHandler {
      Respond = _answers(r => {
        if (r.Method == HttpMethod.Get) {
          return StubOsvHandler.Advisory(r.Uri.Segments[^1], DATA);
        }
        return ++batches == 1
          ? """{"results":[{"vulns":[{"id":"GHSA-page-0001"}]},{"vulns":[{"id":"GHSA-page-0002"}],"next_page_token":"tok-2"}]}"""
          : """{"results":[{"vulns":[{"id":"GHSA-page-0003"}]}]}""";
      }),
    };
    using var client = _client(handler);

    var matches = await client.FindAdvisoriesAsync([_core, _data], CancellationToken.None);

    var second = JsonDocument.Parse(handler.Requests.Where(r => r.Method == HttpMethod.Post).ElementAt(1).Body!)
      .RootElement.GetProperty("queries");
    await Assert.That(second.GetArrayLength()).IsEqualTo(1);
    await Assert.That(second[0].GetProperty("package").GetProperty("name").GetString()).IsEqualTo(DATA);
    await Assert.That(second[0].GetProperty("version").GetString()).IsEqualTo("1.0.0");
    await Assert.That(second[0].GetProperty("page_token").GetString()).IsEqualTo("tok-2");
    await Assert.That(matches.Select(m => (m.Package.Id, m.Advisory.Id))).IsEquivalentTo([
      (CORE, "GHSA-page-0001"),
      (DATA, "GHSA-page-0002"),
      (DATA, "GHSA-page-0003"),
    ]);
  }

  [Test]
  public async Task FindAdvisoriesAsync_EveryRequest_IsAnonymousAndGoesOnlyToOsvAsync() {
    // GitHub's advisory API shows draft advisories to an authenticated maintainer. An audit that
    // sent a token there could report an unpublished advisory, so no request carries credentials
    // and none goes anywhere but OSV's published data.
    var batches = 0;
    var handler = new StubOsvHandler {
      Respond = _answers(r => {
        if (r.Method == HttpMethod.Get) {
          return StubOsvHandler.Advisory("GHSA-test-0003", CORE);
        }
        return ++batches == 1
          ? """{"results":[{"vulns":[{"id":"GHSA-test-0003"}],"next_page_token":"t"},{}]}"""
          : """{"results":[{}]}""";
      }),
    };
    using var client = _client(handler);

    await client.FindAdvisoriesAsync([_core, _data], CancellationToken.None);

    await Assert.That(handler.Requests.Count).IsEqualTo(3)
      .Because("a batch, its next page, and one record fetch: every kind of request the client makes");
    foreach (var request in handler.Requests) {
      await Assert.That(request.Uri.Host).IsEqualTo("api.osv.dev");
      await Assert.That(request.Uri.Scheme).IsEqualTo("https");
      await Assert.That(request.Headers.Authorization).IsNull();
      await Assert.That(request.Headers.Contains("Authorization")).IsFalse();
      await Assert.That(request.Headers.UserAgent.ToString()).IsEqualTo(USER_AGENT);
    }
  }

  [Test]
  public async Task BaseAddress_IsOsvNeverGitHubAsync() {
    await Assert.That(OsvClient.BaseAddress.ToString()).IsEqualTo("https://api.osv.dev/");
  }

  [Test]
  [Arguments(HttpStatusCode.ServiceUnavailable)]
  [Arguments(HttpStatusCode.InternalServerError)]
  [Arguments(HttpStatusCode.TooManyRequests)]
  public async Task FindAdvisoriesAsync_BatchAnswersWithAnError_FailsTheCheckAsync(HttpStatusCode status) {
    var handler = new StubOsvHandler { Respond = (_, _) => Task.FromResult(StubOsvHandler.Json("{}", status)) };
    using var client = _client(handler);

    var exception = await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core], CancellationToken.None));

    await Assert.That(exception!.Message).Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
  }

  [Test]
  public async Task FindAdvisoriesAsync_RecordFetchAnswersWithAnError_FailsTheCheckAsync() {
    // Knowing an advisory exists but not its severity is not a result the threshold can judge.
    var handler = new StubOsvHandler {
      Respond = (r, _) => Task.FromResult(r.Method == HttpMethod.Post
        ? StubOsvHandler.Json("""{"results":[{"vulns":[{"id":"GHSA-test-0004"}]}]}""")
        : StubOsvHandler.Json("{}", HttpStatusCode.BadGateway)),
    };
    using var client = _client(handler);

    var exception = await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core], CancellationToken.None));

    await Assert.That(exception!.Message).Contains("502");
    await Assert.That(exception.Message).Contains("GHSA-test-0004");
  }

  [Test]
  public async Task FindAdvisoriesAsync_OsvUnreachable_FailsTheCheckAsync() {
    var handler = new StubOsvHandler { Respond = (_, _) => throw new HttpRequestException("Name or service not known") };
    using var client = _client(handler);

    var exception = await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core], CancellationToken.None));

    await Assert.That(exception!.Message).Contains("api.osv.dev");
    await Assert.That(exception.InnerException).IsTypeOf<HttpRequestException>();
  }

  [Test]
  public async Task FindAdvisoriesAsync_OsvDoesNotAnswerInTime_FailsTheCheckInsteadOfHangingAsync() {
    // The handler moves the clock past the deadline and then waits for as long as it is let.
    // Only the deadline can end the wait, so a missing or ignored deadline hangs this test.
    var time = new FakeTimeProvider();
    var handler = new StubOsvHandler {
      Respond = async (_, cancellationToken) => {
        time.Advance(_timeout + TimeSpan.FromSeconds(1));
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return StubOsvHandler.Json("{}");
      },
    };
    using var client = _client(handler, time);

    var exception = await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core], CancellationToken.None));

    await Assert.That(exception!.Message).Contains("15 seconds");
  }

  [Test]
  public async Task FindAdvisoriesAsync_DeadlineCoversEveryPageNotEachRequestAsync() {
    // Ten seconds per request is under the deadline each time, but two of them are not. A deadline
    // per request would let a slow, paging answer run on indefinitely.
    var time = new FakeTimeProvider();
    var handler = new StubOsvHandler {
      Respond = async (r, cancellationToken) => {
        time.Advance(TimeSpan.FromSeconds(10));
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        return StubOsvHandler.Json("""{"results":[{"next_page_token":"more"}]}""");
      },
    };
    using var client = _client(handler, time);

    var exception = await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core], CancellationToken.None));

    await Assert.That(exception!.Message).Contains("15 seconds");
    await Assert.That(handler.Requests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task FindAdvisoriesAsync_CallerCancels_IsACancellationNotAFailedCheckAsync() {
    var handler = new StubOsvHandler();
    using var client = _client(handler);
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(() => client.FindAdvisoriesAsync([_core], canceled.Token));
  }

  [Test]
  [Arguments("<html>Service Unavailable</html>")]
  [Arguments("null")]
  public async Task FindAdvisoriesAsync_UnreadableBatchAnswer_FailsTheCheckAsync(string body) {
    var handler = new StubOsvHandler { Respond = _answers(_ => body) };
    using var client = _client(handler);

    await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core], CancellationToken.None));
  }

  [Test]
  public async Task FindAdvisoriesAsync_UnreadableRecord_FailsTheCheckAsync() {
    var handler = new StubOsvHandler {
      Respond = _answers(r => r.Method == HttpMethod.Post
        ? """{"results":[{"vulns":[{"id":"GHSA-test-0005"}]}]}"""
        : "null"),
    };
    using var client = _client(handler);

    var exception = await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core], CancellationToken.None));

    await Assert.That(exception!.Message).Contains("GHSA-test-0005");
  }

  [Test]
  public async Task FindAdvisoriesAsync_FewerResultsThanQueries_FailsTheCheckAsync() {
    // Results are matched to queries by position. A short answer cannot be matched, and reading
    // the missing ones as "no advisories" would be a guess.
    var handler = new StubOsvHandler { Respond = _answers(_ => """{"results":[{}]}""") };
    using var client = _client(handler);

    var exception = await Assert.ThrowsAsync<AuditCheckException>(() => client.FindAdvisoriesAsync([_core, _data], CancellationToken.None));

    await Assert.That(exception!.Message).Contains("1 result");
  }
}
