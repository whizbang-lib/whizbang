using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// System events (<see cref="ISystemEvent"/>) are framework TELEMETRY, not user actions: an
/// <see cref="EventAudited"/> relay minted for a background/bulk flow carries no interactive
/// principal BY DESIGN — its attribution (TenantId, UserId, Scope) rides in the payload as data.
/// A strict security policy (<c>AllowAnonymous = false</c>) must therefore never hard-fail context
/// establishment for a system event: refusing one dead-letters the audit record it carries, so the
/// act of auditing destroys the audit trail. Same shape as the
/// <see cref="IControlPlaneMessage"/> exemption (see ControlPlaneSecurityExemptionTests); the
/// exemption is EXPLICIT — only <see cref="ISystemEvent"/> payloads pass, domain messages keep the
/// strict contract.
/// </summary>
[Category("Security")]
public class SystemEventSecurityExemptionTests {

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

  private static JsonElement _emptyBody() => JsonDocument.Parse("{}").RootElement.Clone();

  private sealed class _emptyServiceProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }

  [Test]
  public async Task StrictPolicy_EventAudited_NoEstablishablePrincipal_EstablishesNoContextWithoutThrowingAsync() {
    // The audit relay for a system/bulk flow has NO interactive principal to extract and no scope
    // on its hops. Attribution must degrade to an unattributed (null) context, never throw —
    // EventAudited carries its own TenantId/UserId as payload data, so nothing is lost.
    var provider = _strictProvider();
    var envelope = _unscopedEnvelope(new EventAudited {
      Id = TrackedGuid.NewMedo().Value,
      OriginalEventType = "Contracts.SomethingHappened",
      OriginalStreamId = TrackedGuid.NewMedo().Value.ToString(),
      OriginalStreamPosition = 0,
      OriginalBody = _emptyBody(),
      Timestamp = DateTimeOffset.UtcNow,
    });

    var result = await provider.EstablishContextAsync(envelope, new _emptyServiceProvider());

    await Assert.That(result).IsNull()
      .Because("a system event with no establishable principal must proceed unattributed — " +
               "hard-failing attribution dead-letters the audit record the event carries.");
  }

  [Test]
  public async Task StrictPolicy_CommandAudited_NoEstablishablePrincipal_EstablishesNoContextWithoutThrowingAsync() {
    // The exemption keys on ISystemEvent, not on one concrete type — every system event minted by
    // a background flow gets the same never-dead-letter guarantee.
    var provider = _strictProvider();
    var envelope = _unscopedEnvelope(new CommandAudited {
      Id = TrackedGuid.NewMedo().Value,
      CommandType = "Contracts.DoSomethingCommand",
      CommandBody = _emptyBody(),
      Timestamp = DateTimeOffset.UtcNow,
    });

    var result = await provider.EstablishContextAsync(envelope, new _emptyServiceProvider());

    await Assert.That(result).IsNull()
      .Because("the exemption covers ISystemEvent as a category, not EventAudited alone.");
  }

  [Test]
  public async Task StrictPolicy_NonSystemEvent_NoEstablishablePrincipal_StillThrowsAsync() {
    // Lock the other direction: the exemption is EXPLICIT — an ordinary domain event with no
    // establishable principal keeps the strict SecurityContextRequiredException contract.
    var provider = _strictProvider();
    var envelope = _unscopedEnvelope(new _plainDomainEvent { Sid = TrackedGuid.NewMedo().Value });

    Exception? caught = null;
    try {
      await provider.EstablishContextAsync(envelope, new _emptyServiceProvider());
    } catch (Exception ex) {
      caught = ex;
    }

    await Assert.That(caught).IsTypeOf<SecurityContextRequiredException>()
      .Because("a domain message must not inherit the system-event exemption — strict stays strict.");
  }

  private sealed record _plainDomainEvent : IEvent {
    [Whizbang.Core.StreamId]
    public Guid Sid { get; init; }
  }
}
