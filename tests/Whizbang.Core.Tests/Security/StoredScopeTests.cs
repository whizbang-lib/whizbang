// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// The scope a stored event row kept in its scope column, read back whole (#1855). Every read-back path that
/// turns a stored row into an envelope restores its scope through this helper, so none of them rebuilds less
/// than was stored: a receptor or perspective reading a scope extension after a replay, rebuild or redelivery
/// must see what the original dispatch saw.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Security/StoredScope.cs</code-under-test>
[Category("Security")]
public class StoredScopeTests {
  private static readonly JsonSerializerOptions _options = JsonContextRegistry.CreateCombinedOptions();

  private static PerspectiveScope _fullScope() => new() {
    TenantId = "tenant-1855",
    UserId = "user-1855",
    CustomerId = "customer-1855",
    OrganizationId = "org-1855",
    AllowedPrincipals = ["group:sales", "user:manager"],
    Extensions = [new ScopeExtension("region", "eu-west"), new ScopeExtension("tier", "gold")],
  };

  private static string _json(PerspectiveScope scope) =>
    JsonSerializer.Serialize(scope, _options.GetTypeInfo(typeof(PerspectiveScope)));

  private static MessageHop _hop(ScopeDelta? scope = null) => new() {
    Type = HopType.Current,
    ServiceInstance = new ServiceInstanceInfo {
      ServiceName = "StoredScopeTests",
      InstanceId = (Guid)TrackedGuid.New(),
      HostName = "test-host",
      ProcessId = 1,
    },
    Topic = "stored-scope-topic",
    Scope = scope,
  };

  // ---------------------------------------------------------------------------
  // Read
  // ---------------------------------------------------------------------------

