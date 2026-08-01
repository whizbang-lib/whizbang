using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Stream-integrity control-plane messages are framework INFRASTRUCTURE signals, not user
/// actions: they publish from background workers with no ambient user scope, by design. A strict
/// security policy (<c>AllowAnonymous = false</c>) must not refuse them — first observed live as
/// a <see cref="SecurityContextRequiredException"/> storm on every checkpoint cycle in a consumer
/// running the strict posture, which also silenced consumer-side gap detection entirely (the same
/// gate guards receptor invocation). The exemption is EXPLICIT: only types marked
/// <see cref="IControlPlaneMessage"/> pass; domain messages keep the strict contract.
/// </summary>
[Category("Security")]
public class ControlPlaneSecurityExemptionTests {

  private static DefaultMessageSecurityContextProvider _strictProvider() => new(
    extractors: [],
    callbacks: [],
    options: new MessageSecurityOptions { AllowAnonymous = false });

  private static MessageEnvelope<T> _unscopedEnvelope<T>(T payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Local, Source = MessageSource.Local },
  };

  private sealed class _emptyServiceProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }

  [Test]
  public async Task StrictPolicy_ControlPlaneMessage_NoScope_EstablishesNoContextWithoutThrowingAsync() {
    var provider = _strictProvider();
    var envelope = _unscopedEnvelope(new IntegrityCheckpoint {
      CheckpointStreamId = TrackedGuid.NewMedo().Value,
      OriginServiceId = TrackedGuid.NewMedo().Value,
      OriginServiceName = "origin-svc",
      FromCommitSequence = 0,
      ToCommitSequence = 5,
    });

    var result = await provider.EstablishContextAsync(envelope, new _emptyServiceProvider());

    await Assert.That(result).IsNull()
      .Because("a control-plane signal carries no user scope BY DESIGN — the strict policy must pass it, not storm the logs and silence gap detection.");
  }

  [Test]
  public async Task StrictPolicy_DomainMessage_NoScope_StillThrowsAsync() {
    var provider = _strictProvider();
    var envelope = _unscopedEnvelope(new _plainDomainEvent { Sid = TrackedGuid.NewMedo().Value });

    Exception? caught = null;
    try {
      await provider.EstablishContextAsync(envelope, new _emptyServiceProvider());
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsTypeOf<SecurityContextRequiredException>()
      .Because("the exemption is EXPLICIT — an unmarked domain message keeps the strict contract.");
  }

  [Test]
  public async Task AllStreamIntegrityControlMessages_CarryTheMarkerAsync() {
    // The exemption only works if every framework message published without user scope opts in.
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(IntegrityCheckpoint))).IsTrue();
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(IntegrityGapDetected))).IsTrue();
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(RequestIntegrityManifest))).IsTrue();
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(IntegrityManifest))).IsTrue();
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(IntegrityDivergenceDetected))).IsTrue();
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(PerspectiveCoverageGapDetected))).IsTrue();
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(RequestRedeliveryCommand))).IsTrue();
    await Assert.That(typeof(IControlPlaneMessage).IsAssignableFrom(typeof(RebuildPerspectiveCommand))).IsTrue()
      .Because("the audit worker dispatches capped local rebuilds with no ambient user scope.");
  }

  private sealed record _plainDomainEvent : IEvent {
    [Whizbang.Core.StreamId]
    public Guid Sid { get; init; }
  }
}
