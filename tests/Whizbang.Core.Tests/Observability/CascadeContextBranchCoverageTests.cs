// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch coverage for <see cref="CascadeContext.WithMetadata(IReadOnlyDictionary{string,object})"/>
/// with entries to merge: onto a context that has no metadata yet (a fresh dictionary) and onto one
/// that does (a copy with the new entries winning). Either way the original is left untouched.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/CascadeContext.cs</code-under-test>
[Category("Observability")]
public class CascadeContextBranchCoverageTests {

  [Test]
  public async Task WithMetadata_Dictionary_OntoNoExistingMetadata_StartsAFreshDictionaryAsync() {
    var original = new CascadeContext { CorrelationId = CorrelationId.New(), CausationId = MessageId.New() };

    var result = original.WithMetadata(new Dictionary<string, object> { ["a"] = 1, ["b"] = "two" });

    await Assert.That(result.Metadata).IsNotNull();
    await Assert.That(result.Metadata!.Count).IsEqualTo(2);
    await Assert.That(result.Metadata["a"]).IsEqualTo(1);
    await Assert.That(result.Metadata["b"]).IsEqualTo("two");
    await Assert.That(original.Metadata).IsNull()
      .Because("contexts are immutable; merging produces a new one");
    await Assert.That(result.CorrelationId).IsEqualTo(original.CorrelationId);
  }

  [Test]
  public async Task WithMetadata_Dictionary_OntoExistingMetadata_MergesWithNewEntriesWinningAsync() {
    var original = new CascadeContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Metadata = new Dictionary<string, object> { ["kept"] = "old", ["replaced"] = "old" },
    };

    var result = original.WithMetadata(new Dictionary<string, object> { ["replaced"] = "new", ["added"] = 3 });

    await Assert.That(result.Metadata!.Count).IsEqualTo(3);
    await Assert.That(result.Metadata["kept"]).IsEqualTo("old")
      .Because("existing entries the merge does not mention carry over");
    await Assert.That(result.Metadata["replaced"]).IsEqualTo("new")
      .Because("on a key collision the merged-in value wins");
    await Assert.That(result.Metadata["added"]).IsEqualTo(3);
    await Assert.That(original.Metadata["replaced"]).IsEqualTo("old")
      .Because("the merge copies the dictionary rather than writing into the original's");
    await Assert.That(original.Metadata.Count).IsEqualTo(2);
  }
}
