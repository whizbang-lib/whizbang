// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>The digest exemption (#1492) names exactly the audit system events.</summary>
[Category("SystemEvents")]
public class DigestExemptTypesTests {
  [Test]
  public async Task TypeNames_AreTheAuditSystemEventsAsync() {
    await Assert.That(DigestExemptTypes.TypeNames).IsEquivalentTo([
      "Whizbang.Core.SystemEvents.EventAudited",
      "Whizbang.Core.SystemEvents.CommandAudited",
    ]);
  }
}
