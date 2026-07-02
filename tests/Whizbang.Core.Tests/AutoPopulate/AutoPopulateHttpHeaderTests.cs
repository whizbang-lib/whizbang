using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Public class whose property is populated from an inbound HTTP header carried in the scope extensions.
/// </summary>
public class HttpHeaderEvent {
  public Guid Id { get; set; }

  [PopulateFromHttpHeader("X-Correlation-ID")]
  public string? CorrelationId { get; set; }
}

/// <summary>
/// <c>[PopulateFromHttpHeader]</c> reads a header value that the edge mapped into the ambient scope's
/// extensions (<c>ex</c> list) and which rides the hop. Locks the general facility end to end at the
/// populator layer (edge seeding of the extension is covered separately).
/// </summary>
[Category("Core")]
[Category("AutoPopulate")]
public class AutoPopulateHttpHeaderTests {

  [Test]
  public async Task PopulateSent_HttpHeader_PopulatesFromScopeExtensionAsync() {
    var message = new HttpHeaderEvent { Id = Guid.NewGuid() };
    var scope = new PerspectiveScope {
      TenantId = "t1",
      Extensions = { new ScopeExtension("X-Correlation-ID", "corr-999") }
    };
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromPerspectiveScope(scope)
    };

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, MessageId.New());

    var populated = (HttpHeaderEvent)result;
    await Assert.That(populated.CorrelationId).IsEqualTo("corr-999")
      .Because("[PopulateFromHttpHeader] reads the value from the scope 'ex' extension carried on the hop.");
  }

  [Test]
  public async Task PopulateSent_HttpHeader_MatchesHeaderKeyCaseInsensitivelyAsync() {
    var message = new HttpHeaderEvent { Id = Guid.NewGuid() };
    var scope = new PerspectiveScope {
      TenantId = "t1",
      // Different casing than the attribute's "X-Correlation-ID" — HTTP header names are case-insensitive.
      Extensions = { new ScopeExtension("x-correlation-id", "corr-CI") }
    };
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromPerspectiveScope(scope)
    };

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, MessageId.New());

    var populated = (HttpHeaderEvent)result;
    await Assert.That(populated.CorrelationId).IsEqualTo("corr-CI");
  }
}
