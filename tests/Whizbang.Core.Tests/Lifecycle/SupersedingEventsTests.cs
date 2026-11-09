// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// The generated, reflection-free registry of <see cref="SupersedingAttribute">superseding</see> event types and their
/// keys (#1729, WS-2 S12): which types supersede, and the key each event is stored with, typed or as received.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Lifecycle/SupersedingEvents.cs</code-under-test>
/// <docs>fundamentals/events/superseding-events</docs>
[NotInParallel("CurrentCulture")]
public class SupersedingEventsTests {
  [Superseding]
  public sealed record EmbeddingUpdated([property: StreamId] Guid DocumentId, [property: SupersedingKey] string? Model, int Dimensions) : IEvent;

  [Superseding]
  public sealed record PermissionsReplaced([property: StreamId] Guid UserId, int Revision) : IEvent;

  [Superseding]
  public record OwnerSnapshot([property: StreamId] Guid Id, [property: SupersedingKey] Guid Owner) : IEvent;

  public sealed record DerivedOwnerSnapshot(Guid Id, Guid Owner) : OwnerSnapshot(Id, Owner);

  public sealed record PlainEvent([property: StreamId] Guid Id) : IEvent;

  [Superseding]
  public sealed record RegionAssigned([property: StreamId] Guid Id, [property: SupersedingKey] Guid RegionId, int Revision) : IEvent;

  [Superseding]
  public sealed record SlotAssigned([property: StreamId] Guid Id, [property: SupersedingKey, JsonPropertyName("slot_no")] int Slot) : IEvent;

  [Superseding]
  public sealed record OptionalRegionAssigned([property: StreamId] Guid Id, [property: SupersedingKey] Guid? RegionId) : IEvent;

  [Superseding(Group = "region-state")]
  public sealed record RegionOpened([property: StreamId] Guid Id, [property: SupersedingKey] Guid RegionId) : IEvent;

  [Superseding(Group = "region-state")]
  public sealed record RegionRevised([property: StreamId] Guid Id, [property: SupersedingKey] Guid RegionId) : IEvent;

  /// <summary>An empty group name is no group: the type is its own group, as without one.</summary>
  [Superseding(Group = "")]
  public sealed record EmptyGroupNamed([property: StreamId] Guid Id) : IEvent;

  public sealed record GroupRegisteredByHand(string Name);

  /// <summary>Registered by hand through the overload code generated before #2123 calls: no key member name.</summary>
  public sealed record LegacyRegistered([property: StreamId] Guid Id, Guid Region) : IEvent;

  /// <summary>Registered by hand with a key member no JSON property of the type maps to.</summary>
  public sealed record UnmappedRegistered([property: StreamId] Guid Id, Guid Region) : IEvent;

  public sealed record RegisteredByHand(string Name);

  public sealed record NeverRegistered;

  private static readonly Guid _stream = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");
  private static readonly Guid _owner = Guid.Parse("0199a1b2-0000-7000-8000-0000000000aa");

  private static string _wireName(Type type) => type.FullName + ", " + type.Assembly.GetName().Name;

  [Test]
  public async Task AttributedTypesInThisAssembly_AreRegisteredByTheGenerator_WithTheirKeyAsync() {
    await Assert.That(SupersedingEvents.IsSuperseding(typeof(EmbeddingUpdated))).IsTrue()
      .Because("the catalog generator's module initializer registers it before any test runs");
    await Assert.That(SupersedingEvents.IsSuperseding(typeof(DerivedOwnerSnapshot))).IsTrue()
      .Because("the attribute is inherited, so an event derived from a superseding event supersedes too");
    await Assert.That(SupersedingEvents.IsSuperseding(typeof(PlainEvent))).IsFalse();

    await Assert.That(SupersedingEvents.KeyOf(new EmbeddingUpdated(_stream, "model-a", 3))).IsEqualTo("model-a");
    await Assert.That(SupersedingEvents.KeyOf(new DerivedOwnerSnapshot(_stream, _owner))).IsEqualTo(_owner.ToString("D"))
      .Because("a key property declared on a base type is the derived type's key too");
    await Assert.That(SupersedingEvents.KeyOf(new PermissionsReplaced(_stream, 2))).IsEqualTo(string.Empty)
      .Because("a superseding type with no [SupersedingKey] has one key per stream, the empty key");
    await Assert.That(SupersedingEvents.KeyOf(new PlainEvent(_stream))).IsNull()
      .Because("the control: an event that is not superseding is stored with no key at all");
    await Assert.That(SupersedingEvents.KeyOf(null)).IsNull();
  }

