using System;
using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Regression tests proving that <see cref="MessageEnvelope{TMessage}.ReceptorInvocations"/>
/// is NOT consulted by security, scope, source-service, or trace-context extraction —
/// those all walk <see cref="MessageEnvelope{TMessage}.Hops"/>. A future contributor who
/// "consolidates" the two lists must break these tests.
/// </summary>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public class EnvelopeContextReceptorInvocationRegressionTests {

  private sealed record TestMessage(string Value) : IMessage;

  private static MessageEnvelope<TestMessage> _envelopeWithHop(string serviceName) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = new TestMessage("test"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = serviceName,
            InstanceId = Guid.NewGuid(),
            HostName = "test-host",
            ProcessId = 1
          }
        }
      ]
    };
  }

  private static List<ReceptorInvocationRecord> _syntheticInvocations() => [
    new ReceptorInvocationRecord {
      ReceptorId = "SomeReceptor",
      Stage = LifecycleStage.PostInboxInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.FromMilliseconds(3),
      ServiceName = "some-other-service"
    }
  ];

  [Test]
  public async Task SourceServiceExtractionIgnoresReceptorInvocationsAsync() {
    // ReceptorInvoker extracts the "source service" from envelope.Hops[^1].ServiceInstance.ServiceName.
    // A ReceptorInvocationRecord also carries a ServiceName, but it must NOT influence that extraction.
    var envelopeA = _envelopeWithHop("upstream-service");
    var envelopeB = _envelopeWithHop("upstream-service");
    envelopeB.ReceptorInvocations = _syntheticInvocations();

    var sourceA = envelopeA.Hops[^1].ServiceInstance.ServiceName;
    var sourceB = envelopeB.Hops[^1].ServiceInstance.ServiceName;

    await Assert.That(sourceA).IsEqualTo(sourceB);
    await Assert.That(sourceA).IsEqualTo("upstream-service");
  }

  [Test]
  public async Task CorrelationIdExtractionIgnoresReceptorInvocationsAsync() {
    var envelope = _envelopeWithHop("svc");
    var expected = envelope.Hops[0].CorrelationId;

    envelope.ReceptorInvocations = _syntheticInvocations();

    var actual = envelope.GetCorrelationId();
    await Assert.That(actual).IsEqualTo(expected);
  }

  [Test]
  public async Task CausationIdExtractionIgnoresReceptorInvocationsAsync() {
    var envelope = _envelopeWithHop("svc");
    var expected = envelope.Hops[0].CausationId;

    envelope.ReceptorInvocations = _syntheticInvocations();

    var actual = envelope.GetCausationId();
    await Assert.That(actual).IsEqualTo(expected);
  }

  [Test]
  public async Task GetCurrentScopeIgnoresReceptorInvocationsAsync() {
    var envelope = _envelopeWithHop("svc");
    var scopeBefore = envelope.GetCurrentScope();

    envelope.ReceptorInvocations = _syntheticInvocations();

    var scopeAfter = envelope.GetCurrentScope();
    // Scope merging only considers Hops — identical before and after.
    await Assert.That(scopeAfter?.ToString()).IsEqualTo(scopeBefore?.ToString());
  }

  [Test]
  public async Task HopsCountUnchangedByReceptorInvocationsAsync() {
    // Tiny sanity test: setting / mutating ReceptorInvocations must not grow the hops list.
    var envelope = _envelopeWithHop("svc");
    var initialCount = envelope.Hops.Count;

    envelope.ReceptorInvocations = _syntheticInvocations();
    envelope.GetOrCreateReceptorInvocations().Add(new ReceptorInvocationRecord {
      ReceptorId = "Another",
      Stage = LifecycleStage.PreOutboxInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.Zero,
      ServiceName = "svc"
    });

    await Assert.That(envelope.Hops.Count).IsEqualTo(initialCount);
  }
}