  [Test]
  public async Task Read_NullOptions_ThrowsAsync() {
    await Assert.That(() => StoredScope.Read("{}", null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("null")]
  [Arguments("{}")]
  [Arguments("""{"t":null,"u":null,"c":null,"o":null}""")]
  public async Task Read_AnAbsentOrEmptyScope_ReadsAsNoScopeAsync(string? column) {
    await Assert.That(StoredScope.Read(column, _options)).IsNull()
      .Because("a column that names nothing is an absent scope; reading one back must never invent authority");
  }

  [Test]
  public async Task Read_TheShortKeyFormat_ReadsEveryFieldAsync() {
    var stored = _fullScope();

    var read = StoredScope.Read(_json(stored), _options);

    await Assert.That(read).IsNotNull();
    await Assert.That(_json(read!)).IsEqualTo(_json(stored))
      .Because("extensions and allowed principals are part of the stored scope and come back with it");
  }

  [Test]
  [Arguments(true, false)]
  [Arguments(false, true)]
  public async Task Read_AMarkerOnlyScope_KeepsTheMarkerAsync(bool system, bool declaredUnscoped) {
    var stored = new PerspectiveScope { IsSystem = system, IsDeclaredUnscoped = declaredUnscoped };

    var read = StoredScope.Read(_json(stored), _options);

    await Assert.That(read).IsNotNull()
      .Because("a marker states intent; reading it back as no scope restores the ambiguity it exists to remove");
    await Assert.That(read!.IsSystem).IsEqualTo(system);
    await Assert.That(read.IsDeclaredUnscoped).IsEqualTo(declaredUnscoped);
  }

  [Test]
  public async Task Read_TheLegacySnakeCaseFormat_ReadsTenantAndUserAsync() {
    var read = StoredScope.Read("""{"tenant_id":"legacy-tenant","user_id":"legacy-user"}""", _options);

    await Assert.That(read).IsNotNull();
    await Assert.That(read!.TenantId).IsEqualTo("legacy-tenant");
    await Assert.That(read.UserId).IsEqualTo("legacy-user");
    await Assert.That(read.CustomerId).IsNull();
    await Assert.That(read.Extensions).IsEmpty();
  }

  [Test]
  [Arguments("""{"t":123}""")]
  [Arguments("""{"tenant_id":5,"user_id":false}""")]
  public async Task Read_IdsThatAreNotStrings_ReadAsNoScopeAsync(string column) {
    await Assert.That(StoredScope.Read(column, _options)).IsNull()
      .Because("an unreadable id yields no scope; guessing one would attach the wrong tenant to a replayed event");
  }

  [Test]
  public async Task Read_AColumnThatIsNotAnObject_FailsLoudAsync() {
    await Assert.That(() => StoredScope.Read("[1]", _options)).Throws<JsonException>()
      .Because("neither format can read an array, and the caller decides how to report a row it cannot read");
  }

  /// <summary>A resolver that answers every type except <see cref="PerspectiveScope"/>.</summary>
  private sealed class ResolverWithoutPerspectiveScope(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver {
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
      type == typeof(PerspectiveScope) ? null : inner.GetTypeInfo(type, options);
  }

  [Test]
  public async Task Read_OptionsWithoutPerspectiveScope_StillReadTheIdsOfEitherFormatAsync() {
    var options = new JsonSerializerOptions(_options) {
      TypeInfoResolver = new ResolverWithoutPerspectiveScope(_options.TypeInfoResolver!)
    };

    var shortKeys = StoredScope.Read("""{"t":"t1","u":"u1","c":"c1","o":"o1"}""", options);
    var legacy = StoredScope.Read("""{"tenant_id":"t2","user_id":"u2"}""", options);

    await Assert.That(shortKeys).IsNotNull();
    await Assert.That(shortKeys!.TenantId).IsEqualTo("t1");
    await Assert.That(shortKeys.UserId).IsEqualTo("u1");
    await Assert.That(shortKeys.CustomerId).IsEqualTo("c1");
    await Assert.That(shortKeys.OrganizationId).IsEqualTo("o1");
    await Assert.That(legacy).IsNotNull();
    await Assert.That(legacy!.TenantId).IsEqualTo("t2");
    await Assert.That(legacy.UserId).IsEqualTo("u2");
  }

  // ---------------------------------------------------------------------------
  // RestoreInto
  // ---------------------------------------------------------------------------

  [Test]
  public async Task RestoreInto_NullHops_ThrowsAsync() {
    await Assert.That(() => StoredScope.RestoreInto(null!, _fullScope())).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task RestoreInto_NoHops_SynthesizesOneCarryingTheWholeScopeAsync() {
    var stored = _fullScope();
    List<MessageHop> hops = [];

    StoredScope.RestoreInto(hops, stored);

    await Assert.That(hops).Count().IsEqualTo(1)
      .Because("GetCurrentScope walks hops, so a scope with no hop to live on is a scope that does not exist");
    await Assert.That(hops[0].Type).IsEqualTo(HopType.Current);
    await Assert.That(hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
    var envelope = new MessageEnvelope<string> {
      MessageId = MessageId.New(), Payload = "p", Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await Assert.That(_json(envelope.GetCurrentScope()!.Scope)).IsEqualTo(_json(stored));
  }

  [Test]
  public async Task RestoreInto_AnUnscopedFirstHop_GivesItTheScopeAndKeepsTheRestAsync() {
    var original = _hop();
    List<MessageHop> hops = [original];

    StoredScope.RestoreInto(hops, _fullScope());

    await Assert.That(hops).Count().IsEqualTo(1);
    await Assert.That(hops[0].Scope).IsNotNull();
    await Assert.That(hops[0].Topic).IsEqualTo(original.Topic);
    await Assert.That(hops[0].ServiceInstance).IsEqualTo(original.ServiceInstance);
  }

  [Test]
  public async Task RestoreInto_AScopedFirstHop_IsLeftAsRecordedAsync() {
    var recorded = ScopeDelta.FromPerspectiveScope(new PerspectiveScope { TenantId = "recorded-tenant" });
    List<MessageHop> hops = [_hop(recorded)];

    StoredScope.RestoreInto(hops, _fullScope());

    await Assert.That(hops[0].Scope).IsSameReferenceAs(recorded)
      .Because("the hop recorded the scope it was dispatched with; the column must not overwrite it");
  }

  [Test]
  public async Task RestoreInto_NoScopeOrAnEmptyOne_LeavesTheHopsAloneAsync() {
    List<MessageHop> none = [];
    List<MessageHop> unscoped = [_hop()];

    StoredScope.RestoreInto(none, null);
    StoredScope.RestoreInto(none, new PerspectiveScope());
    StoredScope.RestoreInto(unscoped, new PerspectiveScope());

    await Assert.That(none).IsEmpty()
      .Because("an event stored without a scope stays unscoped; a synthesized hop would fabricate provenance");
    await Assert.That(unscoped[0].Scope).IsNull();
  }
}
