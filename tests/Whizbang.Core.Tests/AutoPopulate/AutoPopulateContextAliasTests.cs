using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Public class message whose context properties are populated from the ambient scope on the hop.
/// </summary>
public class ContextAliasEvent {
  public Guid Id { get; set; }

  [PopulateFromContext(ContextKind.UserId)]
  public string? UserId { get; set; }

  [PopulateFromContext(ContextKind.TenantId)]
  public string? TenantId { get; set; }
}

/// <summary>
/// The scope is serialized onto the hop with SHORT keys (<c>PerspectiveScope</c> declares
/// <c>[JsonPropertyName("u")]</c>/<c>[JsonPropertyName("t")]</c>), but the generated context extractor looked
/// for the long names ("UserId"/"TenantId") — so <c>[PopulateFromContext]</c> silently returned null at
/// runtime. This locks in that context values populate from the actual JSON field names (resolved from the
/// model by the generator, not hard-coded).
/// </summary>
[Category("Core")]
[Category("AutoPopulate")]
public class AutoPopulateContextAliasTests {

  [Test]
  public async Task PopulateSent_ContextValues_PopulateFromShortScopeKeysAsync() {
    var message = new ContextAliasEvent { Id = Guid.NewGuid() };
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.NewGuid(),
        HostName = "localhost",
        ProcessId = 1234
      },
      Timestamp = DateTimeOffset.UtcNow,
      // Serializes as {"u":"user-123","t":"tenant-abc"} — the real on-hop scope shape.
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { UserId = "user-123", TenantId = "tenant-abc" })
    };

    var result = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, MessageId.New());

    var populated = (ContextAliasEvent)result;
    await Assert.That(populated.UserId).IsEqualTo("user-123")
      .Because("UserId must be read from the scope's short JSON key 'u', resolved from PerspectiveScope's [JsonPropertyName].");
    await Assert.That(populated.TenantId).IsEqualTo("tenant-abc")
      .Because("TenantId must be read from the scope's short JSON key 't'.");
  }
}
