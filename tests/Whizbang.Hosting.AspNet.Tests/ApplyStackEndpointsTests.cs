#pragma warning disable CA1707

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lineage;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// The minimal-API apply-stack surface. Locks the surface's constraints: opt-in mounting under one
/// route group (host auth chains over both routes), honest degradation when no driver query is
/// registered, the flow view appearing only when an anchor is requested, filter passthrough, and
/// the drill-in's repeated <c>step</c> binding.
/// </summary>
/// <code-under-test>src/Whizbang.Hosting.AspNet/ApplyStackEndpoints.cs</code-under-test>
public class ApplyStackEndpointsTests {

  private sealed class FixedQuery(IReadOnlyList<ApplyPathSignature> signatures, IReadOnlyList<Guid>? streams = null) : IApplyStackQuery {
    public ApplyStackQueryOptions? SeenOptions { get; private set; }
    public IReadOnlyList<string>? SeenPath { get; private set; }
    public int SeenLimit { get; private set; }

    public Task<IReadOnlyList<ApplyPathSignature>> GetPathSignaturesAsync(
        ApplyStackQueryOptions options, CancellationToken cancellationToken = default) {
      SeenOptions = options;
      return Task.FromResult(signatures);
    }

    public Task<IReadOnlyList<Guid>> GetStreamsForPathAsync(
        IReadOnlyList<string> path, ApplyStackQueryOptions options, int limit,
        CancellationToken cancellationToken = default) {
      SeenOptions = options;
      SeenPath = path;
      SeenLimit = limit;
      return Task.FromResult(streams ?? []);
    }
  }

  private static IHost _buildHost(IApplyStackQuery? query, string pattern = "/whizbang/apply-stacks") {
    return new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          if (query is not null) {
            s.AddSingleton(query);
          }
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangApplyStacks(pattern));
        });
      })
      .Build();
  }

  private static ApplyPathSignature _sig(long streams, params string[] path) =>
    new(path, streams, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  [Test]
  public async Task Signatures_NoQueryRegistered_ReportsUnavailableWithReasonAsync() {
    using var host = _buildHost(query: null);
    await host.StartAsync();

    var body = await host.GetTestClient().GetStringAsync("/whizbang/apply-stacks");

    await Assert.That(body).Contains("\"available\":false")
      .Because("a host whose driver supplies no query gets a stated condition, never an empty list");
    await Assert.That(body).Contains("IApplyStackQuery");
  }

  [Test]
  public async Task Signatures_ServesThePathSignaturesAsJsonAsync() {
    var query = new FixedQuery([_sig(7, "Created", "Updated+", "Closed")]);
    using var host = _buildHost(query);
    await host.StartAsync();

    var body = await host.GetTestClient().GetStringAsync("/whizbang/apply-stacks");

    await Assert.That(body).Contains("\"available\":true");
    using var json = System.Text.Json.JsonDocument.Parse(body);
    var signature = json.RootElement.GetProperty("signatures")[0];
    await Assert.That(signature.GetProperty("path")[1].GetString()).IsEqualTo("Updated+")
      .Because("the collapsed path round-trips through JSON exactly (the encoder may escape '+', the decoded value must not change)");
    await Assert.That(signature.GetProperty("streamCount").GetInt64()).IsEqualTo(7L);
    await Assert.That(body).DoesNotContain("\"flow\"")
      .Because("no anchor was requested, so no flow view is computed or serialized");
  }

  [Test]
  public async Task Signatures_WithAnchor_IncludesTheFlowViewAsync() {
    var query = new FixedQuery([_sig(7, "Created", "Updated+", "Closed")]);
    using var host = _buildHost(query);
    await host.StartAsync();

    var body = await host.GetTestClient().GetStringAsync(
      "/whizbang/apply-stacks?anchor=Updated&radius=1");

    await Assert.That(body).Contains("\"flow\"");
    await Assert.That(body).Contains("\"anchorEventType\":\"Updated\"");
    await Assert.That(body).Contains("\"offset\":-1")
      .Because("radius 1 around the anchor includes the before-column");
  }

  [Test]
  public async Task Signatures_PassesFiltersThroughToTheQueryAsync() {
    var query = new FixedQuery([]);
    using var host = _buildHost(query);
    await host.StartAsync();

    _ = await host.GetTestClient().GetStringAsync(
      "/whizbang/apply-stacks?perspective=OrderList&scope=%7B%22tenant%22%3A%22alpha%22%7D&max=25");

    await Assert.That(query.SeenOptions!.PerspectiveName).IsEqualTo("OrderList");
    await Assert.That(query.SeenOptions.ScopeJson).IsEqualTo("""{"tenant":"alpha"}""");
    await Assert.That(query.SeenOptions.MaxSignatures).IsEqualTo(25);
  }

  [Test]
  public async Task Streams_BindsRepeatedStepParametersAsTheExactPathAsync() {
    var streamId = Guid.NewGuid();
    var query = new FixedQuery([], [streamId]);
    using var host = _buildHost(query);
    await host.StartAsync();

    var body = await host.GetTestClient().GetStringAsync(
      "/whizbang/apply-stacks/streams?step=Created&step=Updated%2B&step=Closed&limit=5");

    await Assert.That(query.SeenPath!).IsEquivalentTo(["Created", "Updated+", "Closed"])
      .Because("repeated step parameters carry the exact collapsed path — event-type names may contain characters a delimited form would break on");
    await Assert.That(query.SeenLimit).IsEqualTo(5);
    await Assert.That(body).Contains(streamId.ToString());
  }

  [Test]
  public async Task MapWhizbangApplyStacks_CustomPattern_MountsBothRoutesUnderItAsync() {
    var query = new FixedQuery([]);
    using var host = _buildHost(query, pattern: "/internal/lineage");
    await host.StartAsync();
    var client = host.GetTestClient();

    var signatures = await client.GetAsync("/internal/lineage");
    var streams = await client.GetAsync("/internal/lineage/streams?step=A");

    await Assert.That((int)signatures.StatusCode).IsEqualTo(200);
    await Assert.That((int)streams.StatusCode).IsEqualTo(200)
      .Because("the drill-in rides the same group, so one pattern override moves the whole surface");
  }
}
