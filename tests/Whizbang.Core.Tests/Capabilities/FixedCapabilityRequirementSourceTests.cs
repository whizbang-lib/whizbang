// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// A source whose requirements are in force whatever configuration says: a schema that declares a vector column
/// requires pgvector because the column cannot exist without it, not because a setting turned anything on.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Capabilities/ICapabilityRequirementSource.cs</code-under-test>
/// <docs>operations/infrastructure/capabilities#requirement-sources</docs>
public class FixedCapabilityRequirementSourceTests {
  [Test]
  public async Task Resolve_IsTheGivenRequirements_WhateverTheConfigurationAsync() {
    var vector = new CapabilityRequirement(new CapabilityId("postgres", "vector"), "Vector fields on perspectives", "wh_per_product.embedding", null, null);
    ActiveCapabilityRequirement[] inForce = [new(vector, vector.Key, null, CapabilityActivation.Explicit)];
    var source = new FixedCapabilityRequirementSource(inForce);

    await Assert.That(source.Resolve(null)).IsEquivalentTo(inForce);
    await Assert.That(source.Resolve(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())).IsEquivalentTo(inForce);
    await Assert.That(() => new FixedCapabilityRequirementSource(null!)).Throws<ArgumentNullException>();
  }
}
