// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The handling a receptor reached by generated local dispatch enters (#2633): its own, anchored on the outer handling's
/// next local invocation, when an outer handling is active and ran under the current scheme; none otherwise.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorHandling.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/LocalReceptorHandlingScope.cs</code-under-test>
[Category("Messaging")]
[NotInParallel("LocalReceptorHandlingAllocation")] // the thread's allocation counter the measurement reads
public class LocalReceptorHandlingTests {
  private const string SERVICE = "orders";
  private const string OUTER = "Ns.OuterReceptor";
  private const string INNER = "Ns.InnerReceptor";
  private const int ITERATIONS = 1_000;

  [Test]
  public async Task EnterLocalInvocation_AtTopLevel_EntersNothingAsync() {
    Handling? inside;
    using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
      inside = ReceptorHandling.CurrentHandling;
    }

    await Assert.That(inside).IsNull()
      .Because("a top-level local invoke has no handled message: DeriveFrom keeps throwing, naming local dispatch");
  }

  [Test]
  public async Task EnterLocalInvocation_AtTopLevel_AllocatesNothingAsync() {
    // The hot path (#2633 decision 3): one AsyncLocal read, no handling, nothing allocated
    _enterAndLeave();
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < ITERATIONS; i++) {
      _enterAndLeave();
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

    await Assert.That(allocated).IsEqualTo(0L);
  }

  [Test]
  public async Task EnterLocalInvocation_InsideAHandling_EntersItsOwnHandling_AnchoredOnTheNextLocalInvocationAsync() {
    var outer = LocalInvocationIdentityTests._envelope();
    Handling first;
    Handling second;
    IMessageEnvelope? firstEnvelope;
    Handling? restored;
    using (ReceptorHandling.Enter(outer, SERVICE, OUTER)) {
      using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
        first = Handling.Current;
        firstEnvelope = ReceptorHandling.Current;
      }
      using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
        second = Handling.Current;
      }
      restored = ReceptorHandling.CurrentHandling;
    }

    await Assert.That(first.ReceptorId).IsEqualTo(INNER);
    await Assert.That(first.ServiceName).IsEqualTo(SERVICE);
    await Assert.That(first.HasEnded).IsTrue().Because("the inner receptor's handling ends when its body returns");
    await Assert.That(restored!.ReceptorId).IsEqualTo(OUTER);
    await Assert.That(ReceptorHandling.AnchorOf(firstEnvelope!))
      .IsEqualTo(LocalInvocationIdentity.Derive(outer.MessageId.Value, SERVICE, "PlaceOrderHandler", OUTER, 0));
    await Assert.That(ReceptorHandling.AnchorOf(second.Envelope!))
      .IsEqualTo(LocalInvocationIdentity.Derive(outer.MessageId.Value, SERVICE, "PlaceOrderHandler", OUTER, 1))
      .Because("each local invocation takes the outer handling's next local-invocation ordinal");
    await Assert.That(firstEnvelope!.DispatchContext.HandlerName).IsEqualTo("PlaceOrderHandler");
  }

  [Test]
  public async Task EnterLocalInvocation_NestedTwice_AnchorsOnTheInnerHandlingAsync() {
    var outer = LocalInvocationIdentityTests._envelope();
    IMessageEnvelope? middle;
    IMessageEnvelope? innermost;
    using (ReceptorHandling.Enter(outer, SERVICE, OUTER)) {
      using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
        middle = ReceptorHandling.Current;
        using (ReceptorHandling.EnterLocalInvocation(SERVICE, "Ns.Innermost")) {
          innermost = ReceptorHandling.Current;
        }
      }
    }

    await Assert.That(ReceptorHandling.AnchorOf(innermost!))
      .IsEqualTo(LocalInvocationIdentity.Derive(ReceptorHandling.AnchorOf(middle!), SERVICE, "PlaceOrderHandler", INNER, 0))
      .Because("the same rule at every depth: the anchor of the handling the call is made in");
  }

  [Test]
  public async Task EnterLocalInvocation_UnderTheSharedScheme_EntersNothing_TheInnerSharesTheOuterHandlingAsync() {
    // A handling that first ran on a build before #2633 keeps that build's ids on every retry (decision 2)
    var outer = LocalInvocationIdentityTests._envelope(HandlingDerivationScheme.SharedWithCaller);
    Handling? outerHandling;
    Handling? inside;
    using (ReceptorHandling.Enter(outer, SERVICE, OUTER)) {
      outerHandling = ReceptorHandling.CurrentHandling;
      using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
        inside = ReceptorHandling.CurrentHandling;
      }
    }

    await Assert.That(inside).IsSameReferenceAs(outerHandling);
  }

  [Test]
  public async Task EnterLocalInvocation_AnEndedOuterHandling_EntersNothingAsync() {
    var outer = LocalInvocationIdentityTests._envelope();
    var scope = ReceptorHandling.Enter(outer, SERVICE, OUTER);
    var outerHandling = ReceptorHandling.CurrentHandling!;
    outerHandling.End();
    Handling? inside;
    try {
      using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
        inside = ReceptorHandling.CurrentHandling;
      }
    } finally {
      scope.Dispose();
    }

    await Assert.That(inside).IsSameReferenceAs(outerHandling)
      .Because("work the body did not await derives nothing a retry would reproduce, and keeps throwing that it ended");
  }

  [Test]
  public async Task EnterLocalInvocation_AnOuterHandlingWithNoMessageId_EntersNothingAsync() {
    var outer = new MessageEnvelope<object> {
      MessageId = new Whizbang.Core.ValueObjects.MessageId(Guid.Empty),
      Payload = new object(),
      Hops = [],
      DispatchContext = LocalInvocationIdentityTests._envelope().DispatchContext,
    };
    Handling? outerHandling;
    Handling? inside;
    using (ReceptorHandling.Enter(outer, SERVICE, OUTER)) {
      outerHandling = ReceptorHandling.CurrentHandling;
      using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
        inside = ReceptorHandling.CurrentHandling;
      }
    }

    await Assert.That(inside).IsSameReferenceAs(outerHandling);
  }

  [Test]
  public async Task Scheme_ANonInboxHandling_RunsUnderTheCurrentSchemeAsync() {
    var envelope = LocalInvocationIdentityTests._envelope();
    HandlingDerivationScheme scheme;
    using (ReceptorHandling.Enter(envelope, SERVICE, OUTER)) {
      scheme = ReceptorHandling.CurrentHandling!.Scheme;
    }

    await Assert.That(scheme).IsEqualTo(HandlingDerivationScheme.OwnHandling);
    await Assert.That(ReceptorHandling.CurrentScheme).IsEqualTo(HandlingDerivationScheme.OwnHandling)
      .Because("this build stamps the scheme it runs a handling under on the inbox row, so its retries keep its ids");
  }

  private static void _enterAndLeave() {
    using (ReceptorHandling.EnterLocalInvocation(SERVICE, INNER)) {
      // nothing: the scope's cost is what is measured
    }
  }
}
