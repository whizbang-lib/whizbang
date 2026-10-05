// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Minting;

/// <summary>
/// The raw-carry view of <see cref="AuditEventsComposite"/> that fan-out reads.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Minting/AuditEventsComposite.cs</code-under-test>
public class AuditEventsCompositeTests {
  /// <summary>
  /// A composite minted by an older build carries no per-child stream ids. Fan-out must then see
  /// "none" (null), so each child inherits the composite's stream, rather than an empty list it
  /// would index into; a composite that has them hands them through unchanged.
  /// </summary>
  [Test]
  public async Task InnerStreamIds_EmptyReadsAsNone_PresentPassThroughAsync() {
    var streamId = Guid.CreateVersion7();
    IRawInnerComposite legacy = new AuditEventsComposite();
    IRawInnerComposite current = new AuditEventsComposite { InnerStreamIds = [streamId] };

    await Assert.That(legacy.InnerStreamIds).IsNull();
    await Assert.That(current.InnerStreamIds).IsEquivalentTo([streamId]);
  }
}
