// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Whizbang.Core.Configuration;

/// <summary>
/// A view of a configuration section with one child section hidden, so a generated binder leaves
/// that child to a binder of its own. Everything else reads through to the section.
/// </summary>
/// <remarks>
/// The generated configuration binder builds every bindable property it sees, and it cannot build
/// some (a positional record wants every constructor argument present, so a setting naming one
/// field fails startup). Hiding the child from it, rather than copying the rest into a new
/// configuration, keeps every read going to the real section.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterRecoveryPolicyBindingTests.cs</tests>
internal sealed class ConfigurationSectionWithout(IConfigurationSection section, string hiddenChild) : IConfigurationSection {
  private static readonly IConfigurationSection _empty = new ConfigurationBuilder().Build().GetSection("hidden");

  private bool _isHidden(string key) =>
    key.Equals(hiddenChild, StringComparison.OrdinalIgnoreCase)
    || key.StartsWith(hiddenChild + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);

  public string? this[string key] {
    get => _isHidden(key) ? null : section[key];
    set => section[key] = value;
  }

  public string Key => section.Key;

  public string Path => section.Path;

  public string? Value {
    get => section.Value;
    set => section.Value = value;
  }

  public IEnumerable<IConfigurationSection> GetChildren() =>
    section.GetChildren().Where(child => !_isHidden(child.Key));

  public IChangeToken GetReloadToken() => section.GetReloadToken();

  public IConfigurationSection GetSection(string key) => _isHidden(key) ? _empty : section.GetSection(key);
}
