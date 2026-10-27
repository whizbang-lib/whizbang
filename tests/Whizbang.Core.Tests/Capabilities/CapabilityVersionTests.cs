// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// Whether an available version is newer than the installed one, which decides an upgrade: dot-separated
/// segments compared in order, numerically when both are numbers and ordinally otherwise, a longer version newer
/// than its own prefix.
/// </summary>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
public class CapabilityVersionTests {
  [Test]
  [Arguments("1.6", "1.5", true)]
  [Arguments("1.10", "1.9", true)]
  [Arguments("1.9", "1.10", false)]
  [Arguments("1.6", "1.6", false)]
  [Arguments("1.6.1", "1.6", true)]
  [Arguments("1.6", "1.6.1", false)]
  [Arguments("2.0", "1.99", true)]
  [Arguments("1.0beta2", "1.0beta1", true)]
  [Arguments("1.0beta1", "1.0beta2", false)]
  [Arguments("1.0", "1.0beta1", false)]
  public async Task IsNewer_ComparesSegmentBySegmentAsync(string candidate, string current, bool newer) {
    await Assert.That(CapabilityVersion.IsNewer(candidate, current)).IsEqualTo(newer);
  }

  [Test]
  public async Task AnUnknownVersion_IsNeverNewerAsync() {
    await Assert.That(CapabilityVersion.IsNewer(null, "1.0")).IsFalse();
    await Assert.That(CapabilityVersion.IsNewer("1.0", null)).IsFalse();
  }
}
