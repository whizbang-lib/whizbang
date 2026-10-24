// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// A view of a configuration section in which a blank value (empty or whitespace) reads as absent, at every depth, so
/// a binder leaves the property it would have set alone.
/// </summary>
/// <remarks>
/// A key present with an empty value is what an appsettings placeholder or an environment variable set to nothing
/// delivers. Bound directly, a blank claim type replaced the scope's claim-type list with one name that never matches,
/// and the request lost its tenant, user, roles or permissions with nothing logged. No scope setting has a meaning for
/// a blank value, so blank is no setting (#1660).
/// </remarks>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/HotChocolateOptionsConfigurationBindingTests.cs:Scope_BlankKeys_LeaveTheDefaultsAndTheCodeValuesAsync</tests>
internal sealed class BlankAsUnsetConfigurationSection(IConfigurationSection section) : IConfigurationSection {
  public string? this[string key] {
    get => _unlessBlank(section[key]);
    set => section[key] = value;
  }

  public string Key => section.Key;

  public string Path => section.Path;

  public string? Value {
    get => _unlessBlank(section.Value);
    set => section.Value = value;
  }

  public IEnumerable<IConfigurationSection> GetChildren() =>
    section.GetChildren().Select(child => new BlankAsUnsetConfigurationSection(child));

  public IChangeToken GetReloadToken() => section.GetReloadToken();

  public IConfigurationSection GetSection(string key) => new BlankAsUnsetConfigurationSection(section.GetSection(key));

  private static string? _unlessBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