  [Test]
  public async Task KeyOf_AKeyPropertyThatIsNull_IsNull_SoTheEventIsNeverSupersededAsync() =>
    await Assert.That(SupersedingEvents.KeyOf(new EmbeddingUpdated(_stream, null, 3))).IsNull()
      .Because("an event whose key is unknown is kept, rather than merged with every other keyless event of its type");

  [Test]
  public async Task Register_ByHand_IsHarmlessTwice_AndTheFirstRegistrationWinsAsync() {
    SupersedingEvents.Register(typeof(RegisteredByHand), static p => ((RegisteredByHand)p).Name);
    SupersedingEvents.Register(typeof(RegisteredByHand), static _ => "second");

    await Assert.That(SupersedingEvents.IsSuperseding(typeof(RegisteredByHand))).IsTrue();
    await Assert.That(SupersedingEvents.KeyOf(new RegisteredByHand("first"))).IsEqualTo("first");
    await Assert.That(SupersedingEvents.IsSuperseding(typeof(NeverRegistered))).IsFalse();
  }

  [Test]
  public async Task KeyOf_AReceivedJsonPayload_IsReadThroughItsTypeName_AsTheTypedPayloadIsAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var keyed = JsonSerializer.SerializeToElement(new EmbeddingUpdated(_stream, "model-a", 3), options.GetTypeInfo(typeof(EmbeddingUpdated)));
    var keyless = JsonSerializer.SerializeToElement(new PermissionsReplaced(_stream, 2), options.GetTypeInfo(typeof(PermissionsReplaced)));
    var plain = JsonSerializer.SerializeToElement(new PlainEvent(_stream), options.GetTypeInfo(typeof(PlainEvent)));

