// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// How a rebuild's refused write is read (#2252): a fold whose document equals the stored row's left the read model as
/// it should be, and any other is a write the ordering guard refused.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRowUnchanged.cs</code-under-test>
public class PerspectiveRowUnchangedTests {
  [Test]
  public async Task OfRefusedWrite_AFoldEqualToTheStoredRow_IsAlreadyCorrectAsync() {
    using var folded = JsonDocument.Parse("""{"v":1,"model":{"a":1,"b":[1,2]}}""");
    using var stored = JsonDocument.Parse("""{"model":{"b":[1,2],"a":1},"v":1}""");

    await Assert.That(PerspectiveRowUnchanged.OfRefusedWrite(folded, stored))
      .IsEqualTo(PerspectiveRowUnchangedReason.AlreadyCorrect)
      .Because("property order does not make two documents different");
  }

  [Test]
  public async Task OfRefusedWrite_AFoldThatDiffersFromTheStoredRow_IsAWriteRefusedAsync() {
    using var folded = JsonDocument.Parse("""{"a":1}""");
    using var stored = JsonDocument.Parse("""{"a":2}""");

    await Assert.That(PerspectiveRowUnchanged.OfRefusedWrite(folded, stored))
      .IsEqualTo(PerspectiveRowUnchangedReason.WriteRefused);
  }

  [Test]
  public async Task OfRefusedWrite_WithNoStoredRow_IsAWriteRefusedAsync() {
    using var folded = JsonDocument.Parse("""{"a":1}""");

    await Assert.That(PerspectiveRowUnchanged.OfRefusedWrite(folded, null))
      .IsEqualTo(PerspectiveRowUnchangedReason.WriteRefused);
  }

  [Test]
  public async Task OfRefusedWrite_WithNoFold_IsAWriteRefusedAsync() {
    using var stored = JsonDocument.Parse("""{"a":1}""");

    await Assert.That(PerspectiveRowUnchanged.OfRefusedWrite(null, stored))
      .IsEqualTo(PerspectiveRowUnchangedReason.WriteRefused);
  }
}
