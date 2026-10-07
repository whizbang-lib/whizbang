// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Documentation.Tests;

/// <summary>
/// A configuration source that holds no values and records every key the library asks it for.
/// </summary>
/// <remarks>
/// Every section reports one child, <see cref="PLACEHOLDER"/>, so a binder that first checks whether
/// its section exists goes on to read its keys. A key read beneath the placeholder means the binder
/// enumerated that section's children: the names there are the consumer's own (one entry per
/// subscription, per provider, per handler), and the manifest writes that segment as <c>*</c>.
/// </remarks>
internal sealed class ConfigurationKeyRecorder : ConfigurationProvider, IConfigurationSource {
  internal const string PLACEHOLDER = "__any__";

  private readonly ConcurrentDictionary<string, byte> _reads = new(StringComparer.OrdinalIgnoreCase);
  private readonly IReadOnlyDictionary<string, string> _seeds;

  /// <summary>Creates a recorder that answers only the given seed keys.</summary>
  /// <param name="seeds">
  /// Values for the few keys that gate further reads (a cipher name, for one), so the reads behind
  /// the gate run and are recorded too. Everything else reads as absent.
  /// </param>
  public ConfigurationKeyRecorder(IReadOnlyDictionary<string, string>? seeds = null) {
    _seeds = seeds ?? new Dictionary<string, string>();
  }

  /// <summary>Every key read, with consumer-named segments written as <c>*</c>.</summary>
  public IReadOnlyCollection<string> Keys =>
    [.. _reads.Keys.Select(k => string.Join(':', k.Split(':').Select(s => s == PLACEHOLDER ? "*" : s)))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Order(StringComparer.OrdinalIgnoreCase)];

  public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

  public override bool TryGet(string key, out string? value) {
    _reads.TryAdd(key, 0);
    return _seeds.TryGetValue(key, out value);
  }

  public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) {
    // One placeholder level per section, never beneath a placeholder: enough for a binder to see
    // its section and enumerate it once, without recursing forever.
    if (parentPath is not null && parentPath.Split(':').Contains(PLACEHOLDER)) {
      return earlierKeys;
    }

    return earlierKeys.Append(PLACEHOLDER);
  }
}
