// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net;
using System.Text;
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
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// #2588: the opt-in operator endpoints for integrity exclusions. A body becomes an <see cref="IntegrityExclusionRequest"/>
/// or <see cref="IntegrityUnexclusionRequest"/>; a call applied at once or a dry run answers 200 with its outcomes, a
/// queued one 202 with its request id, and a refused one 400 with the reason; the summary answers per origin. The route
/// group is returned so the host can require authorization, as with the dead-letter endpoints.
/// </summary>
/// <code-under-test>src/Whizbang.Hosting.AspNet/IntegrityExclusionEndpoints.cs</code-under-test>
public class IntegrityExclusionEndpointsTests {
  private const string PREFIX = "/whizbang/integrity/exclusions";
  private static readonly Guid _event = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");
  private static readonly Guid _origin = Guid.Parse("0199a1b2-0000-7000-8000-0000000000aa");

  [Test]
  public async Task Post_ADryRunSelection_ReachesTheApi_AndAnswersOkWithTheOutcomesAsync() {
    var api = new FakeExclusions { Result = new IntegrityExclusionResult { DryRun = true, Outcomes = [new(_origin, "marked", 3)] } };
    using var host = await _startAsync(api);

    var response = await _postAsync(host, PREFIX, $$$"""
      {"reason":"pre-1.0 history","requestedBy":"operator-a","dryRun":true,
       "selection":{"originServiceId":"{{{_origin}}}","eventTypes":["Contracts.A"],"from":"2026-10-01T00:00:00Z",
                    "until":"2026-11-01T00:00:00Z","unresolved":"All"}}
      """);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var request = api.Excludes.Single();
    await Assert.That(request.DryRun).IsTrue();
    await Assert.That(request.Selection!.OriginServiceId).IsEqualTo(_origin);
    await Assert.That(request.Selection.EventTypes).IsEquivalentTo(["Contracts.A"]);
    await Assert.That(request.Selection.From).IsEqualTo(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    await Assert.That(request.Selection.Until).IsEqualTo(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    await Assert.That(request.Selection.Unresolved).IsEqualTo(IntegrityUnresolvedKinds.All);
    await Assert.That(await response.Content.ReadAsStringAsync()).Contains("\"outcome\":\"marked\"").And.Contains("\"dryRun\":true");
  }

  [Test]
  public async Task Post_ByIds_Queued_AnswersAcceptedWithTheRequestId_AndASelectionWithoutKindsAsksForAnyEventAsync() {
    var request = Guid.Parse("0199a1b2-0000-7000-8000-0000000000bb");
    var api = new FakeExclusions { Result = new IntegrityExclusionResult { Queued = true, RequestId = request } };
    using var host = await _startAsync(api);

    var byIds = await _postAsync(host, PREFIX, $$"""{"reason":"r","requestedBy":"o","eventIds":["{{_event}}"]}""");
    var bySelection = await _postAsync(host, PREFIX, """{"reason":"r","requestedBy":"o","selection":{}}""");

    await Assert.That(byIds.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    await Assert.That(bySelection.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    await Assert.That(await byIds.Content.ReadAsStringAsync()).Contains(request.ToString());
    await Assert.That(api.Excludes[0].EventIds).IsEquivalentTo([_event]);
    await Assert.That(api.Excludes[0].DryRun).IsFalse();
    await Assert.That(api.Excludes[1].Selection!.Unresolved).IsEqualTo(IntegrityUnresolvedKinds.None);
  }

  [Test]
  public async Task Post_ARefusedOrUnreadableRequest_AnswersBadRequestAsync() {
    var api = new FakeExclusions { Throw = new ArgumentException("A reason is required.") };
    using var host = await _startAsync(api);

    var refused = await _postAsync(host, PREFIX, "null");
    var unreadable = await _postAsync(host, PREFIX, "{not json");
    var unmarkRefused = await _postAsync(host, PREFIX + "/unmark", "null");
    var unmarkUnreadable = await _postAsync(host, PREFIX + "/unmark", "{not json");

    await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("A reason is required.");
    await Assert.That(api.Excludes.Single().Reason).IsEqualTo("").Because("a null body reaches the API as an empty request");
    await Assert.That(unreadable.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(unmarkRefused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(api.Unexcludes.Single().RequestedBy).IsEqualTo("");
    await Assert.That(unmarkUnreadable.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
  }

  [Test]
  public async Task PostUnmark_ReachesTheApi_AndAnswersWithTheOutcomesAsync() {
    var request = Guid.Parse("0199a1b2-0000-7000-8000-0000000000cc");
    var api = new FakeExclusions { Result = new IntegrityExclusionResult { Outcomes = [new(_origin, "unmarked", 1)] } };
    using var host = await _startAsync(api);

    var response = await _postAsync(host, PREFIX + "/unmark", $$"""
      {"reason":"the fix shipped","requestedBy":"operator-a","eventIds":["{{_event}}"],"requestId":"{{request}}",
       "originServiceId":"{{_origin}}","eventTypes":["Contracts.A"]}
      """);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    var unmark = api.Unexcludes.Single();
    await Assert.That(unmark.Reason).IsEqualTo("the fix shipped");
    await Assert.That(unmark.EventIds).IsEquivalentTo([_event]);
    await Assert.That(unmark.RequestId).IsEqualTo(request);
    await Assert.That(unmark.OriginServiceId).IsEqualTo(_origin);
    await Assert.That(unmark.EventTypes).IsEquivalentTo(["Contracts.A"]);
  }

  [Test]
  public async Task Get_AnswersTheSummary_ForEveryOriginOrOne_AndRefusesAnOriginThatIsNotAnIdAsync() {
    var api = new FakeExclusions();
    using var host = await _startAsync(api, "/ops/exclusions");
    var client = host.GetTestClient();

    var all = await client.GetAsync("/ops/exclusions");
    var one = await client.GetAsync($"/ops/exclusions?origin={_origin}");
    var bad = await client.GetAsync("/ops/exclusions?origin=nope");

    await Assert.That(all.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(one.StatusCode).IsEqualTo(HttpStatusCode.OK);
    await Assert.That(await all.Content.ReadAsStringAsync()).Contains("\"excluded\":4").And.Contains("\"openRequests\":2");
    await Assert.That(api.Summaries).IsEquivalentTo(new List<Guid?> { null, _origin });
    await Assert.That(bad.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
  }

  [Test]
  public async Task Map_RejectsANullBuilderOrAnEmptyPrefixAsync() {
    await using var services = new ServiceCollection().AddRouting().BuildServiceProvider();

    await Assert.That(() => ((IEndpointRouteBuilder)null!).MapWhizbangIntegrityExclusionEndpoints()).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new RouteBuilderStub(services).MapWhizbangIntegrityExclusionEndpoints("")).ThrowsExactly<ArgumentException>();
  }

  private static async Task<HttpResponseMessage> _postAsync(IHost host, string path, string json) {
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    return await host.GetTestClient().PostAsync(path, content);
  }

  private static async Task<IHost> _startAsync(FakeExclusions api, string prefix = PREFIX) {
    var host = new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddSingleton<IIntegrityExclusions>(api);
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangIntegrityExclusionEndpoints(prefix));
        });
      })
      .Build();
    await host.StartAsync();
    return host;
  }

  private sealed class RouteBuilderStub(IServiceProvider services) : IEndpointRouteBuilder {
    public IServiceProvider ServiceProvider { get; } = services;
    public ICollection<EndpointDataSource> DataSources { get; } = [];
    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
  }

  private sealed class FakeExclusions : IIntegrityExclusions {
    public List<IntegrityExclusionRequest> Excludes { get; } = [];
    public List<IntegrityUnexclusionRequest> Unexcludes { get; } = [];
    public List<Guid?> Summaries { get; } = [];
    public IntegrityExclusionResult Result { get; init; } = new();
    public Exception? Throw { get; init; }

    public Task<IntegrityExclusionResult> ExcludeAsync(IntegrityExclusionRequest request, CancellationToken cancellationToken = default) {
      Excludes.Add(request);
      return Throw is null ? Task.FromResult(Result) : Task.FromException<IntegrityExclusionResult>(Throw);
    }

    public Task<IntegrityExclusionResult> UnexcludeAsync(IntegrityUnexclusionRequest request, CancellationToken cancellationToken = default) {
      Unexcludes.Add(request);
      return Throw is null ? Task.FromResult(Result) : Task.FromException<IntegrityExclusionResult>(Throw);
    }

    public Task<IntegrityExclusionSummary> SummarizeAsync(Guid? originServiceId = null, CancellationToken cancellationToken = default) {
      Summaries.Add(originServiceId);
      return Task.FromResult(new IntegrityExclusionSummary {
        Origins = [new IntegrityExclusionOriginSummary(_origin, 4, 1, 0, null)],
        OpenRequests = 2,
      });
    }
  }
}
