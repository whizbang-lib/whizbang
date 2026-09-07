using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Coverage-round-23 target for <see cref="DefaultMessageSecurityContextProvider"/>: resolving the
/// effective payload type when a message arrives wrapped in an envelope-shaped SHELL (a nested
/// envelope-in-envelope with no hydrated inner payload) whose OWN concrete class is not itself the
/// generic <c>MessageEnvelope&lt;T&gt;</c> — a subclass that inherits the generic argument from its
/// base type instead of declaring it directly. Every security decision downstream (the exempt-type
/// set, the control-plane and system-event carve-outs, the type named in a thrown
/// <c>SecurityContextRequiredException</c>) keys off this resolved type; resolving the WRAPPER's own
/// type instead of the wrapped message's type here would make an exempt message (e.g. a health check
/// carried through a custom envelope subclass) fail a strict security policy it was supposed to bypass.
/// </summary>
public class DefaultMessageSecurityContextProviderCoverageTests {
  [Test]
  public async Task EstablishContextAsync_ShellSubclassOfConstructedEnvelope_ResolvesInnerTypeFromBaseTypeAsync() {
    var innerShell = new _derivedShellEnvelope {
      MessageId = MessageId.New(),
      Payload = null!,
      Hops = [_hop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var outerEnvelope = new MessageEnvelope<_derivedShellEnvelope> {
      MessageId = MessageId.New(),
      Payload = innerShell,
      Hops = [_hop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var options = new MessageSecurityOptions { AllowAnonymous = false };
    options.ExemptMessageTypes.Add(typeof(_innerMessage));
    var provider = new DefaultMessageSecurityContextProvider(
      extractors: [],
      callbacks: [],
      options: options);

    var result = await provider.EstablishContextAsync(
      outerEnvelope, new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("the exempt set only names typeof(_innerMessage) — reaching the exempt-bypass null result "
        + "proves the resolver walked past the shell's OWN (non-generic) class onto its generic BASE "
        + "type's argument; resolving the shell's own type instead would miss the exemption and either "
        + "run extractors needlessly or throw for a message that was supposed to bypass security");
  }

  private static MessageHop _hop() => new() {
    Type = HopType.Current,
    ServiceInstance = new ServiceInstanceInfo {
      ServiceName = "TestService",
      InstanceId = Guid.NewGuid(),
      HostName = "localhost",
      ProcessId = 1234
    },
    Timestamp = DateTimeOffset.UtcNow
  };

  private sealed record _innerMessage(string Value);

  /// <summary>
  /// A SHELL envelope (its own Payload is null) whose CONCRETE class is not itself generic — the
  /// generic argument (<see cref="_innerMessage"/>) lives on its base type,
  /// <c>MessageEnvelope&lt;_innerMessage&gt;</c>, matching the documented "subclass of a constructed
  /// envelope keeps the argument on a base type rather than itself" case.
  /// </summary>
  private sealed class _derivedShellEnvelope : MessageEnvelope<_innerMessage>;
}
