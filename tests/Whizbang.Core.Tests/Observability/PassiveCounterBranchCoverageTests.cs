// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch backfill for <see cref="PassiveCounter{T}.TagSet"/>: null tag values (hash and equality)
/// and a key mismatch between two same-length tag sets.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/PassiveCounter.cs</code-under-test>
[Category("Shard2")]
public class PassiveCounterBranchCoverageTests {

  [Test]
  public async Task TagSet_NullValuedTags_AreEqualAndHashAlikeAsync() {
    var a = new PassiveCounter<long>.TagSet([new("kind", null)]);
    var b = new PassiveCounter<long>.TagSet([new("kind", null)]);

    await Assert.That(a.Equals(b)).IsTrue()
      .Because("two adds with the same null-valued tag must land on one series");
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
  }

  [Test]
  public async Task TagSet_NullValue_DiffersFromEmptyStringValueAsync() {
    var nullValued = new PassiveCounter<long>.TagSet([new("kind", null)]);
    var emptyValued = new PassiveCounter<long>.TagSet([new("kind", "")]);

    await Assert.That(nullValued.Equals(emptyValued)).IsFalse()
      .Because("a null tag value compares as no text, which is not the empty string");
    await Assert.That(emptyValued.Equals(nullValued)).IsFalse();
  }

  [Test]
  public async Task TagSet_SameLengthDifferentKey_IsADifferentSeriesAsync() {
    var a = new PassiveCounter<long>.TagSet([new("kind", "alpha")]);
    var otherKey = new PassiveCounter<long>.TagSet([new("verdict", "alpha")]);

    await Assert.That(a.Equals(otherKey)).IsFalse()
      .Because("the same value under a different key is a different series");
  }
}
