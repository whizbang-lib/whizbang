// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using EquatableArray = shared::Whizbang.Generators.Shared.Utilities.EquatableArray;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A generator model compares equal to the one the previous run produced only if every member does, and an array
/// compares by reference, so a model holding one is new on every run and regenerates its output on every edit.
/// <c>EquatableArray</c> compares by its elements, so a model holding one stays equal while its contents do.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("Generators")]
public class EquatableArrayTests {
  private sealed record Holder(shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> Items);

  [Test]
  public async Task TwoArrays_WithTheSameElements_AreEqualAndHashAlikeAsync() {
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> first = new[] { "a", "b" };
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> second = new[] { "a", "b" };

    await Assert.That(first.Equals(second)).IsTrue();
    await Assert.That(first.Equals((object)second)).IsTrue();
    await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
  }

  [Test]
  public async Task ARecordHoldingOne_ComparesByTheElementsAsync() {
    var first = new Holder(["a", "b"]);
    var second = new Holder(["a", "b"]);

    await Assert.That(first).IsEqualTo(second);
  }

  [Test]
  public async Task TheSameInstance_IsEqualToItselfAsync() {
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> array = new[] { "a" };

    await Assert.That(array.Equals(array)).IsTrue();
  }

  [Test]
  public async Task DifferentElements_OrADifferentLength_OrNull_AreNotEqualAsync() {
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> array = new[] { "a", "b" };

    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> otherElements = ["a", "c"];
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> shorter = ["a"];

    await Assert.That(array.Equals(otherElements)).IsFalse();
    await Assert.That(array.Equals(shorter)).IsFalse();
    await Assert.That(array.Equals("a")).IsFalse();
    await Assert.That(array.Equals((shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string>?)null)).IsFalse();
  }

  [Test]
  public async Task NullElements_CompareAndHashAsync() {
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string?> first = new[] { null, "a" };
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string?> second = new[] { null, "a" };

    await Assert.That(first.Equals(second)).IsTrue();
    await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
  }

  [Test]
  public async Task ANullArray_ConvertsToNullAsync() {
    string[]? none = null;
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string>? converted = none;

    await Assert.That(converted).IsNull();
  }

  [Test]
  public async Task ItReadsLikeTheArrayItWrapsAsync() {
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> array = new[] { "a", "b" };

    await Assert.That(array.Length).IsEqualTo(2);
    await Assert.That(array.Count).IsEqualTo(2);
    await Assert.That(array[1]).IsEqualTo("b");
    await Assert.That(array.ToList()).IsEquivalentTo(["a", "b"]);
    await Assert.That(((System.Collections.IEnumerable)array).Cast<string>().ToList()).IsEquivalentTo(["a", "b"]);
  }

  [Test]
  public async Task ACollectionExpression_BuildsOneAsync() {
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> built = ["a", "b"];
    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> empty = [];

    shared::Whizbang.Generators.Shared.Utilities.EquatableArray<string> fromArray = new[] { "a", "b" };

    await Assert.That(built.Equals(fromArray)).IsTrue();
    await Assert.That(empty.Length).IsEqualTo(0);
    await Assert.That(EquatableArray.Create<string>([]).Equals(empty)).IsTrue();
  }
}
