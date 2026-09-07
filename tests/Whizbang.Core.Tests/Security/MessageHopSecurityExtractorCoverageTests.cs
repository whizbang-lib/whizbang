using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Covers the "empty scope" branch of <see cref="MessageHopSecurityExtractor.ExtractAsync"/>: a
/// merged <c>ScopeContext</c> that is NOT null (so it survived the earlier "no scope in hop chain"
/// check) but whose TenantId and UserId are both empty — reachable only when a hop's delta carries
/// collection changes (e.g. Roles) with no Values[Scope] entry, so <c>ApplyTo</c> leaves Scope at
/// its all-default <c>PerspectiveScope</c>.
/// </summary>
public class MessageHopSecurityExtractorCoverageTests {
  private sealed record TestMessage(string Value);

  private static ServiceInstanceInfo _serviceInstance() => new() {
    ServiceName = "test-service",
    InstanceId = Guid.NewGuid(),
    HostName = "test-host",
    ProcessId = 1234
  };

  /// <summary>
  /// Production impact if this branch regressed: an untethered role/permission grant (roles
  /// present on the hop chain but no TenantId/UserId ever asserted) would be extracted as a real
  /// security context instead of being treated as "no identity worth acting on" — attaching
  /// authorization decisions to a caller nobody actually identified.
  /// </summary>
  [Test]
  public async Task ExtractAsync_MergedScopeHasRolesButNoTenantOrUser_ReturnsNullAsync() {
    var rolesElement = JsonSerializer.SerializeToElement<IReadOnlyList<string>>(["Admin"]);
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _serviceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      // No Values[ScopeProp.Scope] entry — only a Collections change — so ApplyTo leaves Scope at
      // its default (TenantId/UserId both null). HasChanges is still true (Collections.Count > 0),
      // so _mergeScopeDeltas does NOT drop this hop the way a fully-empty/null Scope would.
      Scope = new ScopeDelta {
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Roles] = new CollectionChanges { Set = rolesElement }
        }
      }
    };
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("payload"),
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var extractor = new MessageHopSecurityExtractor();

    var result = await extractor.ExtractAsync(envelope, new MessageSecurityOptions(), CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("a merged scope with no TenantId and no UserId carries no identity worth extracting, "
             + "even when roles rode along on the same delta — an untethered role grant must not be "
             + "mistaken for one attached to a real, identified caller");
  }
}
