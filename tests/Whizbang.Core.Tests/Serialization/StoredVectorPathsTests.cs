// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>A type only these tests register.</summary>
public sealed record StoredVectorPathsProbe(float[] V);

/// <summary>A second type only these tests register, to show a later registration replaces an earlier one.</summary>
public sealed record StoredVectorPathsReplaced(float[] V);

/// <summary>
/// Issue #2624: the registry of each event type's vector properties, as paths of JSON names from the event's payload.
/// The generated module initializer registers them; the stored-body packing (V2) reads them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/StoredVectorPaths.cs</code-under-test>
[Category("Serialization")]
public class StoredVectorPathsTests {
  [Test]
  public async Task ARegisteredType_ListsItsPaths_InTheirOrderAsync() {
    StoredVectorPaths.Register(typeof(StoredVectorPathsProbe), ["V"], ["Chunk", "emb"]);

    var paths = StoredVectorPaths.For(typeof(StoredVectorPathsProbe));

    await Assert.That(paths.Count).IsEqualTo(2);
    await Assert.That(string.Join("/", paths[0])).IsEqualTo("V");
    await Assert.That(string.Join("/", paths[1])).IsEqualTo("Chunk/emb");
    await Assert.That(StoredVectorPaths.EventTypes()).Contains(typeof(StoredVectorPathsProbe));
  }

  [Test]
  public async Task ATypeNeverRegistered_HasNoPathsAsync() {
    await Assert.That(StoredVectorPaths.For(typeof(StoredVectorPathsTests))).IsEmpty();
    await Assert.That(StoredVectorPaths.EventTypes()).DoesNotContain(typeof(StoredVectorPathsTests));
  }

  [Test]
  public async Task ALaterRegistration_ReplacesTheEarlierOneAsync() {
    StoredVectorPaths.Register(typeof(StoredVectorPathsReplaced), ["Old"]);
    StoredVectorPaths.Register(typeof(StoredVectorPathsReplaced), ["V"]);

    var paths = StoredVectorPaths.For(typeof(StoredVectorPathsReplaced));

    await Assert.That(paths.Count).IsEqualTo(1);
    await Assert.That(string.Join("/", paths[0])).IsEqualTo("V")
      .Because("every context that registers a type derives the same paths from the same declaration");
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    await Assert.That(() => StoredVectorPaths.Register(null!, ["V"])).Throws<ArgumentNullException>();
    await Assert.That(() => StoredVectorPaths.Register(typeof(StoredVectorPathsProbe), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => StoredVectorPaths.Register(typeof(StoredVectorPathsProbe), [null!])).Throws<ArgumentNullException>();
    await Assert.That(() => StoredVectorPaths.For(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AnEmptyPathOrSegment_IsRefusedAsync() {
    await Assert.That(() => StoredVectorPaths.Register(typeof(StoredVectorPathsProbe), [])).Throws<ArgumentException>()
      .Because("the generator registers only types that have a vector");
    await Assert.That(() => StoredVectorPaths.Register(typeof(StoredVectorPathsProbe), [[]])).Throws<ArgumentException>();
    await Assert.That(() => StoredVectorPaths.Register(typeof(StoredVectorPathsProbe), [["V", ""]])).Throws<ArgumentException>();
  }
}
