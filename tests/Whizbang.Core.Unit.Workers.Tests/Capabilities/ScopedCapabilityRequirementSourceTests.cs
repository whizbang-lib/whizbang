// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// A source whose requirements apply to one scope only: a context's schema requires its extensions on the database
/// that context uses, not on every database the host probes.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/ICapabilityRequirementSource.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
public class ScopedCapabilityRequirementSourceTests {
  [Test]
  public async Task Resolve_IsTheInnerSourcesRequirements_InTheScopeAsync() {
    var vector = new CapabilityRequirement(new CapabilityId("postgres", "vector"), "Vector fields on perspectives", "wh_per_product.embedding", null, null);
    var inner = new FixedCapabilityRequirementSource([new ActiveCapabilityRequirement(vector, vector.Key, null, CapabilityActivation.Explicit)]);

    var scoped = new ScopedCapabilityRequirementSource(inner, "vectors");

    await Assert.That(scoped.Scope).IsEqualTo("vectors");
    await Assert.That(scoped.Resolve(null).Single().Scope).IsEqualTo("vectors");
    await Assert.That(scoped.Resolve(null).Single().Requirement).IsEqualTo(vector);
    await Assert.That(() => new ScopedCapabilityRequirementSource(null!, "vectors")).Throws<ArgumentNullException>();
    await Assert.That(() => new ScopedCapabilityRequirementSource(inner, null!)).Throws<ArgumentNullException>();
  }
}
