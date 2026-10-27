// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// The view the scope options bind through reads a blank value as absent at every depth, and is otherwise the
/// section itself: its key, path, writes and reload token are the section's.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.HotChocolate/Middleware/BlankAsUnsetConfigurationSection.cs</code-under-test>
public class BlankAsUnsetConfigurationSectionTests {
  private static IConfigurationRoot _root() =>
    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Scope:Blank"] = "",
      ["Scope:Spaces"] = "   ",
      ["Scope:Set"] = "tid",
      ["Scope:List:0"] = "",
      ["Scope:List:1"] = "groups",
    }).Build();

  [Test]
  public async Task BlankValues_ReadAsAbsent_AtEveryDepthAsync() {
    var view = new BlankAsUnsetConfigurationSection(_root().GetSection("Scope"));

    await Assert.That(view["Blank"]).IsNull();
    await Assert.That(view["Spaces"]).IsNull();
    await Assert.That(view["Set"]).IsEqualTo("tid");
    await Assert.That(view.GetSection("Spaces").Value).IsNull();
    await Assert.That(view.GetSection("List").GetChildren().Select(child => child.Value)).IsEquivalentTo([null, "groups"]);
  }

  [Test]
  public async Task EverythingElse_IsTheSectionsAsync() {
    var root = _root();
    var section = root.GetSection("Scope");
    var view = new BlankAsUnsetConfigurationSection(section);

    view["Written"] = "w";
    var child = view.GetSection("Set");
    child.Value = "replaced";

    await Assert.That(view.Key).IsEqualTo("Scope");
    await Assert.That(child.Path).IsEqualTo("Scope:Set");
    await Assert.That(root["Scope:Written"]).IsEqualTo("w");
    await Assert.That(root["Scope:Set"]).IsEqualTo("replaced");
    await Assert.That(view.GetReloadToken()).IsNotNull();
  }
}