    await Assert.That(SupersedingEvents.KeyOf(keyed, _wireName(typeof(EmbeddingUpdated)), options)).IsEqualTo("model-a")
      .Because("a received payload is still JSON, so the key is read from the payload deserialized as its registered type");
    await Assert.That(SupersedingEvents.KeyOf(keyed, _wireName(typeof(EmbeddingUpdated)), jsonOptions: null)).IsEqualTo("model-a")
      .Because("a host that registers no options still reads the key through the combined registered contexts");
    await Assert.That(SupersedingEvents.KeyOf(keyless, _wireName(typeof(PermissionsReplaced)), options)).IsEqualTo(string.Empty);
    await Assert.That(SupersedingEvents.KeyOf(plain, _wireName(typeof(PlainEvent)), options)).IsNull();
    await Assert.That(SupersedingEvents.KeyOf(keyed, wireTypeName: null, options)).IsNull()
      .Because("without a type name a JSON payload cannot be read as any type");
    await Assert.That(SupersedingEvents.KeyOf(JsonElement.Parse("null"), _wireName(typeof(EmbeddingUpdated)), options)).IsNull()
      .Because("a null payload has no key");
    await Assert.That(SupersedingEvents.KeyOf(new EmbeddingUpdated(_stream, "model-b", 3), _wireName(typeof(EmbeddingUpdated)), options))
      .IsEqualTo("model-b").Because("a typed payload is read directly, whatever name came with it");
  }

  /// <summary>
  /// #2123: a value-type key absent from a received or stored payload must not read as its type's default, or every such
  /// event of a stream falls into one key group and all but the newest are reaped. Absence (or JSON null) is no key; a
  /// present default is a real key.
  /// </summary>
  [Test]
  public async Task KeyOf_AValueTypeKeyMissingFromTheJson_IsNull_ButAPresentDefaultIsAKeyAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var wire = _wireName(typeof(RegionAssigned));
    var missing = JsonElement.Parse($$"""{"Id":"{{_stream}}","Revision":1}""");
    var explicitNull = JsonElement.Parse($$"""{"Id":"{{_stream}}","RegionId":null,"Revision":1}""");
    var presentDefault = JsonElement.Parse($$"""{"Id":"{{_stream}}","RegionId":"{{Guid.Empty}}","Revision":1}""");
    var otherCase = JsonElement.Parse($$"""{"Id":"{{_stream}}","regionId":"{{_owner}}","Revision":1}""");

    await Assert.That(SupersedingEvents.KeyOf(missing, wire, options)).IsNull()
      .Because("a key the payload does not carry is unknown, never the Guid default every other such event would share");
    await Assert.That(SupersedingEvents.KeyOf(explicitNull, wire, options)).IsNull()
      .Because("a JSON null key is unknown too, and is read without throwing for the non-nullable property");
    await Assert.That(SupersedingEvents.KeyOf(presentDefault, wire, options)).IsEqualTo(Guid.Empty.ToString("D"))
      .Because("a default value the payload does carry is a real key");
    await Assert.That(SupersedingEvents.KeyOf(otherCase, wire, options)).IsEqualTo(_owner.ToString("D"))
      .Because("the registered options read property names case-insensitively, and so does the presence check");
    await Assert.That(SupersedingEvents.KeyOf(missing, wire, jsonOptions: null)).IsNull()
      .Because("the combined registered contexts are checked the same way when no options are given");

    await Assert.That(SupersedingEvents.IsKeyMissing(missing, wire, options)).IsTrue();
    await Assert.That(SupersedingEvents.IsKeyMissing(explicitNull, wire, options)).IsTrue();
    await Assert.That(SupersedingEvents.IsKeyMissing(presentDefault, wire, options)).IsFalse();
    await Assert.That(SupersedingEvents.IsKeyMissing(otherCase, wire, options)).IsFalse();
    await Assert.That(SupersedingEvents.IsKeyMissing(missing, wire, jsonOptions: null)).IsTrue();
  }

  [Test]
  public async Task KeyOf_AKeyWithAJsonPropertyName_IsLookedForUnderThatNameAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var wire = _wireName(typeof(SlotAssigned));
    var underItsJsonName = JsonElement.Parse($$"""{"Id":"{{_stream}}","slot_no":0}""");
    var underItsClrName = JsonElement.Parse($$"""{"Id":"{{_stream}}","Slot":7}""");

    await Assert.That(SupersedingEvents.KeyOf(underItsJsonName, wire, options)).IsEqualTo("0")
      .Because("the key is written under its [JsonPropertyName], and a present zero is a key");
    await Assert.That(SupersedingEvents.KeyOf(underItsClrName, wire, options)).IsNull()
      .Because("a property under the CLR name is not the key the serializer reads, so the key is missing");
  }

  [Test]
  public async Task KeyOf_AContractWithAttributeProviders_HonorsItsNamingPolicyAsync() {
    // Metadata that knows its members (the reflection resolver, or System.Text.Json's own source generator) maps the
    // key member to its JSON name through the attribute provider, so the naming policy applies.
    var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver(), PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var wire = _wireName(typeof(RegionAssigned));
    var camel = JsonElement.Parse($$"""{"id":"{{_stream}}","regionId":"{{_owner}}","revision":1}""");
    var pascal = JsonElement.Parse($$"""{"id":"{{_stream}}","RegionId":"{{_owner}}","revision":1}""");
    var slot = JsonElement.Parse($$"""{"id":"{{_stream}}","slot_no":3}""");

    await Assert.That(SupersedingEvents.KeyOf(camel, wire, options)).IsEqualTo(_owner.ToString("D"));
    await Assert.That(SupersedingEvents.KeyOf(pascal, wire, options)).IsNull()
      .Because("under a camelCase policy and case-sensitive options the key is regionId, so RegionId is not it");
    await Assert.That(SupersedingEvents.KeyOf(slot, _wireName(typeof(SlotAssigned)), options)).IsEqualTo("3")
      .Because("[JsonPropertyName] wins over the naming policy");
  }

  [Test]
  public async Task KeyOf_ANullableKey_IsNullWhenMissingOrNull_AndItsValueWhenPresentAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var wire = _wireName(typeof(OptionalRegionAssigned));

    await Assert.That(SupersedingEvents.KeyOf(JsonElement.Parse($$"""{"Id":"{{_stream}}"}"""), wire, options)).IsNull();
    await Assert.That(SupersedingEvents.KeyOf(JsonElement.Parse($$"""{"Id":"{{_stream}}","RegionId":null}"""), wire, options)).IsNull();
    await Assert.That(SupersedingEvents.KeyOf(JsonElement.Parse($$"""{"Id":"{{_stream}}","RegionId":"{{_owner}}"}"""), wire, options))
      .IsEqualTo(_owner.ToString("D"));
  }

  [Test]
  public async Task KeyOf_ARegistrationWithoutAMappableKeyMember_ReadsThePayloadAsBeforeAsync() {
    // Code generated before #2123 registers no key member name, and a member no JSON property maps to cannot be checked
    // for presence: both read the key by deserializing, as they always did, rather than refusing every key.
    SupersedingEvents.Register(typeof(LegacyRegistered), static p => SupersedingEvents.KeyText(((LegacyRegistered)p).Region));
    SupersedingEvents.Register(typeof(UnmappedRegistered), static p => SupersedingEvents.KeyText(((UnmappedRegistered)p).Region), "NoSuchMember", null);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var missing = JsonElement.Parse($$"""{"Id":"{{_stream}}"}""");

    await Assert.That(SupersedingEvents.KeyOf(missing, _wireName(typeof(LegacyRegistered)), options)).IsEqualTo(Guid.Empty.ToString("D"));
    await Assert.That(SupersedingEvents.KeyOf(missing, _wireName(typeof(UnmappedRegistered)), options)).IsEqualTo(Guid.Empty.ToString("D"));
    await Assert.That(SupersedingEvents.IsKeyMissing(missing, _wireName(typeof(LegacyRegistered)), options)).IsFalse();
    await Assert.That(SupersedingEvents.IsKeyMissing(missing, _wireName(typeof(UnmappedRegistered)), options)).IsFalse();
  }

  [Test]
  public async Task IsKeyMissing_IsFalse_WhereThereIsNoKeyToMissAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var empty = JsonElement.Parse("{}");

    await Assert.That(SupersedingEvents.IsKeyMissing(empty, _wireName(typeof(PermissionsReplaced)), options)).IsFalse()
      .Because("a superseding type with no key property has the empty key, which no payload can miss");
    await Assert.That(SupersedingEvents.IsKeyMissing(empty, _wireName(typeof(PlainEvent)), options)).IsFalse()
      .Because("an event that is not superseding has no key at all");
    await Assert.That(SupersedingEvents.IsKeyMissing(empty, wireTypeName: null, options)).IsFalse();
    await Assert.That(SupersedingEvents.IsKeyMissing(JsonElement.Parse("null"), _wireName(typeof(RegionAssigned)), options)).IsFalse()
      .Because("a payload that is not an object is no key-less event; it is read as before, and a null payload has no key");
  }

  /// <summary>
  /// #2124: a superseding group lets several types supersede each other. The generator registers each type's group, an
  /// explicit name that is the same whatever the CLR type is called, so the write paths stamp it beside the key.
  /// </summary>
  [Test]
  public async Task GroupOf_AGroupedType_IsItsDeclaredGroup_AnUngroupedOrUnregisteredTypeHasNoneAsync() {
    await Assert.That(SupersedingEvents.GroupOf(typeof(RegionOpened))).IsEqualTo("region-state");
    await Assert.That(SupersedingEvents.GroupOf(typeof(RegionRevised))).IsEqualTo("region-state");
    await Assert.That(SupersedingEvents.GroupOf(typeof(EmbeddingUpdated))).IsNull()
      .Because("without a group a type is its own group, which the store reads as its event type");
    await Assert.That(SupersedingEvents.GroupOf(typeof(PlainEvent))).IsNull();
    await Assert.That(SupersedingEvents.GroupOf(typeof(EmptyGroupNamed))).IsNull()
      .Because("an empty name is no group, so it can never merge every type that leaves it empty");
    await Assert.That(SupersedingEvents.IsSuperseding(typeof(EmptyGroupNamed))).IsTrue();
    await Assert.That(() => SupersedingEvents.GroupOf((Type)null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SupersedingAttribute_Group_IsNoneUnlessNamed_AndReadsBackAsNamedAsync() {
    var named = new SupersedingAttribute { Group = "region-state" };

    await Assert.That(named.Group).IsEqualTo("region-state");
    await Assert.That(new SupersedingAttribute().Group).IsNull().Because("without a group a type is its own group");
  }

  [Test]
  public async Task GroupOf_APayload_TypedOrReceived_IsItsTypesGroupAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var revised = new RegionRevised(_stream, _owner);
    var json = JsonSerializer.SerializeToElement(revised, options.GetTypeInfo(typeof(RegionRevised)));

    await Assert.That(SupersedingEvents.GroupOf(revised)).IsEqualTo("region-state");
    await Assert.That(SupersedingEvents.GroupOf((object?)null)).IsNull();
    await Assert.That(SupersedingEvents.GroupOf(new PlainEvent(_stream))).IsNull();
    await Assert.That(SupersedingEvents.GroupOf(json, _wireName(typeof(RegionRevised)))).IsEqualTo("region-state")
      .Because("a received payload is still JSON, so its group is found by its type name");
    await Assert.That(SupersedingEvents.GroupOf(json, wireTypeName: null)).IsNull();
    await Assert.That(SupersedingEvents.GroupOf(json, _wireName(typeof(PlainEvent)))).IsNull();
    await Assert.That(SupersedingEvents.GroupOf(revised, wireTypeName: null)).IsEqualTo("region-state")
      .Because("a typed payload is read directly, whatever name came with it");
  }

  [Test]
  public async Task Register_WithAGroup_ByHand_KeepsTheGroup_TheOlderOverloadsRegisterNoneAsync() {
    SupersedingEvents.Register(typeof(GroupRegisteredByHand), static p => ((GroupRegisteredByHand)p).Name, "Name", null, "by-hand");

    await Assert.That(SupersedingEvents.GroupOf(typeof(GroupRegisteredByHand))).IsEqualTo("by-hand");
    await Assert.That(SupersedingEvents.KeyOf(new GroupRegisteredByHand("n"))).IsEqualTo("n");
    await Assert.That(SupersedingEvents.GroupOf(typeof(LegacyRegistered))).IsNull()
      .Because("code generated before #2124 registers no group, so its types stay their own group");
  }

  [Test]
  public async Task KeyText_IsInvariantText_WhateverTheCurrentCultureAsync() {
    var culture = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    try {
      await Assert.That(SupersedingEvents.KeyText(1.5m)).IsEqualTo("1.5")
        .Because("a host's culture must never split one key into two");
      await Assert.That(SupersedingEvents.KeyText(42)).IsEqualTo("42");
      await Assert.That(SupersedingEvents.KeyText(_owner)).IsEqualTo(_owner.ToString("D"));
      await Assert.That(SupersedingEvents.KeyText("as-is")).IsEqualTo("as-is");
      await Assert.That(SupersedingEvents.KeyText(new RegisteredByHand("x"))).IsEqualTo(new RegisteredByHand("x").ToString())
        .Because("a value that is not formattable is its own ToString");
      await Assert.That(SupersedingEvents.KeyText(null)).IsNull();
    } finally {
      CultureInfo.CurrentCulture = culture;
    }
  }

  /// <summary>
  /// #2110: the history reconcile walks the registered superseding types, so the registry lists them; an ordinary event
  /// is never among them, and a later registration appears in the next listing.
  /// </summary>
  [Test]
  public async Task Types_ListsEveryRegisteredSupersedingType_AndNoOtherAsync() {
    var types = SupersedingEvents.Types();

    await Assert.That(types).Contains(typeof(EmbeddingUpdated));
    await Assert.That(types).Contains(typeof(PermissionsReplaced));
    await Assert.That(types).DoesNotContain(typeof(PlainEvent)).Because("only a [Superseding] event is listed");
    await Assert.That(types).DoesNotContain(typeof(NeverRegistered));
  }

  [Test]
  public async Task NullType_ThrowsAsync() {
    await Assert.That(() => SupersedingEvents.Register(null!, null)).Throws<ArgumentNullException>();
    await Assert.That(() => SupersedingEvents.IsSuperseding(null!)).Throws<ArgumentNullException>();
  }
}
