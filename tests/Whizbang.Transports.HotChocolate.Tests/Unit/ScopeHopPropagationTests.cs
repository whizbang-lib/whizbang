// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// From the HTTP edge to a receptor two hops away (#1854). The scope middleware maps a claim to a scope
/// extension and lists one claim for propagation; a message dispatched by the request crosses the JSON wire,
/// a receiving service establishes its scope from the hops, emits a message of its own, and the service after
/// that sees the extension and the listed claim, never the unlisted one.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.HotChocolate/Middleware/WhizbangScopeMiddleware.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Security/HopScope.cs</code-under-test>
public class ScopeHopPropagationTests {

  [Test]
  public async Task ExtensionMappedAtTheEdge_ReachesAReceptorTwoHopsAway_AndTheEventItEmitsAsync() {
    var options = new WhizbangScopeOptions();
    options.ExtensionClaimMappings["acting_admin"] = "acting-admin";
    options.PropagatedClaimTypes.Add("department");
    var http = new DefaultHttpContext {
      User = new ClaimsPrincipal(new ClaimsIdentity([
        new Claim("tenant_id", "tenant-edge"),
        new Claim("sub", "user-edge"),
        new Claim("acting_admin", "true"),
        new Claim("department", "finance"),
        new Claim("email", "someone@example.test"),
      ], "TestAuth")),
    };

    // The request dispatches a message: its hop is built from the request's scope.
    ScopeDelta? firstHop = null;
    var middleware = new WhizbangScopeMiddleware(_ => {
      firstHop = HopScope.FromMessageContext(MessageContext.New()) ?? HopScope.FromAmbient();
      return Task.CompletedTask;
    }, options);
    await middleware.InvokeAsync(http, new ScopeContextAccessor());
    await Assert.That(ScopeContextAccessor.CurrentContext).IsNull()
      .Because("the request's scope must not leak into the receiving side, or the test proves nothing");

    // One hop away: the receptor emits a message of its own under the scope it was given.
    ScopeDelta? secondHop = null;
    IScopeContext? seenOneHopAway = null;
    await _receiveAsync(_wire(firstHop), scope => {
      seenOneHopAway = scope;
      secondHop = HopScope.FromAmbient();
    });

    // Two hops away: the receptor sees the request's extension and emits again.
    ScopeDelta? thirdHop = null;
    IScopeContext? seenTwoHopsAway = null;
    await _receiveAsync(_wire(secondHop), scope => {
      seenTwoHopsAway = scope;
      thirdHop = HopScope.FromAmbient();
    });

    await _assertCarriesAsync(seenOneHopAway, "the receptor one hop away");
    await _assertCarriesAsync(seenTwoHopsAway, "the receptor two hops away");
    await _assertCarriesAsync(_wire(thirdHop).GetCurrentScope(), "the event the receptor two hops away emitted");
  }

  private static async Task _assertCarriesAsync(IScopeContext? scope, string where) {
    await Assert.That(scope).IsNotNull().Because($"{where} has no scope at all");
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-edge");
    await Assert.That(scope.Scope.Extensions.Select(e => (e.Key, e.Value)))
      .IsEquivalentTo([("acting-admin", (string?)"true")])
      .Because($"{where} must see the extension the edge mapped from the caller's claim");
    await Assert.That(scope.Claims.Keys).IsEquivalentTo(["department"])
      .Because($"{where} sees the listed claim and never the unlisted one");
  }

  /// <summary>An envelope carrying <paramref name="hopScope"/>, after a JSON round trip as a transport carries it.</summary>
  private static MessageEnvelope<JsonElement> _wire(ScopeDelta? hopScope) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(TrackedGuid.New()),
      Payload = JsonSerializer.SerializeToElement(1),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UnixEpoch,
        Scope = hopScope,
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var typeInfo = JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);
    return (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(bytes, typeInfo)!;
  }

  /// <summary>
  /// Establishes the receiving service's context from the envelope's hops and runs <paramref name="receptor"/>
  /// inside it, as a security callback does.
  /// </summary>
  private static async Task _receiveAsync(IMessageEnvelope envelope, Action<IScopeContext> receptor) {
    var services = new ServiceCollection();
    services.AddScoped<IScopeContextAccessor, ScopeContextAccessor>();
    services.AddSingleton<ISecurityContextCallback>(new ReceptorCallback(receptor));
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    await SecurityContextHelper.EstablishFullContextAsync(envelope, scope.ServiceProvider);
  }

  private sealed class ReceptorCallback(Action<IScopeContext> receptor) : ISecurityContextCallback {
    public ValueTask OnContextEstablishedAsync(IScopeContext context, IMessageEnvelope envelope,
        IServiceProvider scopedProvider, CancellationToken cancellationToken = default) {
      receptor(context);
      return ValueTask.CompletedTask;
    }
  }
}
