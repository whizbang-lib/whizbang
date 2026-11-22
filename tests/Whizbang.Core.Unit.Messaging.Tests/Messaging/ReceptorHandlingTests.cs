// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A receptor body runs inside the handling of the message it was invoked for (#2274): what the body publishes
/// explicitly then derives its id from that handling, as what it returns always has, so a retry re-derives both.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorHandling.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Messaging")]
[NotInParallel("ReceptorHandlingProbe")]
public class ReceptorHandlingTests {
  [Test]
  public async Task InvokeAsync_TheReceptorBody_RunsInTheHandlingOfItsEnvelopeAsync() {
    var seen = new List<IMessageEnvelope?>();
    var registry = new Whizbang.Core.Unit.Messaging.Tests.Generated.GeneratedReceptorRegistry();
    registry.Register<ReceptorHandlingProbeEvent>(new ReceptorHandlingProbeReceptor(() => seen.Add(ReceptorHandling.Current)),
      LifecycleStage.PostInboxInline);
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var envelope = _envelope(new ReceptorHandlingProbeEvent());

    await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

    await Assert.That(seen).Count().IsEqualTo(1);
    await Assert.That(seen[0]).IsSameReferenceAs(envelope)
      .Because("the body's explicit publishes derive from this envelope, sharing its emission ordinals with what it returns");
    await Assert.That(ReceptorHandling.Current).IsNull()
      .Because("after the body returns, the caller is no longer in that handling");
  }

  [Test]
  public async Task Enter_Disposed_RestoresTheOuterHandlingAsync() {
    var outer = _envelope(new ReceptorHandlingProbeEvent());
    var inner = _envelope(new ReceptorHandlingProbeEvent());

    IMessageEnvelope? insideInner;
    IMessageEnvelope? backInOuter;
    using (ReceptorHandling.Enter(outer)) {
      using (ReceptorHandling.Enter(inner)) {
        insideInner = ReceptorHandling.Current;
      }
      backInOuter = ReceptorHandling.Current;
    }

    await Assert.That(insideInner).IsSameReferenceAs(inner);
    await Assert.That(backInOuter).IsSameReferenceAs(outer)
      .Because("a receptor invoked from inside another's body returns its caller to the caller's own handling");
    await Assert.That(ReceptorHandling.Current).IsNull();
  }

  [Test]
  public async Task Enter_ANullEnvelope_IsRefusedAsync() {
    await Assert.That(() => ReceptorHandling.Enter(null!)).Throws<ArgumentNullException>();
  }

  private static MessageEnvelope<ReceptorHandlingProbeEvent> _envelope(ReceptorHandlingProbeEvent message) => new() {
    MessageId = MessageId.New(),
    Payload = message,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local, HandlerName = "ProbeHandler" },
  };
}

/// <summary>The message the handling probe runs for.</summary>
public sealed record ReceptorHandlingProbeEvent : IMessage;

/// <summary>Reports the handling its body runs in. Registered only at runtime by the tests above.</summary>
[SuppressReceptorRegistration]
public sealed class ReceptorHandlingProbeReceptor(Action onReceived) : IReceptor<ReceptorHandlingProbeEvent> {
  /// <inheritdoc />
  public ValueTask HandleAsync(ReceptorHandlingProbeEvent message, CancellationToken cancellationToken = default) {
    onReceived();
    return ValueTask.CompletedTask;
  }
}
