// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Registry;

/// <summary>
/// Pins the per-type stream-id registry (#1905, #1908): a message resolves by its exact runtime type, a type with no
/// entry takes its nearest registered base type's, the answer does not depend on registration order, and a cached
/// answer is recomputed once a nearer entry is registered.
/// </summary>
/// <remarks>
/// The registry is process-wide and has no reset, so every test uses message types of its own; no test can see
/// another's entries.
/// </remarks>
[Category("Core")]
[Category("Registry")]
public class StreamIdTypeRegistryTests {
  private static StreamIdTypeEntry _entry<T>() => new(typeof(T), _ => null, typeof(Guid));

  public class ExactBase;
  public class ExactDerived : ExactBase;

  [Test]
  public async Task Resolve_ATypeWithAnEntry_TakesItsOwnOverItsBasesAsync() {
    var baseEntry = _entry<ExactBase>();
    var derivedEntry = _entry<ExactDerived>();
    StreamIdTypeRegistry.Register(baseEntry);
    StreamIdTypeRegistry.Register(derivedEntry);

    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(ExactDerived))).IsSameReferenceAs(derivedEntry);
    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(ExactBase))).IsSameReferenceAs(baseEntry);
  }

  public class OrderBase;
  public class OrderDerived : OrderBase;

  /// <summary>The derived type's own entry wins even when it registered first: no answer depends on order.</summary>
  [Test]
  public async Task Resolve_ADerivedEntryRegisteredBeforeItsBase_StillWinsAsync() {
    var derivedEntry = _entry<OrderDerived>();
    StreamIdTypeRegistry.Register(derivedEntry);
    StreamIdTypeRegistry.Register(_entry<OrderBase>());

    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(OrderDerived))).IsSameReferenceAs(derivedEntry);
  }

  public class WalkRoot;
  public class WalkMiddle : WalkRoot;
  public class WalkLeaf : WalkMiddle;

  /// <summary>A type with no entry, such as a runtime subclass, takes its nearest registered base type's.</summary>
  [Test]
  public async Task Resolve_ATypeWithNoEntry_TakesItsNearestRegisteredBaseAsync() {
    var middle = _entry<WalkMiddle>();
    StreamIdTypeRegistry.Register(_entry<WalkRoot>(), middle);

    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(WalkLeaf))).IsSameReferenceAs(middle);
  }

  public class Unregistered;

  [Test]
  public async Task Resolve_ATypeWithNoEntryAnywhereInItsChain_ReturnsNullAsync() {
    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(Unregistered))).IsNull();
  }

  public class CachedBase;
  public class CachedLeaf : CachedBase;

  /// <summary>
  /// The base walk's answer is cached, and a later registration of a nearer entry advances the generation, so the next
  /// lookup takes it.
  /// </summary>
  [Test]
  public async Task Resolve_AfterANearerEntryRegisters_TakesItAsync() {
    var baseEntry = _entry<CachedBase>();
    StreamIdTypeRegistry.Register(baseEntry);
    var before = StreamIdTypeRegistry.Resolve(typeof(CachedLeaf));
    var cached = StreamIdTypeRegistry.Resolve(typeof(CachedLeaf));
    var generation = StreamIdTypeRegistry.Generation;

    var leafEntry = _entry<CachedLeaf>();
    StreamIdTypeRegistry.Register(leafEntry);

    await Assert.That(before).IsSameReferenceAs(baseEntry);
    await Assert.That(cached).IsSameReferenceAs(baseEntry);
    await Assert.That(StreamIdTypeRegistry.Generation).IsGreaterThan(generation);
    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(CachedLeaf))).IsSameReferenceAs(leafEntry);
  }

  public class RacedBase;
  public class RacedLeaf : RacedBase;

  /// <summary>
  /// A lookup made while a registration is visible but not yet counted (#1900) never leaves a stale answer behind: the
  /// lookup after the registration takes the new entry. Were the generation advanced before the entries were in place,
  /// the lookup inside the window would cache the base's entry against the new generation, and it would stick.
  /// </summary>
  [Test]
  public async Task Resolve_DuringARegistration_LeavesNoStaleAnswerAsync() {
    StreamIdTypeRegistry.Register(_entry<RacedBase>());
    _ = StreamIdTypeRegistry.Resolve(typeof(RacedLeaf));
    var leafEntry = _entry<RacedLeaf>();
    StreamIdTypeEntry? seenInside = null;

    using (StreamIdTypeRegistry.BeforePublishForTests(() => seenInside = StreamIdTypeRegistry.Resolve(typeof(RacedLeaf)))) {
      StreamIdTypeRegistry.Register(leafEntry);
    }

    await Assert.That(seenInside).IsNotNull()
      .Because("the lookup inside the window ran and found an answer, which the next generation replaces");
    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(RacedLeaf))).IsSameReferenceAs(leafEntry);
  }

  public class Twice;

  /// <summary>A type has one entry, from the assembly that compiles it; a second registration of it is ignored.</summary>
  [Test]
  public async Task Register_ATypeThatAlreadyHasAnEntry_KeepsTheFirstAsync() {
    var first = _entry<Twice>();
    StreamIdTypeRegistry.Register(first);
    StreamIdTypeRegistry.Register(_entry<Twice>());

    await Assert.That(StreamIdTypeRegistry.Resolve(typeof(Twice))).IsSameReferenceAs(first);
  }

  public class Accessed {
    public Guid Id { get; set; }
  }

  [Test]
  public async Task Entry_ReadsAndWritesThroughItsAccessorsAsync() {
    var id = new Guid("018f2a6e-0000-7000-8000-0000000019a5");
    var settable = new StreamIdTypeEntry(typeof(Accessed), m => ((Accessed)m).Id, typeof(Guid), shouldGenerate: true,
      onlyIfEmpty: true, set: (m, value, _) => { ((Accessed)m).Id = value; return true; });
    var readOnly = new StreamIdTypeEntry(typeof(Accessed), m => ((Accessed)m).Id, typeof(Guid));
    var message = new Accessed();

    var wrote = settable.Set(message, id, null);

    await Assert.That(wrote).IsTrue();
    await Assert.That(settable.Extract(message)).IsEqualTo(id);
    await Assert.That(settable.IsSettable).IsTrue();
    await Assert.That(settable.ShouldGenerate).IsTrue();
    await Assert.That(settable.OnlyIfEmpty).IsTrue();
    await Assert.That(settable.MessageType).IsEqualTo(typeof(Accessed));
    await Assert.That(settable.StreamIdType).IsEqualTo(typeof(Guid));
    await Assert.That(readOnly.IsSettable).IsFalse();
    await Assert.That(readOnly.Set(message, Guid.Empty, null)).IsFalse();
    await Assert.That(message.Id).IsEqualTo(id);
  }
}
