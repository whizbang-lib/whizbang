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
/// The operator endpoint that asks an origin to republish named streams (#1028): the body becomes a
/// <see cref="StreamRedeliveryRequest"/>, a sent request answers 202 with its receipt, and a request the service
/// cannot send answers with the reason.
/// </summary>
/// <code-under-test>src/Whizbang.Hosting.AspNet/StreamRedeliveryEndpoints.cs</code-under-test>
public class StreamRedeliveryEndpointsTests {
  private static readonly Guid _stream = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");

  [Test]
  public async Task Post_SendsTheRequest_AndAnswersAcceptedWithTheReceiptAsync() {
    var requester = new FakeRequester();
    using var host = _host(requester);
    await host.StartAsync();

    var response = await _postAsync(host, "/whizbang/redelivery/streams", $$"""
      {"originService":"origin-svc","streamIds":["{{_stream}}"],"originRequestTopic":"origin.requests",
       "replyTopic":"replies","tenantScope":"tenant-a","eventTypes":["Contracts.OrderPlaced"],"stateOnly":true}
      """);

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    var request = requester.Requests.Single();
    await Assert.That(request.OriginService).IsEqualTo("origin-svc");
    await Assert.That(request.StreamIds).IsEquivalentTo([_stream]);
    await Assert.That(request.OriginRequestTopic).IsEqualTo("origin.requests");
    await Assert.That(request.ReplyTopic).IsEqualTo("replies");
    await Assert.That(request.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(request.EventTypes).IsEquivalentTo(["Contracts.OrderPlaced"]);
    await Assert.That(request.StateOnly).IsTrue();
    var body = await response.Content.ReadAsStringAsync();
    await Assert.That(body).Contains("\"requests\":1").And.Contains("\"originService\":\"origin-svc\"");
  }

  [Test]
  public async Task Post_UnderACustomPrefix_WithOnlyTheRequiredFields_DefaultsTheRestAsync() {
    var requester = new FakeRequester();
    using var host = _host(requester, "/ops/redelivery");
    await host.StartAsync();

    var response = await _postAsync(host, "/ops/redelivery/streams", $$"""{"originService":"origin-svc","streamIds":["{{_stream}}"]}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
    await Assert.That(requester.Requests.Single().StateOnly).IsFalse();
  }

  [Test]
  public async Task Post_ARequestTheRequesterRefuses_AnswersBadRequestWithTheReasonAsync() {
    var requester = new FakeRequester { Throw = new ArgumentException("A redelivery request names at least one stream.") };
    using var host = _host(requester);
    await host.StartAsync();

    var response = await _postAsync(host, "/whizbang/redelivery/streams", "null");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(await response.Content.ReadAsStringAsync()).Contains("at least one stream");
    await Assert.That(requester.Requests.Single().OriginService).IsEqualTo("")
      .Because("A null body reaches the requester as an empty request, which it refuses with its own reason.");
  }

  [Test]
  public async Task Post_ARequestThisServiceCannotSend_AnswersConflictWithTheReasonAsync() {
    var requester = new FakeRequester { Throw = new InvalidOperationException("Name it as the origin request topic.") };
    using var host = _host(requester);
    await host.StartAsync();

    var response = await _postAsync(host, "/whizbang/redelivery/streams", $$"""{"originService":"o","streamIds":["{{_stream}}"]}""");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    await Assert.That(await response.Content.ReadAsStringAsync()).Contains("origin request topic");
  }

  [Test]
  public async Task Post_ABodyThatIsNotJson_AnswersBadRequestAsync() {
    var requester = new FakeRequester();
    using var host = _host(requester);
    await host.StartAsync();

    var response = await _postAsync(host, "/whizbang/redelivery/streams", "{not json");

    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    await Assert.That(requester.Requests).IsEmpty();
  }

  [Test]
  public async Task Map_RejectsANullBuilderOrAnEmptyPrefixAsync() {
    await using var services = new ServiceCollection().AddRouting().BuildServiceProvider();

    await Assert.That(() => ((IEndpointRouteBuilder)null!).MapWhizbangStreamRedeliveryEndpoints()).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new RouteBuilderStub(services).MapWhizbangStreamRedeliveryEndpoints("")).ThrowsExactly<ArgumentException>();
  }

  private static async Task<HttpResponseMessage> _postAsync(IHost host, string path, string json) {
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    return await host.GetTestClient().PostAsync(path, content);
  }

  private static IHost _host(FakeRequester requester, string prefix = "/whizbang/redelivery") =>
    new HostBuilder()
      .ConfigureWebHost(web => {
        web.UseTestServer();
        web.ConfigureServices(s => {
          s.AddRouting();
          s.AddSingleton<IStreamRedeliveryRequester>(requester);
        });
        web.Configure(app => {
          app.UseRouting();
          app.UseEndpoints(e => e.MapWhizbangStreamRedeliveryEndpoints(prefix));
        });
      })
      .Build();

  private sealed class RouteBuilderStub(IServiceProvider services) : IEndpointRouteBuilder {
    public IServiceProvider ServiceProvider { get; } = services;
    public ICollection<EndpointDataSource> DataSources { get; } = [];
    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
  }

  private sealed class FakeRequester : IStreamRedeliveryRequester {
    public List<StreamRedeliveryRequest> Requests { get; } = [];
    public Exception? Throw { get; init; }

    public Task<StreamRedeliveryReceipt> RequestAsync(StreamRedeliveryRequest request, CancellationToken cancellationToken = default) {
      Requests.Add(request);
      return Throw is null
        ? Task.FromResult(new StreamRedeliveryReceipt(request.OriginService, "origin.requests", "replies", request.StreamIds.Count, 1))
        : Task.FromException<StreamRedeliveryReceipt>(Throw);
    }
  }
}
