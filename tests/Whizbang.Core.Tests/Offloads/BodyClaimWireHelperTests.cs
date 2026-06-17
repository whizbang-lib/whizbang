using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// Tests for the shared <see cref="BodyClaimWireHelper.ResolveDeserializeTypeInfo"/> used by every
/// wire transport on receive. The resolver-delegate overload lets a transport inject its own
/// type-name resolver (e.g. ASB) while still sharing the claim-detection + resolve logic.
/// </summary>
public class BodyClaimWireHelperTests {
  [Test]
  public async Task ResolveDeserializeTypeInfo_NonClaim_UsesInjectedResolverAsync() {
    var sentinel = _stringTypeInfo();
    var called = false;
    JsonTypeInfo? Resolver(string name, JsonSerializerOptions o) { called = true; return sentinel; }

    var ti = BodyClaimWireHelper.ResolveDeserializeTypeInfo(
      "Some.Type, Asm", isClaimHeaderValue: null, _options(), Resolver);

    await Assert.That(called).IsTrue();
    await Assert.That(ti).IsSameReferenceAs(sentinel);
  }

  [Test]
  public async Task ResolveDeserializeTypeInfo_ClaimHeader_IgnoresResolver_ReturnsClaimEnvelopeTypeAsync() {
    var called = false;
    JsonTypeInfo? Resolver(string name, JsonSerializerOptions o) { called = true; return null; }

    var ti = BodyClaimWireHelper.ResolveDeserializeTypeInfo(
      "Some.Type, Asm", isClaimHeaderValue: "true", _options(), Resolver);

    // Claim path resolves MessageEnvelope<BodyClaimEnvelopePayload> from options, not via the resolver.
    await Assert.That(called).IsFalse();
    await Assert.That(ti).IsNotNull();
    await Assert.That(ti!.Type).IsEqualTo(typeof(MessageEnvelope<BodyClaimEnvelopePayload>));
  }

  [Test]
  public async Task ResolveDeserializeTypeInfo_NonClaim_NullResolver_FallsBackToRegistryAsync() {
    // No resolver supplied → the helper uses JsonContextRegistry.GetTypeInfoByName. An unknown
    // type yields null (the existing 3-arg behavior is preserved).
    var ti = BodyClaimWireHelper.ResolveDeserializeTypeInfo(
      "Totally.Unknown.Type, NoAsm", isClaimHeaderValue: null, _options());

    await Assert.That(ti).IsNull();
  }

  private static JsonSerializerOptions _options() =>
    new() { TypeInfoResolver = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().TypeInfoResolver };

  private static JsonTypeInfo _stringTypeInfo() =>
    new JsonSerializerOptions { TypeInfoResolver = ClaimWireContext.Default }.GetTypeInfo(typeof(string));
}

[JsonSerializable(typeof(string))]
internal sealed partial class ClaimWireContext : JsonSerializerContext;
