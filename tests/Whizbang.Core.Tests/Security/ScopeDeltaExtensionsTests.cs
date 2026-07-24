using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Scope <c>Extensions</c> carry arbitrary key/value pairs — notably HTTP header values mapped in at the edge
/// (<c>ExtensionHeaderMappings</c>) that <c>[PopulateFromHttpHeader]</c> reads back. They must survive the
/// scope→hop serialization (<c>_serializeScope</c>/<c>_deserializeScope</c>), which previously dropped them.
/// </summary>
[Category("Core")]
[Category("Security")]
public class ScopeDeltaExtensionsTests {

  [Test]
  public async Task ScopeDelta_CarriesExtensions_ThroughSerializationRoundTripAsync() {
    var scope = new PerspectiveScope {
      TenantId = "t1",
      Extensions = { new ScopeExtension("X-Correlation-ID", "abc-123") }
    };

    var delta = ScopeDelta.FromPerspectiveScope(scope);
    await Assert.That(delta).IsNotNull();

    var applied = delta!.ApplyTo(null);
    var ext = applied.Scope.Extensions.FirstOrDefault(e => e.Key == "X-Correlation-ID");
    await Assert.That(ext).IsNotNull()
      .Because("Header values ride scope Extensions; they must survive scope→hop serialization.");
    await Assert.That(ext!.Value).IsEqualTo("abc-123");
  }

  [Test]
  public async Task FromPerspectiveScope_WithOnlyExtensions_IsNotNullAsync() {
    // A scope carrying only an extension (no tenant/user/customer/org) must still produce a delta,
    // otherwise the header value would be silently dropped.
    var scope = new PerspectiveScope {
      Extensions = { new ScopeExtension("X-Correlation-ID", "abc-123") }
    };

    var delta = ScopeDelta.FromPerspectiveScope(scope);

    await Assert.That(delta).IsNotNull();
  }
}
