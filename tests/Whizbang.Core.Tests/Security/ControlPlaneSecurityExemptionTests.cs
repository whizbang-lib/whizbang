using System.Linq;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents;
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

  /// <summary>
  /// The redelivery control plane ships its payload as a <see cref="RedeliveryComposite"/> bundle,
  /// and <see cref="RequestRedeliveryCommand"/> — the message that ASKS for it — already carries the
  /// control-plane marker. The bundle did not, so every served repair bundle threw
  /// SecurityContextRequiredException on arrival: it is machine-minted and carries no user identity
  /// by construction. Because that failure feeds the inbox attempt ladder, it did not merely fail —
  /// it dead-lettered, was replayed by recovery, failed again, and flooded a fleet's logs badly
  /// enough that an operator shut the environment down.
  /// </summary>
  [Test]
  public async Task StrictPolicy_RedeliveryComposite_NoScope_EstablishesNoContextWithoutThrowingAsync() {
    var provider = _strictProvider();
    var envelope = _unscopedEnvelope(new RedeliveryComposite {
      StreamId = TrackedGuid.NewMedo().Value,
      OriginServiceId = TrackedGuid.NewMedo().Value,
    });

    var result = await provider.EstablishContextAsync(envelope, new _emptyServiceProvider());

    await Assert.That(result).IsNull()
      .Because("a repair bundle is a machine-minted control-plane container with no user identity — "
             + "the security context that matters belongs to its INNER events, which are established "
             + "individually after fan-out");
  }

  /// <summary>
  /// Structural guard for the opt-in model. Control-plane status is deliberately marked per type so
  /// security fails CLOSED — auto-deriving it (by namespace, base type, or publisher) risks silently
  /// exempting a domain message, which is a hole nobody would notice. The cost of that choice is
  /// that FORGETTING is silent too: RequestRedeliveryCommand carried the marker, the bundle it ships
  /// did not, and the omission only surfaced as an exception flood that took an environment down.
  /// This test keeps the opt-in model but makes forgetting fail here instead of in production: every
  /// concrete composite the FRAMEWORK itself mints is wire-only, carries no user identity, and must
  /// be marked. A new framework composite fails this until someone makes that call consciously.
  /// </summary>
  [Test]
  public async Task EveryFrameworkMintedComposite_CarriesTheControlPlaneMarkerAsync() {
    var frameworkComposites = typeof(IControlPlaneMessage).Assembly
      .GetTypes()
      .Where(t => !t.IsAbstract && !t.IsInterface)
      .Where(t => typeof(ICompositeEvent).IsAssignableFrom(t))
      .ToList();

    await Assert.That(frameworkComposites).IsNotEmpty()
      .Because("if this finds nothing the reflection query has drifted and the guard is vacuous");

    var unmarked = frameworkComposites
      .Where(t => !typeof(IControlPlaneMessage).IsAssignableFrom(t))
      .Select(t => t.Name)
      .OrderBy(n => n)
      .ToList();

    await Assert.That(unmarked).IsEmpty()
      .Because("a framework-minted composite is machine-generated and has no user security context "
             + "to establish; without the marker a strict consumer throws "
             + "SecurityContextRequiredException on every one, and (because the same gate guards "
             + "receptor invocation) the feature silently never runs. Unmarked: "
             + string.Join(", ", unmarked));
  }

  /// <summary>
  /// The exemption reads <c>envelope.Payload.GetType()</c> and tests the marker against THAT type.
  /// When the payload is itself an <see cref="IMessageEnvelope"/> — a doubly-wrapped envelope — the
  /// test lands on <c>MessageEnvelope&lt;T&gt;</c>, which never carries
  /// <see cref="IControlPlaneMessage"/> because the marker belongs to the inner payload. The
  /// exemption silently misses and a strict consumer throws on traffic that is exempt by design.
  /// <para>
  /// Observed live: every service in a fleet threw
  /// <see cref="SecurityContextRequiredException"/> naming
  /// <c>MessageEnvelope`1[[…RedeliveryComposite…]]</c> — the envelope type, not the composite,
  /// which is what identifies the double wrap. Because the inbox worker catches per lifecycle stage
  /// and continues, ONE message threw on several stages, each with a full stack; the receptor never
  /// ran, so redelivery repair never completed and re-requested, and the allocation churn drove pods
  /// from a flat working set into repeated OOM kills on a regular cycle. A service carrying no
  /// composite code of its own was affected too, because the shared inbox fans control-plane traffic
  /// to every subscriber.
  /// </para>
  /// Marking more types cannot fix this: the marker WAS present on the inner payload. The exemption
  /// has to see through the wrapper.
  /// </summary>
  [Test]
  public async Task StrictPolicy_NestedEnvelopeCarryingControlPlaneMessage_EstablishesNoContextWithoutThrowingAsync() {
    var provider = _strictProvider();
    var inner = _unscopedEnvelope(new RedeliveryComposite {
      StreamId = TrackedGuid.NewMedo().Value,
      OriginServiceId = TrackedGuid.NewMedo().Value,
    });
    var outer = _unscopedEnvelope((object)inner);

    var result = await provider.EstablishContextAsync(outer, new _emptyServiceProvider());

    await Assert.That(result).IsNull()
      .Because("the marker is on the INNER payload — an exemption that only inspects the outer "
             + "wrapper misses it and storms a strict consumer with exceptions for traffic that is "
             + "exempt by design");
  }

  /// <summary>
  /// The unwrap must not become a blanket exemption. Wrapping a domain message in an extra envelope
  /// is not a way to escape the strict contract — if it were, the fix would trade a log storm for a
  /// silent authorization hole, which is far worse than the bug it replaces.
  /// </summary>
  [Test]
  public async Task StrictPolicy_NestedEnvelopeCarryingDomainMessage_StillThrowsAsync() {
    var provider = _strictProvider();
    var inner = _unscopedEnvelope(new _plainDomainEvent { Sid = TrackedGuid.NewMedo().Value });
    var outer = _unscopedEnvelope((object)inner);

    Exception? caught = null;
    try {
      await provider.EstablishContextAsync(outer, new _emptyServiceProvider());
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsTypeOf<SecurityContextRequiredException>()
      .Because("unwrapping resolves the REAL payload type — it must not hand an unmarked domain "
             + "message a free pass just because something wrapped it twice");
  }

  /// <summary>
  /// Nesting is not guaranteed to stop at one level, so the unwrap walks to the innermost payload
  /// rather than peeling exactly once. A fix that unwraps a single layer would pass the test above
  /// and still storm on a three-deep envelope.
  /// </summary>
  [Test]
  public async Task StrictPolicy_DeeplyNestedEnvelopeCarryingControlPlaneMessage_EstablishesNoContextWithoutThrowingAsync() {
    var provider = _strictProvider();
    var innermost = _unscopedEnvelope(new IntegrityCheckpoint {
      CheckpointStreamId = TrackedGuid.NewMedo().Value,
      OriginServiceId = TrackedGuid.NewMedo().Value,
      OriginServiceName = "origin-svc",
      FromCommitSequence = 0,
      ToCommitSequence = 5,
    });
    var middle = _unscopedEnvelope((object)innermost);
    var outer = _unscopedEnvelope((object)middle);

    var result = await provider.EstablishContextAsync(outer, new _emptyServiceProvider());

    await Assert.That(result).IsNull()
      .Because("peeling exactly one layer would leave the same defect one level deeper — the "
             + "exemption resolves the innermost payload");
  }
}

