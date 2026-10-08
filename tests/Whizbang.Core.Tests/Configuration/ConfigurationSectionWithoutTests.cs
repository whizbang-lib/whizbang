// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// A section view that hides one child: the hidden child reads as absent through every member, and
/// everything else reads and writes through to the real section.
/// </summary>
public class ConfigurationSectionWithoutTests {
  private static IConfigurationSection _section() =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Outer:Inner:Plain"] = "visible",
        ["Outer:Inner:Hidden:Field"] = "secret",
        ["Outer:Inner:HiddenNot"] = "kept"
      })
      .Build()
      .GetSection("Outer:Inner");

  [Test]
  public async Task Indexer_HidesTheChildAndEverythingUnderItAsync() {
    var view = new ConfigurationSectionWithout(_section(), "Hidden");

    await Assert.That(view["Plain"]).IsEqualTo("visible");
    await Assert.That(view["hidden:Field"]).IsNull();
    await Assert.That(view["HiddenNot"]).IsEqualTo("kept");
  }

  [Test]
  public async Task GetChildren_LeavesTheHiddenChildOutAsync() {
    var view = new ConfigurationSectionWithout(_section(), "HIDDEN");

    await Assert.That(view.GetChildren().Select(c => c.Key)).IsEquivalentTo(["Plain", "HiddenNot"]);
  }

  [Test]
  public async Task GetSection_ReturnsAnEmptySectionForTheHiddenChildAsync() {
    var view = new ConfigurationSectionWithout(_section(), "Hidden");

    await Assert.That(view.GetSection("Hidden").Exists()).IsFalse();
    await Assert.That(view.GetSection("Hidden:Field").Value).IsNull();
    await Assert.That(view.GetSection("Plain").Value).IsEqualTo("visible");
  }

  [Test]
  public async Task IdentityAndValue_ReadThroughToTheSectionAsync() {
    var section = _section();
    var view = new ConfigurationSectionWithout(section, "Hidden");

    await Assert.That(view.Key).IsEqualTo("Inner");
    await Assert.That(view.Path).IsEqualTo("Outer:Inner");
    await Assert.That(view.Value).IsNull();
    await Assert.That(view.GetReloadToken()).IsNotNull();
  }

  [Test]
  public async Task Writes_GoToTheSectionAsync() {
    var section = _section();
    var view = new ConfigurationSectionWithout(section, "Hidden") {
      ["Plain"] = "changed",
      Value = "own"
    };

    await Assert.That(section["Plain"]).IsEqualTo("changed");
    await Assert.That(section.Value).IsEqualTo("own");
    await Assert.That(view.Value).IsEqualTo("own");
  }
}
