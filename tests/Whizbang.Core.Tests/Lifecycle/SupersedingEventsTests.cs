// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
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

  [Test]
  public async Task NullType_ThrowsAsync() {
    await Assert.That(() => SupersedingEvents.Register(null!, null)).Throws<ArgumentNullException>();
    await Assert.That(() => SupersedingEvents.IsSuperseding(null!)).Throws<ArgumentNullException>();
  }
}
